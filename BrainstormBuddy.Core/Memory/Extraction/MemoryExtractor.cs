using System.Globalization;
using System.Text;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Extraction;

/// <summary>
/// Memory LLM: по сегментам встречи собирает транскрипт с тайм-кодами,
/// просит строгий JSON (саммари + memories) и мапит ответ в MemoryItem-кандидатов.
///
/// Транскрипт режется на части ≤ <see cref="MaxChunkChars"/> (паттерн
/// AttachmentFactExtractor): один запрос на всю встречу упирался в бюджет ответа
/// (finish=length) и извлечение терялось целиком (R3 аудита 16.10). Обрезка
/// части → часть делится пополам и пересылается мелкими кусками, факты уже
/// разобранных частей сохраняются и дедуплицируются между частями. Битый JSON →
/// ровно ОДИН repair-запрос на часть; повторный провал или обрезка на минимальном
/// размере → часть закрывается с ошибкой, остальные части доезжают (частичный
/// успех лучше полной потери, и одинаковый запрос не повторяется).
/// Транспортная ошибка провайдера → провал всей встречи (ретрайт джобы имеет
/// смысл: провайдер может подняться). Ошибка с префиксом «fatal:» в
/// <see cref="MemoryExtractionResult.Error"/> = детерминированный контент-провал
/// (все части упёрлись в лимит/нечитаемый JSON) — MemoryJobHandlers пробрасывает
/// его как есть, worker гасит джобу без ретраев. Исключения наружу не летят
/// (memory-ошибки не должны ломать звонок). Фильтрация мусорных реплик — на стороне
/// LLM через системный промпт: сюда подаётся ПОЛНЫЙ транскрипт (по частям).
/// </summary>
public sealed class MemoryExtractor : IMemoryExtractionService
{
    private const string LogCategory = "Memory";

    /// <summary>Маркер ошибки «пустой транскрипт» — content-кейс, НЕ ошибка LLM:
    /// хендлер extract завершает такую джобу успехом (null), без ретраев до failed.</summary>
    public const string EmptyTranscriptError = "пустой транскрипт";

    // Бюджет ответа: summary + десятки items. Reasoning-модели сжигают бюджет на
    // скрытые размышления → при 4000 ответ приходил finish=length с пустым content
    // («memory job N failed permanently: пустой ответ memory-модели»).
    // Боевой путь — Config.Memory.ExtractMaxTokens (default 8000) через maxTokensSource.
    private const int DefaultMaxTokens = 8000;

    /// <summary>Максимальный размер части транскрипта на один запрос: 10k символов
    /// укладываются в бюджет ответа (саммари + десятки items) с запасом. Реплики
    /// в транскрипте идут по строкам, поэтому граница реза — конец реплики.</summary>
    private const int MaxChunkChars = 10_000;

    /// <summary>Минимальный размер части: обрезка ответа на тексте ≤ этого объёма —
    /// окончательный провал части (делить дальше нечего), а не повтор того же ask.</summary>
    private const int MinPartChars = 2_500;

    /// <summary>Потолок числа LLM-запросов на одну встречу. Деление обрезанной
    /// части конечно само по себе (останавливается на <see cref="MinPartChars"/>),
    /// но на сверхдлинной встрече «все части обрезаны» даёт 7 запросов на каждую —
    /// это уже непреднамеренный расход, поэтому потолок.</summary>
    private const int MaxRequests = 96;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly IApiClient _api;
    private readonly LoggingService? _logger;
    private readonly Func<int>? _maxTokensSource;
    private readonly Func<string>? _intensitySource;
    /// <summary>Внешний учёт фонового расхода памяти (§8.1): один вызов на
    /// реальный LLM-запрос (каждая часть и каждый repair — даже обрезанный,
    /// токены за него провайдер уже списал). Null → без учёта.</summary>
    private readonly Action<AskResult>? _onUsage;

    /// <param name="intensitySource">Клауза интенсивности §6.4 — обычно
    /// () =&gt; Config.Memory.ExtractionIntensity ("low"|"medium"|"high").</param>
    /// <param name="onUsage">Внешний учёт расхода (§8.1 аудита 15.10): вызов на
    /// каждый РЕАЛЬНЫЙ запрос — фоновый spend «Документы и память» идёт в отдельный
    /// счётчик. null → без учёта.</param>
    public MemoryExtractor(IApiClient api, LoggingService? logger = null,
        Func<int>? maxTokensSource = null, Func<string>? intensitySource = null,
        Action<AskResult>? onUsage = null)
    {
        _api = api;
        _logger = logger;
        _maxTokensSource = maxTokensSource;
        _intensitySource = intensitySource;
        _onUsage = onUsage;
    }

    public async Task<MemoryExtractionResult> ExtractAsync(MemoryMeeting m,
        List<MemorySegment> segs, CancellationToken ct)
    {
        try
        {
            var transcript = BuildTranscript(segs);
            if (transcript.Length == 0)
                return new MemoryExtractionResult("", new List<MemoryItem>(), EmptyTranscriptError);

            var maxTokens = Math.Max(256, _maxTokensSource?.Invoke() ?? DefaultMaxTokens);
            // Уровень интенсивности читается ОДИН раз на старт операции: смена
            // настройки посреди прогона не должна менять промпты у оставшихся
            // частей/подчастей и repair-запросов.
            var intensityClause = MemoryPrompts.IntensityClause(_intensitySource?.Invoke());
            var system = MemoryPrompts.Extract + intensityClause;
            var repairSystem = MemoryPrompts.Repair + intensityClause;

            // Части — изменяемый список: обрезанная часть заменяется подчастями
            // прямо на своей позиции (i-- и та же позиция заново).
            var parts = SplitChunks(transcript, MaxChunkChars);
            var candidates = new List<MemoryItem>();
            var summaries = new List<string>();
            var seenFact = new HashSet<string>(StringComparer.Ordinal);
            var failedParts = new List<string>();
            var okParts = 0;
            var requests = 0;
            string? transportError = null;

            for (var i = 0; i < parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (requests >= MaxRequests)
                {
                    // Потолок: незапрошенные части учитываем как failed — иначе
                    // прогон «тихо» обрезает хвост встречи, а при okParts==0
                    // (всё уперлось в потолок раньше) уходил ложный
                    // «нет частей транскрипта» вместо честного fatal.
                    var skipped = parts.Count - i;
                    _logger?.Warn($"Memory extraction: meeting={m.Id}: потолок {MaxRequests} запросов " +
                        $"исчерпан — остальные части ({skipped}) не запрошены", LogCategory);
                    failedParts.Add($"потолок {MaxRequests} запросов исчерпан — {skipped} част(ей/и) не запрошены");
                    break;
                }

                var ask = await AskPartAsync(PartUserText(m.Title, parts[i], i, parts.Count),
                    system, repairSystem, maxTokens, ct).ConfigureAwait(false);
                requests += ask.Requests;

                if (ask.Transport)
                {
                    // Провайдер/сеть: смысла слать остальные части нет — встреча
                    // упадёт целиком, джоба ретраит джобу (провайдер может подняться).
                    transportError = ask.Error;
                    break;
                }
                if (ask.Error != null)
                {
                    // Контент-провал части (обрезка на мин. размере / пустой ответ /
                    // нечитаемый JSON после repair): часть закрыта, остальные едут.
                    failedParts.Add($"часть {i + 1}/{parts.Count}: {ask.Error}");
                    continue;
                }

                if (ask.Truncated)
                {
                    if (parts[i].Length > MinPartChars)
                    {
                        var subLen = Math.Max(MinPartChars, parts[i].Length / 2);
                        var subs = SplitChunks(parts[i], subLen);
                        if (subs.Count > 1)
                        {
                            _logger?.Info($"Memory extraction: meeting={m.Id}: часть {i + 1}/{parts.Count} " +
                                $"обрезана — делю на {subs.Count} × ≤{subLen} симв.", LogCategory);
                            parts.RemoveAt(i);
                            parts.InsertRange(i, subs);
                            i--;        // та же позиция — первая подчасть
                            continue;
                        }
                    }
                    // Минимальный размер — и всё равно обрезка: часть провалилась
                    // окончательно. Повторять тот же запрос бессмысленно.
                    failedParts.Add($"часть {i + 1}/{parts.Count}: ответ обрезан лимитом токенов " +
                        $"(минимальный размер {parts[i].Length} симв.)");
                    continue;
                }

                // ask.Parsed != null
                okParts++;
                if (!string.IsNullOrWhiteSpace(ask.Parsed!.Summary))
                    summaries.Add(ask.Parsed.Summary);
                foreach (var c in ToCandidates(ask.Parsed.Memories, m.Id))
                {
                    // Дедуп между частями и подчастями: факт, прозвучавший дважды
                    // (граница частей/re-split), не должен попадать в память дважды.
                    var key = TextNormalizer.Normalize(c.Content);
                    if (key.Length == 0 || !seenFact.Add(key))
                        continue;
                    candidates.Add(c);
                }
            }

            if (transportError != null)
                return Fail(transportError);

            if (okParts == 0)
            {
                if (failedParts.Count == 0)
                    return Fail("нет частей транскрипта для извлечения");
                // Ни одна часть не разобралась. Транспорта здесь нет — значит все
                // части уперлись в лимит/нечитаемый JSON: повтор джобой того же
                // запроса ничего не изменит, поэтому «fatal:».
                var head = failedParts[0];
                _logger?.Warn($"Memory extraction: meeting={m.Id}: ни одна часть не разобралась " +
                    $"({failedParts.Count} с ошибкой): {head}", LogCategory);
                return Fail(failedParts.Count == 1
                    ? $"fatal: {head}"
                    : $"fatal: ни одна из частей встречи не разобралась ({failedParts.Count} с ошибкой): {head}");
            }

            if (failedParts.Count > 0)
                _logger?.Warn($"Memory extraction degraded: meeting={m.Id}, разобрано частей " +
                    $"{okParts}/{parts.Count}, с ошибкой {failedParts.Count}: {failedParts[0]}", LogCategory);

            _logger?.Info($"Memory extraction done: meeting={m.Id}, items={candidates.Count}" +
                (parts.Count > 1 ? $", частей с фактами {okParts}/{parts.Count}" : ""), LogCategory);
            return new MemoryExtractionResult(string.Join("\n", summaries), candidates, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Error("Memory extraction failed", ex, LogCategory);
            return Fail(ex.Message);
        }

        MemoryExtractionResult Fail(string error)
        {
            _logger?.Warn($"Memory extraction error: {error}", LogCategory);
            return new MemoryExtractionResult("", new List<MemoryItem>(), error);
        }
    }

    /// <summary>
    /// Один запрос по части транскрипта + ровно один repair при битом JSON.
    /// Usage (<see cref="_onUsage"/>) считается на КАЖДЫЙ реальный запрос.
    /// Отмена пробрасывается наружу (<see cref="OperationCanceledException"/>).
    /// </summary>
    private async Task<PartAsk> AskPartAsync(string userText, string system, string repairSystem,
        int maxTokens, CancellationToken ct)
    {
        var resp = await _api.AskAsync(userText, system, maxTokens, new List<ChatMessage>(), ct,
            background: true, requestTimeout: RequestTimeout).ConfigureAwait(false);
        // Отмена при живом токене — только сторона клиента (редкий путь чужой
        // реализации): OpenAiClient всегда отменяется через ct, поэтому такой
        // ответ трактуем как контент-провал части, а не как запрос отмены.
        if (resp.Canceled && !ct.IsCancellationRequested)
            return new PartAsk(null, false, false, "memory model: запрос отменён клиентом", 1);
        if (resp.Canceled) ct.ThrowIfCancellationRequested();
        _onUsage?.Invoke(resp);

        if (resp.Error != null)
            return new PartAsk(null, false, true, $"memory model: {resp.Error}", 1);

        // Обрезка бюджета ответа — не «плохой JSON», а сигнал делить часть:
        // проверяем ДО парса (обрезанный JSON всё равно не читается, а repair на
        // нём — лишний оплаченный запрос). Пустой content при finish=length —
        // типичный исход reasoning-модели, тот же случай.
        if (resp.WasTruncated
            || string.Equals(resp.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
            || resp.EmptyKind is EmptyAnswerKind.LengthExhausted or EmptyAnswerKind.ProviderCap)
            return new PartAsk(null, true, false, null, 1);

        if (string.IsNullOrWhiteSpace(resp.Content))
            return new PartAsk(null, false, false,
                $"memory model: пустой ответ (finish={resp.FinishReason ?? "null"}, kind={resp.EmptyKind})", 1);

        var parsed = ExtractionJson.Parse(resp.Content);
        if (parsed.Ok)
            return new PartAsk(parsed, false, false, null, 1);

        // 1 retry с repair-инструкцией; повторный фейл → часть закрыта с ошибкой.
        // Repair получает ту же клаузу интенсивности (§6.4): она применяется
        // к каждому extraction-запросу, а запрет выдумывания в промпте остаётся.
        var repair = await _api.AskAsync(resp.Content ?? "", repairSystem, maxTokens,
            new List<ChatMessage>(), ct, background: true, requestTimeout: RequestTimeout).ConfigureAwait(false);
        // Отмена со стороны клиента при живом ct (как в основном запросе выше) —
        // контент-провал части; usage не считаем до разбора отмены.
        if (repair.Canceled && !ct.IsCancellationRequested)
            return new PartAsk(null, false, false, "memory model (repair): запрос отменён клиентом", 2);
        if (repair.Canceled) ct.ThrowIfCancellationRequested();
        _onUsage?.Invoke(repair);
        if (repair.Error != null)
            return new PartAsk(null, false, true, $"memory model (repair): {repair.Error}", 2);
        if (string.IsNullOrWhiteSpace(repair.Content))
            return new PartAsk(null, false, false,
                $"memory model (repair): пустой ответ (finish={repair.FinishReason ?? "null"}, kind={repair.EmptyKind})", 2);
        parsed = ExtractionJson.Parse(repair.Content);
        if (parsed.Ok)
            return new PartAsk(parsed, false, false, null, 2);
        return new PartAsk(null, false, false, $"extraction JSON не читается после repair: {parsed.Error}", 2);
    }

    /// <summary>
    /// User-текст запроса: заголовок с названием встречи и номером части
    /// (модель понимает, что summary у неё частичный) + сам кусок транскрипта.
    /// </summary>
    private static string PartUserText(string? title, string partText, int index, int count) =>
        $"ТРАНСКРИПТ ВСТРЕЧИ{(string.IsNullOrWhiteSpace(title) ? "" : $" «{title}»")}" +
        (count > 1 ? $" (часть {index + 1}/{count})" : "") + $":\n\n" + partText;

    /// <summary>
    /// Разбиение на части ≤ maxLen без перекрытия; граница — последний абзац
    /// (\n\n) или конец строки в четвёртой четверти части, иначе жёсткий срез.
    /// Хвост Trim'ится по краям — текст не теряется. Тот же паттерн, что
    /// AttachmentFactExtractor.SplitChunks (документы режутся своим экземпляром).
    /// </summary>
    internal static List<string> SplitChunks(string text, int maxLen)
    {
        var chunks = new List<string>();
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
            var chunk = text.AsSpan(pos, len).Trim();
            if (chunk.Length > 0)
                chunks.Add(chunk.ToString());
            pos += len;
        }
        return chunks;
    }

    /// <summary>
    /// Исход запроса по одной части транскрипта.
    /// <see cref="Transport"/> — ошибка провайдера/сети (не модель), незачем
    /// слать остальные части. <see cref="Truncated"/> — ответ обрезан бюджетом,
    /// часть нужно делить. <see cref="Error"/> — контент-провал части.
    /// <see cref="Requests"/> — сколько реальных LLM-запросов сделано (1 или 2).
    /// </summary>
    private sealed record PartAsk(ExtractionParseResult? Parsed, bool Truncated, bool Transport,
        string? Error, int Requests);

    /// <summary>Транскрипт вида «[S1 00:12:03] текст»; без метки спикера — Role.</summary>
    internal static string BuildTranscript(List<MemorySegment> segs)
    {
        var sb = new StringBuilder(segs.Count * 80);
        foreach (var s in segs)
        {
            var text = (s.Text ?? "").Replace("\r", " ").Replace('\n', ' ').Trim();
            // Маркер источника OCR ([screen]) — служебная метка, а не часть реплики:
            // из промпта убираем, чтобы не утекала в контент фактов.
            if (text.StartsWith("[screen]", StringComparison.OrdinalIgnoreCase))
                text = text["[screen]".Length..].TrimStart();
            if (text.Length == 0) continue;
            var who = string.IsNullOrWhiteSpace(s.Speaker)
                ? (string.IsNullOrWhiteSpace(s.Role) ? "S?" : s.Role)
                : s.Speaker!.Trim();
            var stamp = s.StartMs is { } ms ? FormatMs(ms) : "--:--:--";
            sb.Append('[').Append(who).Append(' ').Append(stamp).Append("] ").Append(text).Append('\n');
        }
        return sb.ToString();
    }

    private static string FormatMs(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.Hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes:00}:{t.Seconds:00}");
    }

    /// <summary>DTO → MemoryItem-кандидаты; кладём provenance (meeting/ms/speaker).</summary>
    internal static List<MemoryItem> ToCandidates(List<ParsedMemory> memories, long meetingId)
    {
        var list = new List<MemoryItem>(memories.Count);
        foreach (var p in memories)
        {
            var content = (p.Content ?? "").Trim();
            if (content.Length == 0) continue;                 // пустой факт не сохраняем
            if (p.SourceStartMs == null && p.SourceEndMs == null)
                continue;                                      // provenance обязателен
            list.Add(new MemoryItem
            {
                ScopeType = MemoryScopeType.Global,
                Type = MapType(p.Type),
                Content = content,
                Importance = Clamp01(p.Importance ?? 0.5),
                Confidence = Clamp01(p.Confidence ?? 0.5),
                Status = MemoryItemStatus.Active,
                Source = "extraction",
                SourceMeetingId = meetingId,
                SourceStartMs = p.SourceStartMs,
                SourceEndMs = p.SourceEndMs,
                Speaker = string.IsNullOrWhiteSpace(p.SpeakerId) ? null : p.SpeakerId.Trim()
            });
        }
        return list;
    }

    /// <summary>Тип из JSON → MemoryItemType; русские/английские алиазы, fallback Fact.</summary>
    internal static MemoryItemType MapType(string? type)
    {
        switch ((type ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_'))
        {
            case "decision":
            case "решение":
                return MemoryItemType.Decision;
            case "action":
            case "action_item":
            case "actionitem":
            case "todo":
            case "task":
            case "задача":
            case "действие":
                return MemoryItemType.ActionItem;
            case "deadline":
            case "дедлайн":
            case "срок":
                return MemoryItemType.Deadline;
            case "open_question":
            case "openquestion":
            case "question":
            case "вопрос":
            case "открытый_вопрос":
                return MemoryItemType.OpenQuestion;
            case "risk":
            case "риск":
                return MemoryItemType.Risk;
            case "person":
            case "person_fact":
            case "personfact":
            case "персона":
            case "человек":
                return MemoryItemType.PersonFact;
            case "project":
            case "project_fact":
            case "projectfact":
            case "проект":
                return MemoryItemType.ProjectFact;
            case "preference":
            case "предпочтение":
                return MemoryItemType.Preference;
            case "constraint":
            case "ограничение":
                return MemoryItemType.Constraint;
            case "idea":
            case "идея":
                return MemoryItemType.Idea;
            case "correction":
            case "уточнение":
            case "исправление":
                return MemoryItemType.Correction;
            default:
                return MemoryItemType.Fact;   // fact | факт | неизвестное
        }
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
