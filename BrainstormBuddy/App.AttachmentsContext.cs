using BrainstormBuddy.Ai;
using BrainstormBuddy.Context;
using BrainstormBuddy.Core.Attachments;

namespace BrainstormBuddy;

/// <summary>
/// Маршрутизация и сборка контекста вложений для ask-путей (§4 задачи 16.10):
/// router → Clarify (немедленный ответ) / WholeDocument (map-reduce
/// <see cref="WholeDocumentQa"/>) / Pages+General (<see cref="AttachmentContextBuilder"/>
/// с токен-бюджетом планировщика).
/// </summary>
public partial class App
{
    /// <summary>
    /// Итог маршрутизации вопроса по вложениям.
    /// <paramref name="Payload"/> — готовый блок для <c>attachmentsContext</c>
    /// в AskAsync (null/empty — вложений в запросе нет).
    /// <paramref name="ImmediateAnswer"/> — ответ без LLM-прохода контекста
    /// (уточнение неоднозначного имени документа). Фактический расход
    /// map/reduce-вызовов уже учтён через AccountUsage внутри метода.
    /// </summary>
    internal sealed record AttachmentRouteResult(
        string? Payload,
        string? ImmediateAnswer);

    /// <summary>
    /// Сегменты грузятся лениво только для документов, которые реально попадут
    /// в контекст (раньше читались все).
    /// При переиспользовании сегментов вопрос маршрутизируется:
    /// Clarify → ImmediateAnswer с именами-конкурентами;
    /// WholeDocument → полный map-reduce всех страниц (не выборка);
    /// Pages → жёсткий скоуп на документ + буст адресов в скоринге;
    /// General → приоритет явно названного документа (PreferredDocumentId).
    /// Бюджет — от планировщика контекста: InputBudget модели минус резерв
    /// ответа и уже занятые токены (никаких «символы = токены»).
    /// </summary>
    internal async Task<AttachmentRouteResult?> BuildAttachmentRouteAsync(
        string question, ContextPlan? plan, CancellationToken ct)
    {
        var store = _attachmentStore;
        var meetingId = MeetingJournal?.CurrentId;
        if (store == null || meetingId == null) return null;

        var metas = store.ListMeetingDocuments(meetingId)
            .Where(m => !m.Doc.Deleted).Select(m => m.Doc).ToList();
        if (metas.Count == 0) return null;
        var route = AttachmentQueryRouter.Route(question, metas);

        // ── Неоднозначность: короткий уточняющий ответ вместо смешивания ──
        if (route.Kind == AttachmentQueryKind.Clarify)
        {
            var names = string.Join("\n", route.Candidates.Take(5)
                .Select(d => $"• {d.DisplayName}"));
            var more = route.Candidates.Count > 5
                ? $"\n…и ещё {route.Candidates.Count - 5}" : "";
            return new AttachmentRouteResult(null,
                TT("L_Att_ClarifyWhich", "Уточните, пожалуйста, о каком документе вопрос:") + "\n" + names + more);
        }

        var scoped = route.Kind is AttachmentQueryKind.Pages or AttachmentQueryKind.WholeDocument
            ? metas.Where(d => d.DocumentId == route.Document?.DocumentId).ToList()
            : metas;

        // ── Токен-бюджет общий с планировщиком контекста ──
        var inputBudget = plan?.InputBudget ?? ContextBudgetResolver.InputBudget(
            Config.Api.ChatModel, Config.Api.BaseUrl);
        var reserve = Math.Min(Config.Advanced.MaxResponseTokens, 25000)
            + MeetingContextManager.ReserveSlack;
        // EstimatedInputTokens уже включает вопрос (MeetingContext.BuildPlan) —
        // дважды не считаем; плана нет → оцениваем хотя бы вопрос.
        var usedTokens = plan?.EstimatedInputTokens
            ?? ContextBudgetResolver.EstimateTokens(question);
        var docs = new List<(StoredDocument Doc, IReadOnlyList<DocSegment> Segments)>(scoped.Count);
        foreach (var meta in scoped)
        {
            ct.ThrowIfCancellationRequested();
            var segs = await store.LoadSegmentsAsync(meta.DocumentId, ct).ConfigureAwait(false);
            if (segs.Count > 0) docs.Add((meta, segs));
        }
        // Для адресованного вопроса пустые сегменты — это данные для модели:
        // ниже будет добавлена явная отметка непрочитанных страниц. Для общего
        // запроса сохраняем безопасный прежний фолбэк без вложений.
        if (docs.Count == 0 && route.Kind != AttachmentQueryKind.Pages) return null;

        // ── Вопрос ко всему документу: реальный map-reduce по всем страницам ──
        if (route.Kind == AttachmentQueryKind.WholeDocument)
        {
            var (doc, segs) = docs[0];
            // Один вызов LLM ≤ свободное окно в символах (≈4 симв/токен —
            // та же инверсия, что у AttachmentContextBuilder).
            var maxInputChars = Math.Max(WholeDocumentQa.MinPartChars,
                (int)Math.Min((long)(inputBudget - reserve - usedTokens) * 4, 384_000));
            var mapTokens = Math.Min(Config.Advanced.MaxResponseTokens, 1500);
            // Каждый реальный LLM-вызов фиксируется в счётчике встречи —
            // EstimatedTokens не подменяет фактический usage. Прогресс — через
            // Dispatcher: речевой caller выполняется не на UI-потоке.
            var progress = new Progress<WholeDocProgress>(pr =>
                Dispatcher.InvokeAsync(() => _overlay?.SetContextInfo(
                    string.Format(TT("L_Att_WholeDocFmt", "Документ: {0} {1}/{2}"),
                        pr.Stage == "map" ? TT("L_Att_WholeDocMap", "читаю") : TT("L_Att_WholeDocReduce", "свожу"),
                        pr.Done, pr.Total), true, showProvidedText: true)));

            WholeDocResult result;
            try
            {
                result = await WholeDocumentQa.RunAsync(
                    question, doc.DisplayName, segs, maxInputChars, mapTokens,
                    callAsync: async (userText, maxTok, c) =>
                    {
                        var r = await ApiClient.AskAsync(userText,
                            Config.Advanced.SystemPrompt, maxTok, new List<ChatMessage>(), c,
                            requestTimeout: TimeSpan.FromMinutes(4));
                        AccountUsage(r, background: true);
                        return string.IsNullOrWhiteSpace(r?.Content) ? null : r.Content;
                    },
                    progress: progress, ct: ct).ConfigureAwait(false);
            }
            finally
            {
                // Индикатор — про конкретный map-reduce: после ответа/отмены он
                // не должен висеть «свожу 1/1» до следующего вопроса.
                _ = Dispatcher.InvokeAsync(() => _overlay?.SetContextInfo(null, false));
            }

            // Отмена обязана остановить финальный ask, как и map/reduce.
            if (result.Canceled)
                ct.ThrowIfCancellationRequested();

            var coverage = FormatUncovered(result.UncoveredRanges, doc.DisplayName);
            // Частичный прогон честно помечается — «прогнан целиком» только
            // при завершённой сводке.
            var header = result.Reduced
                ? string.Format(TT("L_Att_WholeDocPayloadFmt",
                    "[КОНСПЕКТ ДОКУМЕНТА «{0}»: прогнан целиком, частей {1}/{2}]"),
                    doc.DisplayName, result.MappedParts, result.TotalParts)
                : string.Format(TT("L_Att_WholeDocPartialFmt",
                    "[ЧАСТИЧНЫЙ конспект документа «{0}»: частей {1}/{2}, сводка не завершена]"),
                    doc.DisplayName, result.MappedParts, result.TotalParts);
            var payload = AttachmentContextBuilder.SerializeForUserMessage(
                new AttachmentContext(header + "\n" + result.Digest + coverage,
                    [new AttachmentSourceRef(doc.DocumentId, doc.DisplayName,
                        TT("L_Att_WholeDocAnchor", "весь документ"))],
                    1, 1));
            Logger.Info($"Attach whole-doc '{doc.DisplayName}': parts {result.MappedParts}/{result.TotalParts}, " +
                $"uncovered={result.UncoveredRanges.Count}, reduced={result.Reduced}, canceled={result.Canceled}", "Attach");
            return new AttachmentRouteResult(payload, null);
        }

        // ── Pages / General: чанк-бюджет от планировщика ──
        var isPages = route.Kind == AttachmentQueryKind.Pages;

        // Документы, извлечённые до версии 3, не содержат адресуемых заглушек
        // пустых страниц: вопрос «что на стр. 4» получал «текст не извлечён».
        // Проверяем только запрошенные страницы по исходному PDF (без OCR) и
        // добавляем заглушку, если страница действительно пуста. Новые
        // документы сюда не попадают — у них уже есть page-empty сегменты.
        if (isPages && route.Document is { } pagesDoc)
        {
            var docIndex = docs.FindIndex(d => string.Equals(d.Doc.DocumentId,
                pagesDoc.DocumentId, StringComparison.OrdinalIgnoreCase));
            if (docIndex >= 0)
            {
                var segs = docs[docIndex].Segments.ToList();
                var covered = new HashSet<int>();
                foreach (var seg in segs)
                    if (AttachmentContextBuilder.TryGetPageNumber(seg.Anchor.Label, out var n))
                        covered.Add(n);
                var requested = route.Pages
                    .SelectMany(r => Enumerable.Range(r.From,
                        Math.Min(r.To - r.From + 1, 2000)))
                    .Where(p => !covered.Contains(p)).Distinct().Take(20).ToList();
                if (requested.Count > 0)
                {
                    var sourcePath = store.GetSourcePath(pagesDoc);
                    var blank = await Task.Run(
                        () => PdfPageInspector.FindBlankPages(sourcePath, requested, ct), ct)
                        .ConfigureAwait(false);
                    foreach (var page in blank.OrderBy(p => p))
                    {
                        segs.Add(new DocSegment(AttachmentPageMarkers.EmptyText(page),
                            new TextAnchor(AttachmentPageMarkers.EmptyKind,
                                AttachmentPageMarkers.EmptyLabel(page)), page));
                        Logger.Info($"Attach '{pagesDoc.DisplayName}': page {page} is blank (legacy extraction)",
                            "Attach");
                    }
                    if (blank.Count > 0) docs[docIndex] = (docs[docIndex].Doc, segs);
                }
            }
        }

        // Если ВСЕ запрошенные страницы подтверждённо пусты, LLM не нужен:
        // отвечаем прямо, иначе модель иногда пересказывает соседние страницы
        // или говорит «информации нет» без причины. Смешанный диапазон
        // (пустая + текстовая) по-прежнему уходит в LLM.
        if (isPages && route.Document is { } blankDoc && route.Pages.Count > 0)
        {
            var requestedPages = route.Pages
                .SelectMany(r => Enumerable.Range(r.From, Math.Min(r.To - r.From + 1, 2000)))
                .Distinct().OrderBy(p => p).ToList();
            var emptyPages = new HashSet<int>();
            var docSegs = docs.FirstOrDefault(d => string.Equals(d.Doc.DocumentId,
                blankDoc.DocumentId, StringComparison.OrdinalIgnoreCase)).Segments;
            if (docSegs != null)
                foreach (var seg in docSegs)
                    if (seg.Anchor.Kind == AttachmentPageMarkers.EmptyKind
                        && AttachmentContextBuilder.TryGetPageNumber(seg.Anchor.Label, out var n))
                        emptyPages.Add(n);
            if (requestedPages.Count > 0 && requestedPages.All(emptyPages.Contains))
            {
                var pagesText = string.Join(", ", requestedPages.Take(20));
                var answer = requestedPages.Count == 1
                    ? string.Format(TT("L_Att_BlankPageFmt",
                        "Страница {0} документа «{1}» пустая: текста и изображений на ней нет."),
                        requestedPages[0], blankDoc.DisplayName)
                    : string.Format(TT("L_Att_BlankPagesFmt",
                        "Страницы {0} документа «{1}» пустые: текста и изображений на них нет."),
                        pagesText, blankDoc.DisplayName);
                return new AttachmentRouteResult(null, answer);
            }
        }

        var ctx = AttachmentContextBuilder.Build(question, docs,
            new AttachmentContextBuildOptions(
                PreferredDocumentId: route.Document?.DocumentId,
                OnlyDocumentId: isPages ? route.Document?.DocumentId : null,
                InputBudgetTokens: inputBudget,
                ReservedTokens: reserve,
                UsedTokens: usedTokens,
                // Адреса страниц от роутера (в т.ч. позиционные «последняя
                // страница» — числом в тексте их нет) получают доминирующий буст.
                ExplicitPages: isPages ? route.Pages : null));
        var basePayload = AttachmentContextBuilder.SerializeForUserMessage(ctx);
        if (route.MissingPages.Count > 0)
            basePayload += "\n" + string.Format(
                TT("L_Att_MissingPagesFmt", "[в документе нет страниц: {0}]"),
                string.Join(", ", route.MissingPages.Take(20)));
        // Запрошенные страницы без извлечённого текста (Fast-пропуск слоя,
        // ошибка чтения/OCR, остановка): честная заметка вместо ответа «с потолка».
        if (isPages && route.Pages.Count > 0 && route.Document is { } routeDoc)
        {
            // PageStats.Total может быть заполнен при PageCount=0, когда ни один
            // сегмент не извлечён. Маршрут уже подтверждает адрес страницы.
            var pagedSegs = docs.FirstOrDefault(d => string.Equals(d.Doc.DocumentId,
                routeDoc.DocumentId, StringComparison.OrdinalIgnoreCase)).Segments
                ?? Array.Empty<DocSegment>();
            var coveredPages = new HashSet<int>();
            foreach (var seg in pagedSegs)
                if (AttachmentContextBuilder.TryGetPageNumber(seg.Anchor.Label, out var p))
                    coveredPages.Add(p);
            var unread = route.Pages
                .SelectMany(r => Enumerable.Range(r.From, Math.Min(r.To - r.From + 1, 2000)))
                .Where(p => !coveredPages.Contains(p))
                .Distinct().OrderBy(p => p).Take(20).ToList();
            if (unread.Count > 0)
                basePayload += "\n" + string.Format(
                    TT("L_Att_NoTextPagesFmt", "[текст страниц не извлечён: {0}]"),
                    string.Join(", ", unread));
        }
        Logger.Info($"Attachment context: {ctx.DocumentsIncluded}/{ctx.DocumentsRequested} docs, " +
            $"{basePayload.Length} chars, route={route.Kind}", "Attach");
        return new AttachmentRouteResult(
            basePayload.Length == 0 ? null : basePayload, null);
    }

    /// <summary>
    /// Ошибка дополнительного контекста не должна ломать обычный ask: вопрос без
    /// целевого документа просто идёт без вложений (null). А вот при явном вопросе
    /// к конкретному документу молчаливый пропуск = выдуманный ответ модели —
    /// возвращаем ImmediateAnswer с понятной причиной сбоя.
    /// Отмену запроса передаём вызывающему коду без подавления.
    /// </summary>
    private async Task<AttachmentRouteResult?> BuildAttachmentRouteSafeAsync(
        string question, ContextPlan? plan, CancellationToken ct)
    {
        try
        {
            return await BuildAttachmentRouteAsync(question, plan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"BuildAttachmentRoute failed: {ex.Message}", "Attach");
            var targetName = TryRouteTargetName(question);
            if (targetName == null) return null;
            return new AttachmentRouteResult(null, string.Format(
                TT("L_Att_DocReadErrFmt", "Не удалось обработать документ «{0}»: {1}"),
                targetName, ex.Message));
        }
    }

    /// <summary>
    /// Имя документа, к которому адресован вопрос (роутинг по метаданным, без
    /// загрузки сегментов — работает и при упавшем доступе к контенту).
    /// Null → конкретного документа в вопросе нет, допустим фолбэк на обычный ask.
    /// </summary>
    private string? TryRouteTargetName(string question)
    {
        try
        {
            var store = _attachmentStore;
            var meetingId = MeetingJournal?.CurrentId;
            if (store == null || meetingId == null) return null;
            var metas = store.ListMeetingDocuments(meetingId)
                .Where(m => !m.Doc.Deleted).Select(m => m.Doc).ToList();
            if (metas.Count == 0) return null;
            return AttachmentQueryRouter.Route(question, metas).Document?.DisplayName;
        }
        catch { return null; }
    }

    /// <summary>Честная заметка о недоехавших диапазонах — не замалчиваем ошибки/отмену.</summary>
    private static string FormatUncovered(IReadOnlyList<PageInterval> ranges, string displayName)
    {
        if (ranges.Count == 0) return "";
        var list = string.Join(", ", ranges.Take(12).Select(r => r.ToString()));
        var tail = ranges.Count > 12 ? ",…" : "";
        return "\n\n" + string.Format(
            TT("L_Att_UncoveredFmt", "⚠ Без ответа остались страницы: {0}{1} (документ «{2}»)"),
            list, tail, displayName);
    }
}
