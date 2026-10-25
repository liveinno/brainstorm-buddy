using System.Text;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Извлечение фактов из прикреплённого документа в память (Core-уровень;
/// воркер-петля AttachmentFactWorker живёт в приложении).
///
/// Пайплайн = тот же, что у транскрипта: TranscriptFactExtractor.BuildPrompt →
/// IApiClient.AskAsync (background, дедлайн 120с на запрос) → ParseOutput →
/// MemoryWriter.ApplyAsync. Текст документа — склейка сегментов segments.json,
/// режется частями ≤ 10 000 символов БЕЗ перекрытия; суммарный лимит задания
/// настраиваемый (по умолчанию 700 000) — при превышении задание уходит в Error
/// «превышен лимит», молчаливого обрезания нет. Ответ, урезанный max_tokens
/// (finish=length), не повторяется «как есть»: часть делится пополам и
/// пересылается уже мелкими кусками; план частей пишется в factjob-*.plan.json,
/// поэтому Interrupted возобновляется без повторной отправки обработанного.
/// Прогресс пишется в fact_jobs после каждой части.
///
/// Из документа разрешены только новые факты: директивы Update/Forget из
/// ответа LLM игнорируются — вложение не может менять/удалять память.
/// Review-режим (<paramref name="reviewMode"/> или job.VerifyMode == "review") →
/// факты пишутся Pending (кандидаты), не «сохранено».
/// </summary>
public sealed class AttachmentFactExtractor
{
    private const string LogCategory = "Attach";
    private const int MaxChunkChars = 10_000;
    /// <summary>Минимальный размер части: обрезанный ответ на тексте ≤ этого
    /// объёма считается окончательным провалом части, а не поводом делить дальше.</summary>
    private const int MinPartChars = 2_500;
    /// <summary>Суммарный лимит символов задания по умолчанию (на ОДИН документ).
    /// Боевой путь — maxTotalCharsSource (Config.Attachments.FactExtractMaxChars).</summary>
    private const int DefaultMaxTotalChars = 700_000;
    /// <summary>Бюджет токенов на запрос по умолчанию (если maxTokensSource не задан).
    /// 8000: reasoning-модель сжигала 4000 на скрытые размышления → finish=length,
    /// пустой content («пустой ответ memory-модели»). Боевой путь — Config.Memory.ExtractMaxTokens.</summary>
    private const int DefaultExtractMaxTokens = 8000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly IApiClient _api;
    private readonly IMemoryStore _store;
    private readonly LoggingService? _log;
    private readonly Func<string?>? _rulesSource;
    private readonly MemoryConsolidator? _consolidator;
    private readonly Func<int>? _maxTokensSource;
    private readonly Func<int>? _maxTotalCharsSource;
    private readonly Func<string>? _intensitySource;
    /// <summary>Внешний учёт расхода фонового извлечения (§8.1): один вызов на
    /// реальный LLM-запрос. Null → счётчик токенов не ведётся.</summary>
    private readonly Action<AskResult>? _onUsage;
    /// <param name="rulesSource">Источник доп. правил промпта — обычно
    /// () => Config.Memory.TranscriptExtractRules; null → без пользовательских правил.</param>
    /// <param name="consolidator">Консолидатор памяти для дедупа кандидатов
    /// (точные + embedding-дубли); null → только точный нормализованный дедуп.</param>
    /// <param name="maxTokensSource">Бюджет ответа модели на одну часть — обычно
    /// () => Config.Memory.ExtractMaxTokens; null → <see cref="DefaultExtractMaxTokens"/>.</param>
    /// <param name="maxTotalCharsSource">Лимит символов задания на один документ
    /// (Attachments.FactExtractMaxChars); null → <see cref="DefaultMaxTotalChars"/>.</param>
    /// <param name="intensitySource">Интенсивность извлечения §6.4 —
    /// () =&gt; Config.Memory.ExtractionIntensity ("low"|"medium"|"high").</param>
    /// <param name="onUsage">Внешний учёт расхода (§8.1 аудита 15.10): вызывается
    /// РОВНО один раз на РЕАЛЬНЫЙ запрос LLM — фоновый spend «Документы и память»
    /// уходит в отдельный счётчик, а не в «Потрачено за встречу». Повторы частей
    /// (обрезка/возобновление) учтутся как новые запросы. null → без учёта.</param>
    public AttachmentFactExtractor(IApiClient api, IMemoryStore store,
        LoggingService? logger = null, Func<string?>? rulesSource = null,
        MemoryConsolidator? consolidator = null, Func<int>? maxTokensSource = null,
        Func<int>? maxTotalCharsSource = null, Func<string>? intensitySource = null,
        Action<AskResult>? onUsage = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = logger;
        _rulesSource = rulesSource;
        _consolidator = consolidator;
        _maxTokensSource = maxTokensSource;
        _maxTotalCharsSource = maxTotalCharsSource;
        _intensitySource = intensitySource;
        _onUsage = onUsage;
    }

    /// <summary>
    /// Выполнить задание извлечения до терминального статуса и вернуть финальный
    /// FactJob (каждый апдейт уже записан через <see cref="AttachmentStore.UpdateFactJob"/>).
    /// <paramref name="memMeetingId"/> — id встречи в memory.db (long!): факты
    /// уходят в scope Meeting с этим id; без него (память выключена/встреча не
    /// создана) факты пишутся в Global — документ живёт вне mem_meetings.
    /// <paramref name="onJobUpdated"/> вызывается после КАЖДОГО persist прогресса
    /// (на фон-потоке воркера — подписчик сам маршалит на UI): чип/тост/карточка
    /// Memory Center показывают живой «запрос N/M», а не только терминал.
    /// Возобновление: Interrupted-задание доезжает с RequestsDone — уже
    /// обработанные части НЕ отправляются повторно (счётчики сохранены); дополнительная
    /// страховка от дублей — точный/embedding-дедуп в MemoryWriter для случая,
    /// когда часть упала между ApplyAsync и Persist.
    /// </summary>
    public async Task<FactJob> RunAsync(FactJob job, AttachmentStore attachments,
        long? memMeetingId = null, string? reviewMode = null, CancellationToken ct = default,
        Action<FactJob>? onJobUpdated = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(attachments);

        var running = job with { Status = FactJobStatus.Running, Error = null };
        Persist(attachments, running);

        FactJob Finish(FactJobStatus status, string? error = null)
        {
            running = running with { Status = status, Error = error };
            Persist(attachments, running);
            return running;
        }

        // Аренда против гонки удаления: документ нельзя стереть, пока идёт чтение.
        // Внутри try — занятость документа сама становится статусом Error задания.
        try
        {
            using var lease = attachments.AcquireDocument(job.DocumentId);
            var doc = attachments.Get(job.DocumentId);
            if (doc == null)
                return Finish(FactJobStatus.Error, $"документ {job.DocumentId} не найден");
            if (doc.Deleted)
                return Finish(FactJobStatus.Error, "документ удалён");

            var segs = await attachments.LoadSegmentsAsync(job.DocumentId, ct).ConfigureAwait(false);
            // Заглушки пустых страниц не несут фактов и не должны сдвигать
            // локаторы цитат в склеенном тексте.
            var factSegs = segs.Where(s => s.Anchor.Kind != AttachmentPageMarkers.EmptyKind).ToList();
            var text = string.Concat(factSegs.Select(s => s.Text));
            if (text.Length == 0)
                return Finish(FactJobStatus.NoFacts, "нет извлечённого текста");

            var maxTotal = Math.Max(MinPartChars, _maxTotalCharsSource?.Invoke() ?? DefaultMaxTotalChars);
            if (text.Length > maxTotal)
                return Finish(FactJobStatus.Error,
                    $"превышен лимит извлечения фактов на один документ: {text.Length} > {maxTotal} символов — разделите файл (в чат документ прикрепляется)");

            // План частей переживает Interrupted (factjob-*.plan.json): адаптивная
            // делёжка обрезанных частей не ломает возобновление по RequestsDone.
            var layout = DocLayout.Build(factSegs);
            var partRanges = new List<DocRange>();
            var plan = (await attachments.LoadFactJobPlanAsync(job.Id, job.DocumentId, ct)
                            .ConfigureAwait(false))?.ToList();
            if (plan == null || plan.Count == 0)
            {
                plan = SplitChunksWithSpans(text, MaxChunkChars)
                    .Select(c => new AttachmentStore.FactPlanItem(c.Text, false, c.Start, c.End))
                    .ToList();
                await attachments.SaveFactJobPlanAsync(job.Id, job.DocumentId, plan, ct)
                    .ConfigureAwait(false);
            }
            // Диапазоны частей в тексте документа: из плана, если он записан с
            // offset'ами; иначе ищем каждую часть по содержимому (план старой версии
            // или подчасти адаптивной делёжки — у тех диапазоны сдвигаются).
            foreach (var p in plan)
                partRanges.Add(FindPartRange(text, p.Text, p.Start, p.End));
            running = running with { RequestsPlanned = plan.Count };
            // Чистый запуск (Queued): счётчики нулевые. Interrupted: сохраняем
            // RequestsDone/CharsSent/FactsSaved и продолжаем с неотправленной части.
            if (running.RequestsDone == 0) running = running with { CharsSent = 0 };
            Persist(attachments, running);

            var startIndex = Math.Clamp(running.RequestsDone, 0, plan.Count);
            if (startIndex > 0)
                _log?.Info($"fact-job {job.Id} doc {job.DocumentId}: resume с части " +
                    $"{startIndex + 1}/{plan.Count} (RequestsDone={job.RequestsDone}, " +
                    $"CharsSent={job.CharsSent}, FactsSaved={job.FactsSaved})", LogCategory);

            var rules = _rulesSource?.Invoke();
            var maxTokens = Math.Max(256, _maxTokensSource?.Invoke() ?? DefaultExtractMaxTokens);
            var intensity = _intensitySource?.Invoke();
            var forcePending = string.Equals(reviewMode ?? job.VerifyMode, "review",
                StringComparison.OrdinalIgnoreCase);
            var writer = new MemoryWriter(_store, _log, _consolidator);

            for (var i = startIndex; i < plan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var part = plan[i];
                if (part.Failed) continue;   // окончательный провал прошлого прогона

                var reqAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var res = await _api.AskAsync(
                        "Извлеки факты из фрагмента документа по инструкциям выше.",
                        TranscriptFactExtractor.BuildPrompt(part.Text, rules, intensity: intensity),
                        maxTokens, new List<ChatMessage>(), ct,
                        background: true, requestTimeout: RequestTimeout)
                    .ConfigureAwait(false);
                // §8.1: учёт РОВНО этого реального запроса (в т.ч. обрезанного —
                // токены за него провайдер уже списал). Дробление части породит
                // новые запросы, каждый учтётся отдельно.
                _onUsage?.Invoke(res);
                var elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(reqAt).TotalMilliseconds;

                if (res.Canceled)
                {
                    if (ct.IsCancellationRequested)
                    {
                        _log?.Info($"fact-job {job.Id} doc {job.DocumentId}: отмена на части " +
                            $"{i + 1}/{plan.Count} после {elapsedMs}ms", LogCategory);
                        ct.ThrowIfCancellationRequested();
                    }
                    // Таймаут запроса (linked CTS клиента) — классифицируем как
                    // сетевую ошибку части, а НЕ как отмену задания.
                    return Finish(FactJobStatus.Error,
                        $"часть {i + 1}/{plan.Count}: модель не ответила вовремя (таймаут запроса {RequestTimeout.TotalSeconds:0}с)");
                }

                // Ответ, обрезанный бюджетом токенов, не повторяем как есть:
                // делим часть пополам и пересылаем мелкими кусками (A1 аудита).
                var truncated = res.Error == null && !res.Canceled
                    && (res.EmptyKind is EmptyAnswerKind.LengthExhausted or EmptyAnswerKind.ProviderCap
                        || string.Equals(res.FinishReason, "length", StringComparison.OrdinalIgnoreCase));
                if (truncated)
                {
                    if (part.Text.Length > MinPartChars)
                    {
                        var subLen = Math.Max(MinPartChars, part.Text.Length / 2);
                        var subChunks = SplitChunksWithSpans(part.Text, subLen);
                        if (subChunks.Count > 1)
                        {
                            _log?.Info($"fact-job {job.Id}: часть {i + 1} обрезана " +
                                $"(finish={res.FinishReason}) — делю на {subChunks.Count} × ≤{subLen} симв.", LogCategory);
                            // Подчасти режут ту же область документа — переносим
                            // диапазоны в координаты полного текста (со сдвигом
                            // Trim'а подобластей), чтобы «Источник» не потерялся.
                            var parent = partRanges[i];
                            var cursor = parent.Start;
                            var subRanges = new List<DocRange>(subChunks.Count);
                            foreach (var sc in subChunks)
                            {
                                var r = FindPartRange(text, sc.Text,
                                    cursor, parent.Start + sc.End);
                                subRanges.Add(r);
                                if (r.End > cursor) cursor = r.End;
                            }
                            plan.RemoveAt(i);
                            plan.InsertRange(i, subChunks.Zip(subRanges, (c, r) =>
                                new AttachmentStore.FactPlanItem(c.Text, false, r.Start, r.End)));
                            partRanges.RemoveAt(i);
                            partRanges.InsertRange(i, subRanges);
                            running = running with { RequestsPlanned = plan.Count };
                            Persist(attachments, running);
                            await attachments.SaveFactJobPlanAsync(job.Id, job.DocumentId, plan, ct)
                                .ConfigureAwait(false);
                            i--; continue;   // та же позиция — первая подчасть
                        }
                    }
                    // Минимальный размер — и всё равно обрезка: часть провалена
                    // окончательно, остальные доезжают; счётчик сдвигаем, чтобы
                    // возобновление не гоняло её по кругу.
                    _log?.Warn($"fact-job {job.Id}: часть {i + 1}/{plan.Count} обрезана " +
                        $"на минимальном размере ({part.Text.Length} симв.) — пропускаем", LogCategory);
                    plan[i] = part with { Failed = true };
                    await attachments.SaveFactJobPlanAsync(job.Id, job.DocumentId, plan, ct)
                        .ConfigureAwait(false);
                    running = running with
                    {
                        RequestsDone = i + 1,
                        CharsSent = running.CharsSent + part.Text.Length,
                    };
                    Persist(attachments, running);
                    continue;
                }

                if (res.Error != null)
                    return Finish(FactJobStatus.Error,
                        $"часть {i + 1}/{plan.Count}: {res.Error}");
                if (string.IsNullOrWhiteSpace(res.Content))
                    return Finish(FactJobStatus.Error,
                        $"часть {i + 1}/{plan.Count}: модель вернула пустой ответ " +
                        $"(finish={res.FinishReason ?? "null"}, kind={res.EmptyKind})");

                // Документ — данные, не команды: исполняем только MEMORY_ADD.
                var dirs = TranscriptFactExtractor.ParseOutput(res.Content ?? "")
                    .Where(d => d.Kind == MemoryDirectiveKind.Add)
                    .ToList();

                // «Источник» факта = место в документе: локатор сегментов
                // («[Стр. 3] — [Стр. 4]»), диапазон символов и дословная цитата
                // фрагмента. Раньше тут был «часть i/N» — индекс нарезки, по
                // которому нельзя было ни открыть документ, ни найти текст.
                var range = partRanges[i];
                var anchor = BuildDocAnchor(layout, text, range, i + 1, plan.Count);
                var opts = new AttachmentApplyOptions(
                    doc.DocumentId, doc.DisplayName, doc.Sha256, anchor, forcePending);
                var applied = await writer
                    .ApplyAsync(dirs, memMeetingId, ct, opts)
                    .ConfigureAwait(false);

                running = running with
                {
                    RequestsDone = i + 1,
                    CharsSent = running.CharsSent + part.Text.Length,
                    FactsSaved = forcePending ? running.FactsSaved : running.FactsSaved + applied.Count,
                    FactsPending = forcePending ? running.FactsPending + applied.Count : running.FactsPending,
                };
                Persist(attachments, running);
            }

            // Задание доехало — план больше не нужен.
            try { await attachments.SaveFactJobPlanAsync(job.Id, job.DocumentId, null, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log?.Warn($"fact_jobs: план job {job.Id} не удалён: {ex.Message}", LogCategory); }

            var failedParts = plan.Count(p => p.Failed);
            if (failedParts > 0)
                return Finish(FactJobStatus.Error,
                    $"обработано {plan.Count - failedParts} из {plan.Count} частей; " +
                    $"{failedParts} ч. не извлеклись — ответ модели упирался в лимит токенов");
            if (running.FactsPending > 0)
                return Finish(FactJobStatus.PendingReview);
            if (running.FactsSaved > 0)
                return Finish(FactJobStatus.Done);
            return Finish(FactJobStatus.NoFacts);
        }
        catch (OperationCanceledException)
        {
            return Finish(FactJobStatus.Canceled);
        }
        catch (Exception ex)
        {
            _log?.Error($"извлечение фактов из {job.DocumentId}: сбой", ex, LogCategory);
            return Finish(FactJobStatus.Error, ex.Message);
        }

        void Persist(AttachmentStore attachments, FactJob job)
        {
            try { attachments.UpdateFactJob(job); }
            catch (Exception ex) { _log?.Warn($"fact_jobs: не удалось записать прогресс job {job.Id}: {ex.Message}", LogCategory); }
            try { onJobUpdated?.Invoke(job); }
            catch (Exception ex) { _log?.Warn($"fact_jobs: подписчик прогресса job {job.Id} упал: {ex.Message}", LogCategory); }
        }
    }

    /// <summary>
    /// Разбиение на части ≤ maxLen без перекрытия; граница — последний абзац
    /// (\n\n) или конец строки в четвёртой четверти части, иначе жёсткий срез.
    /// Хвост Trim'ится по краям — текст не теряется.
    /// </summary>
    internal static List<string> SplitChunks(string text, int maxLen)
        => SplitChunksWithSpans(text, maxLen).Select(c => c.Text).ToList();

    /// <summary>
    /// То же, но с точными границами каждой части в исходном тексте [Start,End).
    /// Нужно для «Источника» факта: MemoryItem.SourceAnchor должен показывать место
    /// в документе (страница + символы + цитата), а не «часть i/N».
    /// </summary>
    internal static List<DocChunk> SplitChunksWithSpans(string text, int maxLen)
    {
        var chunks = new List<DocChunk>();
        var pos = 0;
        while (pos < text.Length)
        {
            var len = Math.Min(maxLen, text.Length - pos);
            if (len == maxLen)
            {
                var window = text.AsSpan(pos, len);
                var cut = window.LastIndexOf("\n\n");
                if (cut < len / 2)
                    cut = window.LastIndexOf('\n');
                if (cut >= len / 2 && cut > 0)
                    len = cut;
            }
            var raw = text.AsSpan(pos, len);
            var lead = 0;
            while (lead < raw.Length && char.IsWhiteSpace(raw[lead])) lead++;
            var tail = raw.Length;
            while (tail > lead && char.IsWhiteSpace(raw[tail - 1])) tail--;
            if (tail > lead)
                chunks.Add(new DocChunk(raw[lead..tail].ToString(), pos + lead, pos + tail));
            pos += len;
        }
        return chunks;
    }

    /// <summary>Часть плана с границами в склеенном тексте сегментов [Start,End).</summary>
    internal readonly record struct DocChunk(string Text, int Start, int End);

    /// <summary>Диапазон символов [Start,End) в склеенном тексте; (0,0) — не определён.</summary>
    internal readonly record struct DocRange(int Start, int End);

    /// <summary>
    /// Раскладка сегментов документа по символам склеенного текста: где какая
    /// страница («Стр. 3», «Слайд 7», «§ Глава», «строки 10–40»). Локатор берётся из
    /// <see cref="DocSegment.Anchor"/> экстракторов — не из догадок по содержимому.
    /// </summary>
    private sealed class DocLayout
    {
        private readonly record struct Seg(int Start, int End, char Sym, string Label);

        private readonly List<Seg> _segs;

        private DocLayout(List<Seg> segs) => _segs = segs;

        public static DocLayout Build(IReadOnlyList<DocSegment> segments)
        {
            var text = new StringBuilder();
            var segs = new List<Seg>();
            foreach (var s in segments)
            {
                var start = text.Length;
                text.Append(s.Text);
                if (s.Text.Length == 0) continue;
                segs.Add(new Seg(start, text.Length, KindSymbol(s.Anchor.Kind), s.Anchor.Label));
            }
            return new DocLayout(segs);
        }

        /// <summary>Локатор диапазона: «[Стр. 3]», «[Стр. 3] — [Стр. 4]» (разные
        /// сегменты), иначе null — если диапазон не накрыт сегментами.</summary>
        public string? Location(int start, int end)
        {
            string? first = null, last = null;
            foreach (var s in _segs)
            {
                if (s.End <= start || s.Start >= end) continue;
                var label = $"{s.Sym}{s.Label}{s.Sym}";
                first ??= label;
                last = label;
            }
            if (first == null) return null;
            return string.Equals(first, last, StringComparison.Ordinal) ? first : $"{first} — {last}";
        }

        /// <summary>Скобка локатора по типу сегмента: страница/слайд/лист — «[]»,
        /// раздел/абзац/строки/таблица — «»» (кавычки), «§»-заголовки сами несут знак.</summary>
        private static char KindSymbol(string kind) => kind switch
        {
            "page" or "slide" or "sheet" => '[',
            "lines" or "table" or "paragraph" => '"',
            "section" => '«',
            _ => '«',
        };
    }

    /// <summary>
    /// «Источник» факта из документа в MemoryItem.SourceAnchor: JSON
    /// {"l":"[Стр. 3] — [Стр. 4]","c":"12000-22000","q":"дословная цитата фрагмента"}.
    /// Memory Center парсит его кнопками «Открыть документ»/«Перейти к реплике».
    /// Legacy-значение «часть i/N» (не локация) не парсится — показывается как есть.
    /// </summary>
    public static class DocAnchor
    {
        /// <summary>Разобранный якорь. Location/Start/End/Query — null, если якорь
        /// не разобрался (legacy).</summary>
        public sealed record Parts(string? Location, long? Start, long? End, string? Quote);

        public static string Encode(string? location, long start, long end, string? quote)
        {
            var sb = new StringBuilder("{\"l\":");
            WriteJsonString(sb, location);
            sb.Append(",\"c\":\"").Append(start).Append('-').Append(end).Append('"');
            sb.Append(",\"q\":");
            WriteJsonString(sb, quote);
            sb.Append('}');
            return sb.ToString();
        }

        public static bool TryParse(string? anchor, out Parts parts)
        {
            parts = new Parts(null, null, null, null);
            if (string.IsNullOrEmpty(anchor) || anchor[0] != '{') return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(anchor);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
                string? loc = null, quote = null;
                long? start = null, end = null;
                if (root.TryGetProperty("l", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String)
                    loc = l.GetString();
                if (root.TryGetProperty("q", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.String)
                    quote = q.GetString();
                if (root.TryGetProperty("c", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var dash = c.GetString()?.IndexOf('-') ?? -1;
                    if (dash > 0 && long.TryParse(c.GetString()![..dash], out var s)
                        && long.TryParse(c.GetString()![(dash + 1)..], out var e))
                    { start = s; end = e; }
                }
                if (loc == null && quote == null && start == null) return false;
                parts = new Parts(loc, start, end, quote);
                return true;
            }
            catch (System.Text.Json.JsonException) { return false; }
        }

        /// <summary>Человекочитаемая строка: «[Стр. 3] · сим. 12000-22000» (для карточки).</summary>
        public static string Describe(Parts p)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(p.Location)) sb.Append(p.Location);
            if (p.Start is { } s && p.End is { } e && e > s)
            {
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append("сим. ").Append(s).Append('–').Append(e);
            }
            return sb.ToString();
        }

        private static void WriteJsonString(StringBuilder sb, string? value)
        {
            if (value == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>Диапазон части в склеенном тексте: доверяем планным offset'ам,
    /// если часть в них реально лежит; иначе точный поиск подстроки; иначе
    /// нечёткий (пробелы/переносы строк могли поехать при правках плана).</summary>
    private static DocRange FindPartRange(string text, string part, int start, int end)
    {
        if (string.IsNullOrEmpty(part)) return default;
        if (start >= 0 && end > start && end <= text.Length
            && text.IndexOf(part, start, StringComparison.Ordinal) == start)
            return new DocRange(start, end);
        var exact = text.IndexOf(part, StringComparison.Ordinal);
        if (exact >= 0) return new DocRange(exact, exact + part.Length);
        var fuzzy = LocateFuzzy(text, part);
        return fuzzy.Start >= 0 ? fuzzy : default;
    }

    /// <summary>Нечёткий поиск части: любые пробельные разделители между словами.
    /// Нужен, когда разрез прошёл внутри слова или переноса строки, а план
    /// (или его offset'ы) от этого устарел.</summary>
    private static DocRange LocateFuzzy(string text, string part)
    {
        if (part.Length == 0) return default;
        for (var origin = 0; origin < text.Length; origin++)
        {
            var t = origin;
            while (t < text.Length && char.IsWhiteSpace(text[t])) t++;
            var p = 0;
            var start = -1;
            while (t < text.Length && p < part.Length)
            {
                if (char.IsWhiteSpace(text[t]))
                {
                    while (t < text.Length && char.IsWhiteSpace(text[t])) t++;
                    while (p < part.Length && char.IsWhiteSpace(part[p])) p++;
                    continue;
                }
                if (text[t] != part[p]) break;
                if (start < 0) start = t;
                t++;
                p++;
            }
            if (p == part.Length) return new DocRange(start, t);
        }
        return default;
    }

    /// <summary>Максимум дословной цитаты в «Источнике» (длинные части режем).</summary>
    private const int QuoteMaxChars = 600;

    /// <summary>
    /// SourceAnchor факта из документа: локатор сегментов + диапазон символов +
    /// цитата фрагмента. Если диапазон не определён (часть не нашлась даже
    /// нечётко) — в локатор уходит «часть i/N»: лучше устаревший индекс, чем пусто.
    /// </summary>
    private static string BuildDocAnchor(DocLayout layout, string text, DocRange range,
        int partNumber, int totalParts)
    {
        string? location = null;
        long start = 0, end = 0;
        string? quote = null;
        if (range.Start >= 0 && range.End > range.Start && range.End <= text.Length)
        {
            location = layout.Location(range.Start, range.End);
            start = range.Start;
            end = range.End;
            var len = Math.Min(QuoteMaxChars, range.End - range.Start);
            quote = text.Substring(range.Start, len);
            if (len < range.End - range.Start) quote += "...";
        }
        return DocAnchor.Encode(
            location ?? $"часть {partNumber}/{totalParts}", start, end, quote);
    }
}
