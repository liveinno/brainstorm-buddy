using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Извлечение текста из PDF через PdfPig, постранично (якорь «Стр. N»).
/// Режим <see cref="PdfReadMode.Fast"/> читает только текстовый слой: страницы без
/// него пропускаются с предупреждением, OCR не вызывается вовсе. Режим
/// <see cref="PdfReadMode.Full"/> отправляет страницу без текстового слоя
/// (&lt; <see cref="AttachmentLimits.MinPdfTextLayerChars"/> символов) в локальный
/// OCR через <see cref="IPdfPageOcr"/> (якорь «Стр. N (OCR)»); лимит OCR —
/// <see cref="PdfReadOptions.EffectiveOcrMaxPages"/> страниц, остальные помечаются
/// предупреждением «лимит OCR». Текстового слоя не дублируем: OCR только на
/// страницах без текста. Ошибка OCR одной страницы — страница «не распознана»,
/// документ доезжает. Зашифрованный/битый PDF — отказ.
/// </summary>
internal static class PdfExtractor
{
    public static async Task<ExtractedDocument> ExtractAsync(
        string storedPath, string displayName, IPdfPageOcr? pdfOcr, CancellationToken ct,
        PdfReadOptions? options = null, IProgress<ExtractionProgress>? progress = null)
    {
        var opts = options ?? PdfReadOptions.Default;
        var ocrCap = opts.EffectiveOcrMaxPages;
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(storedPath);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new AttachmentRejectedException(
                "Документ защищён паролем — сохраните незашифрованную копию");
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException
            or System.IO.InvalidDataException or System.IO.IOException)
        {
            throw new AttachmentRejectedException("Не удалось прочитать PDF — файл повреждён");
        }

        using (pdf)
        {
            var pageCount = pdf.NumberOfPages;

            var segments = new List<DocSegment>();
            var warnings = new List<string>();
            var order = 0; var total = 0;
            var noTextPages = 0; var overOcrLimit = 0; var skippedNoLayer = 0;
            var skippedList = new List<int>();   // Fast: стр. без слоя, пропущены
            var noTextList = new List<int>();    // не обработаны: ошибка чтения / OCR недоступен / OCR не смог
            var overCapList = new List<int>();   // не обработаны: лимит OCR
            var blankPages = new List<int>();    // Full: OCR подтвердил пустую страницу
            var needOcr = new List<int>();       // Full: страницы в очереди OCR
            var pageText = new string?[pageCount + 1];   // текст страницы по её номеру
            var ocrAnchored = new bool[pageCount + 1];   // текст получен через OCR
            var done = 0;
            var interrupted = false;
            var reportLock = new object();

            // Страницы, уже извлечённые в прошлом прогоне (повтор после остановки
            // или смены режима): текст не перечитываем, сегменты берём как есть.
            var reused = new Dictionary<int, DocSegment>();
            if (opts.ReuseSegments != null)
            {
                foreach (var s in opts.ReuseSegments)
                {
                    // Пустая страница — тоже доведённый результат: повторный
                    // импорт не должен снова гонять по ней OCR.
                    if (s.Anchor.Kind is not ("page" or AttachmentPageMarkers.EmptyKind)) continue;
                    var label = s.Anchor.Label;
                    const string prefix = "Стр. ";
                    if (!label.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rest = label.Substring(prefix.Length);       // «12» или «12 (OCR)»
                    var sp = rest.IndexOf(' ');
                    if (int.TryParse(sp > 0 ? rest.Substring(0, sp) : rest, out var n)
                        && n >= 1 && n <= pageCount && !reused.ContainsKey(n))
                        reused[n] = s;
                }
            }

            void Report()
            {
                lock (reportLock)
                    progress?.Report(new ExtractionProgress(done, pageCount));
            }

            var ocrUsable = opts.Mode == PdfReadMode.Full && pdfOcr is not null && pdfOcr.IsAvailable;

            Report();
            try
            {
                // Проход 1: текстовый слой. OCR-страницы только ставятся в очередь —
                // их done засчитывается после распознавания, не по индексу.
                for (var i = 1; i <= pageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (reused.ContainsKey(i)) { done++; Report(); continue; }

                    string layer;
                    try
                    {
                        layer = TextSanitizer.Normalize(pdf.GetPage(i).Text);
                    }
                    catch (Exception ex) when (ex is not AttachmentRejectedException
                                               and not OperationCanceledException)
                    {
                        AttachLog.Warn($"PDF стр.{i}: ошибка чтения страницы — {ex.GetType().Name}");
                        // Страница без читаемого слоя: в Full её обязан попробовать
                        // OCR — битый текстовый слой не значит, что на странице
                        // нет изображения с текстом. Кап общий с «пустыми» страницами.
                        if (ocrUsable && needOcr.Count < ocrCap)
                        {
                            needOcr.Add(i);      // done++ — по завершении OCR
                            continue;
                        }
                        if (pdfOcr is not null && pdfOcr.IsAvailable && opts.Mode == PdfReadMode.Full)
                        { overOcrLimit++; overCapList.Add(i); }
                        else { noTextPages++; noTextList.Add(i); }
                        done++; Report();
                        continue;
                    }

                    if (layer.Length >= AttachmentLimits.MinPdfTextLayerChars)
                    {
                        pageText[i] = layer;
                        done++; Report();
                        continue;
                    }

                    // Страница без текстового слоя.
                    if (opts.Mode == PdfReadMode.Fast)
                    {
                        skippedNoLayer++; skippedList.Add(i);
                        done++; Report();
                        continue;
                    }
                    if (ocrUsable && needOcr.Count < ocrCap)
                    {
                        needOcr.Add(i);      // done++ — по завершении OCR
                        continue;
                    }
                    if (pdfOcr is not null && pdfOcr.IsAvailable) { overOcrLimit++; overCapList.Add(i); }
                    else { noTextPages++; noTextList.Add(i); }
                    done++; Report();
                }

                // Проход 2: параллельный OCR очереди. Общий лимит — один семафор
                // на документ (между файлами связывает сериализация runner'а);
                // результат пишется в pageText[i] — порядок сегментов = порядок страниц.
                if (needOcr.Count > 0 && pdfOcr is not null)
                {
                    var degree = Math.Max(1, opts.EffectiveOcrParallelism);
                    using var gate = new SemaphoreSlim(degree);
                    var ocrFailed = new List<int>();
                    var failedLock = new object();
                    var tasks = new List<Task>(needOcr.Count);
                    foreach (var pageNo in needOcr)
                    {
                        var p = pageNo;
                        tasks.Add(Task.Run(async () =>
                        {
                            await gate.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                ct.ThrowIfCancellationRequested();
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                var text = TextSanitizer.Normalize(
                                    await pdfOcr.RecognizePageAsync(storedPath, p - 1, ct)
                                        .ConfigureAwait(false));
                                sw.Stop();
                                // §14: постраничная длительность OCR — ищем причину медленных проходов.
                                AttachLog.Info($"PDF стр.{p}: OCR {sw.ElapsedMilliseconds} мс, {text.Length} симв.");
                                pageText[p] = text;
                                ocrAnchored[p] = true;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                AttachLog.Warn($"PDF стр.{p}: OCR не удался — {ex.GetType().Name}: {ex.Message}");
                                lock (failedLock) ocrFailed.Add(p);
                            }
                            finally { gate.Release(); }
                            lock (reportLock)
                            {
                                done++;
                                progress?.Report(new ExtractionProgress(done, pageCount));
                            }
                        }, CancellationToken.None));
                    }
                    try
                    {
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Отмена внутри очереди: in-flight отменены, ожидавшие — сняты.
                        // Частичные результаты уже в pageText — сохраняем их ниже.
                        if (!opts.KeepPartialOnCancel) throw;
                        interrupted = true;
                    }
                    ocrFailed.Sort();
                    foreach (var p in ocrFailed) { noTextPages++; noTextList.Add(p); }
                    // Пустой OCR-результат: страница без изображений — это
                    // действительно пустая страница (в PDF без текстового слоя),
                    // о ней нужно честно ответить, а не писать «нет текста».
                    // Если изображения есть — OCR не смог, оставляем «не обработано».
                    foreach (var p in needOcr)
                    {
                        if (ocrFailed.Contains(p)) continue;
                        if (pageText[p] is not { Length: > 0 } && !interrupted)
                        {
                            pageText[p] = null;
                            if (!HasImages(pdf, p)) blankPages.Add(p);
                            else { noTextPages++; noTextList.Add(p); }
                        }
                    }
                    if (interrupted)
                    {
                        // Незавершённые OCR-страницы уходят в «не обработаны» — повтор продолжит с них.
                        foreach (var p in needOcr)
                            if (pageText[p] is not { Length: > 0 } && !ocrFailed.Contains(p))
                            { noTextPages++; noTextList.Add(p); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!opts.KeepPartialOnCancel) throw;
                interrupted = true;
            }

            // Сборка сегментов строго в порядке страниц: reuse + свежий текст.
            for (var i = 1; i <= pageCount; i++)
            {
                if (reused.TryGetValue(i, out var seg))
                {
                    SegmentAccumulator.Add(segments, seg.Text, seg.Anchor, ref order, ref total);
                    continue;
                }
                if (pageText[i] is { Length: > 0 } text)
                {
                    SegmentAccumulator.Add(segments, text,
                        new TextAnchor("page", ocrAnchored[i] ? $"Стр. {i} (OCR)" : $"Стр. {i}"),
                        ref order, ref total);
                    continue;
                }
                if (blankPages.Contains(i))
                {
                    // Адресуемая страница-заглушка: запрос «что на стр. N»
                    // получает честный ответ «страница пустая», а не
                    // «текст не извлечён»/«страницы нет».
                    SegmentAccumulator.Add(segments,
                        AttachmentPageMarkers.EmptyText(i),
                        new TextAnchor(AttachmentPageMarkers.EmptyKind,
                            AttachmentPageMarkers.EmptyLabel(i)), ref order, ref total);
                }
            }

            // Заглушки пустых страниц — не текст: статистика и охват считают
            // только реальные page-сегменты.
            var textSegments = segments.Where(s => s.Anchor.Kind == "page").ToList();
            var blankList = segments.Where(s => s.Anchor.Kind == AttachmentPageMarkers.EmptyKind)
                .Select(s => AttachmentContextBuilder.TryGetPageNumber(s.Anchor.Label, out var n) ? n : 0)
                .Where(n => n > 0).Distinct().OrderBy(n => n).ToList();

            // §4: смешанный документ — явный охват «текст X из Y страниц»,
            // чтобы частичный результат не выглядел полным.
            if (textSegments.Count < pageCount && textSegments.Count > 0)
                warnings.Insert(0, $"Текст: {textSegments.Count} из {pageCount} стр.");
            if (blankList.Count > 0)
                warnings.Add($"Пустые страницы (без текста): {blankList.Count} — стр. {Pages(blankList)}");
            var ocrDonePages = textSegments.Count(s => s.Anchor.Label.EndsWith("(OCR)", StringComparison.Ordinal));
            if (ocrDonePages > 0)
                warnings.Add($"OCR: {ocrDonePages} стр.");
            if (skippedNoLayer > 0)
            {
                // Fast не зовёт OCR, но причину пропуска называем сразу: по ней
                // runner решает, можно ли продолжить документ автоматически.
                var reason = pdfOcr is { IsAvailable: false, UnavailableReason: { } r }
                    ? $"; OCR недоступен: {r}"
                    : string.Empty;
                warnings.Add($"OCR: {skippedNoLayer} стр. без текстового слоя пропущены (стр. {Pages(skippedList)}{reason})");
            }
            if (noTextPages > 0)
            {
                var reason = pdfOcr?.IsAvailable == false && pdfOcr.UnavailableReason is { } r
                    ? $" (OCR недоступен: {r})"
                    : string.Empty;
                warnings.Add($"{noTextPages} стр. не обработаны — нет текста{reason}: стр. {Pages(noTextList)}");
            }
            if (overOcrLimit > 0)
                warnings.Add($"{overOcrLimit} стр. без текста не распознаны — лимит OCR ({ocrCap} стр.): стр. {Pages(overCapList)})");

            // Страницы без результата (пропущенные Fast, не распознанные OCR,
            // не дошедшие при остановке) — единый честный счётчик для статуса
            // и повтора. Он же перечислен в предупреждениях выше.
            var blankSet = blankPages.ToHashSet();
            var missingPages = Enumerable.Range(1, pageCount)
                .Where(i => pageText[i] is not { Length: > 0 } && !reused.ContainsKey(i)
                            && !blankSet.Contains(i))
                .ToList();
            if (interrupted)
                warnings.Add(missingPages.Count > 0
                    ? $"Обработка остановлена: прочитано {done} из {pageCount} стр.; без результата: стр. {Pages(missingPages)}"
                    : $"Обработка остановлена: прочитано {done} из {pageCount} стр.");

            if (textSegments.Count == 0)
            {
                if (interrupted && opts.KeepPartialOnCancel)
                {
                    // Остановили до первой страницы — вернуть «пустой» частичный
                    // документ нельзя (нечего сохранять): пробрасываем отмену.
                    throw new OperationCanceledException(ct);
                }
                if (skippedNoLayer > 0)
                {
                    throw new AttachmentRejectedException(
                        $"В PDF нет читаемого текста — {skippedNoLayer} стр. без текстового слоя; " +
                        "включите полный режим чтения PDF (OCR)",
                        retryableWithOcr: true);
                }
                var reason = pdfOcr?.IsAvailable == false && pdfOcr.UnavailableReason is { } r
                    ? $" (OCR недоступен: {r})"
                    : string.Empty;
                throw new AttachmentRejectedException($"В PDF нет читаемого текста{reason}");
            }

            var textLayerDonePages = textSegments.Count - ocrDonePages;
            var emptyPages = Math.Max(0, pageCount - textSegments.Count);
            return new ExtractedDocument(
                segments, total, warnings, interrupted, missingPages.Count,
                TotalPages: pageCount,
                // Пустая страница — доведённый результат (мы знаем, что она пуста),
                // а не «не обработана».
                ProcessedPages: Math.Min(pageCount, textSegments.Count + blankList.Count),
                TextLayerPages: textLayerDonePages,
                OcrPages: ocrDonePages,
                EmptyPages: emptyPages);
        }
    }

    /// <summary>
    /// Есть ли на странице изображения. Пустой OCR без картинок — это
    /// подтверждённо пустая страница; если картинки есть, OCR просто не смог.
    /// Ошибку разбора ресурсов трактуем как «картинки есть»: лучше честное
    /// «не обработано», чем ложное «страница пуста».
    /// </summary>
    private static bool HasImages(PdfDocument pdf, int pageNumber)
    {
        try { return pdf.GetPage(pageNumber).GetImages().Any(); }
        catch { return true; }
    }

    /// <summary>Список номеров страниц для предупреждения: все, если их немного, иначе первые 20 + «…».</summary>
    private static string Pages(List<int> pages)
        => pages.Count <= 20 ? string.Join(", ", pages)
                           : string.Join(", ", pages.Take(20)) + ", …";
}
