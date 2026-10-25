using System.Text;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Memory.Recall;
using BrainstormBuddy.Services;
using BrainstormBuddy.Transcription;

namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>
/// Встроенный поиск Buddy для модели: факты памяти, фрагменты прошлых встреч и
/// чтение найденного источника. Работает без внешнего MCP-сервера (не зависит
/// от YouTrack/Filesystem) и маршрутизирует вызовы «чужих» серверов в
/// <c>fallback</c>, поэтому один экземпляр можно отдать tool-loop как транспорт.
///
/// Бюджет: <see cref="BeginRequest"/> получает остаток дополнительного поиска
/// (настройка «Бюджет дополнительного поиска», урезанная под окно модели).
/// Повторные вызовы не возвращают уже выданные фрагменты; когда бюджет кончился
/// или новых результатов нет, инструмент честно сообщает это модели.
/// </summary>
public sealed class BuiltinMemoryTools : IMcpToolTransport
{
    public const string ServerId = "builtin-memory";
    public const string AdapterMemorySearch = "buddy_memory_search";
    public const string AdapterMeetingsSearch = "buddy_meetings_search";
    public const string AdapterSourceRead = "buddy_source_read";
    public const string OriginalMemorySearch = "memory_search";
    public const string OriginalMeetingsSearch = "meetings_search";
    public const string OriginalSourceRead = "source_read";

    /// <summary>Сервер-«обёртка» для tool-loop: инструменты Buddy, не внешний MCP.</summary>
    public static McpOutgoingServer Server { get; } = new()
    {
        Id = ServerId,
        Name = UiText.Tr("Память Buddy", "Buddy memory"),
        Transport = "builtin",
        Trust = "own",
        Enabled = true,
    };

    private readonly Func<string, int, int, CancellationToken, Task<List<MemoryRecallHit>>> _searchMemory;
    private readonly Func<string, MeetingSearchOptions?, CancellationToken, Task<MeetingSearchResult>> _searchMeetings;
    private readonly Func<int> _extraBudgetTokens;
    private readonly Func<string?> _scopeLabel;
    private readonly Func<bool>? _memoryEnabled;
    private readonly Func<bool>? _meetingsEnabled;
    private readonly Func<string?>? _currentMeetingId;
    private readonly Func<DateOnly>? _today;
    private readonly IMcpToolTransport? _fallback;
    private readonly LoggingService? _log;

    /// <summary>Жёсткий предел поисковых вызовов (search/read) в одном ответе:
    /// каждый шаг заново отправляет накопленный контекст, поэтому «ещё один
    /// уточняющий запрос» после восьми попыток дороже, чем итог с ограничением.
    /// Бюджет токенов остаётся основным лимитом; этот предел защищает от
    /// бесконечного перебора формулировок.</summary>
    private const int MaxSearchCalls = 8;

    /// <summary>
    /// Состояние одного ответа: бюджет, счётчики, дедуп, источники и цитаты.
    /// Создаётся в <see cref="BeginRequest"/> и живёт в AsyncLocal потока
    /// запроса, поэтому одновременные вопросы не видят и не сбрасывают
    /// состояние друг друга.
    /// </summary>
    public sealed class RequestState
    {
        internal readonly object Gate = new();
        internal readonly HashSet<string> Returned = new(StringComparer.Ordinal);
        internal readonly HashSet<string> SearchSeen = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, SourceEntry> Sources = new(StringComparer.Ordinal);
        internal readonly List<string> CitationList = new();
        internal int Budget;
        internal int Spent;
        internal int MemoryTokens;
        internal int MeetingTokens;
        internal int ReadTokens;
        internal int Calls;
        internal int EmptyStreak;
        internal string? LastStop;

        internal RequestState(string requestId, int budget)
        {
            RequestId = requestId;
            Budget = budget;
        }

        /// <summary>Сквозной requestId этого ответа.</summary>
        public string RequestId { get; }

        /// <summary>Человекочитаемые источники, реально выданные модели
        /// именно в этом ответе.</summary>
        public IReadOnlyList<string> Citations
        {
            get { lock (Gate) return CitationList.ToArray(); }
        }

        /// <summary>Дополнительные поиски (memory/meetings) и чтения в ответе.</summary>
        public int SearchCallCount { get { lock (Gate) return Calls; } }
        public int SpentTokens { get { lock (Gate) return Spent; } }
        public int MemorySpentTokens { get { lock (Gate) return MemoryTokens; } }
        public int MeetingSpentTokens { get { lock (Gate) return MeetingTokens; } }
        public int ReadSpentTokens { get { lock (Gate) return ReadTokens; } }
        public string? LastStopReason { get { lock (Gate) return LastStop; } }
        public int BudgetTokens { get { lock (Gate) return Budget; } }
    }

    /// <summary>Состояние текущего async-потока: tool-loop, его источники и
    /// счётчики привязаны к запросу, а не к общему экземпляру транспорта.</summary>
    private static readonly AsyncLocal<RequestState?> _request = new();

    public BuiltinMemoryTools(
        Func<string, int, int, CancellationToken, Task<List<MemoryRecallHit>>> searchMemory,
        Func<string, MeetingSearchOptions?, CancellationToken, Task<MeetingSearchResult>> searchMeetings,
        Func<int> extraBudgetTokens,
        Func<string?> scopeLabel,
        IMcpToolTransport? fallback = null,
        LoggingService? log = null,
        Func<bool>? memoryEnabled = null,
        Func<bool>? meetingsEnabled = null,
        Func<string?>? currentMeetingId = null,
        Func<DateOnly>? today = null)
    {
        _searchMemory = searchMemory;
        _searchMeetings = searchMeetings;
        _extraBudgetTokens = extraBudgetTokens;
        _scopeLabel = scopeLabel;
        _fallback = fallback;
        _log = log;
        _memoryEnabled = memoryEnabled;
        _meetingsEnabled = meetingsEnabled;
        _currentMeetingId = currentMeetingId;
        _today = today;
    }

    /// <summary>Начать запрос: новое состояние для ЭТОГО async-потока;
    /// работающие запросы сохраняют своё. Возвращённый state - источник
    /// итоговых цитат и счётчиков конкретного ответа.</summary>
    public RequestState BeginRequest(string requestId, int extraBudgetTokens)
    {
        var state = new RequestState(requestId, Math.Max(0, extraBudgetTokens));
        _request.Value = state;
        return state;
    }

    /// <summary>Состояние текущего потока (пустое - вызов без BeginRequest).</summary>
    private static RequestState Current => _request.Value ??= new RequestState("", 0);

    /// <summary>Дополнительные поиски (memory/meetings) и чтения текущего запроса.</summary>
    public int SearchCallCount => Current.SearchCallCount;

    public int SpentTokens => Current.SpentTokens;

    public int MemorySpentTokens => Current.MemorySpentTokens;

    public int MeetingSpentTokens => Current.MeetingSpentTokens;

    public int ReadSpentTokens => Current.ReadSpentTokens;

    /// <summary>Причина последней остановки дополнительного поиска текущего
    /// запроса: call-limit / budget / duplicate-query / no-new-results.</summary>
    public string? LastStopReason => Current.LastStopReason;

    public int BudgetTokens => Current.BudgetTokens;

    private IReadOnlyList<McpToolSpec> Specs
    {
        get
        {
            // Инструменты отдаются только для включённых областей: при
            // выключенной памяти модель не должна искать по фактам, но поиск
            // по прошлым встречам остаётся самостоятельной настройкой.
            var memoryOn = _memoryEnabled?.Invoke() != false;
            var meetingsOn = _meetingsEnabled?.Invoke() != false;
            var today = (_today?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd");
            return new[]
    {
        new McpToolSpec(AdapterMemorySearch, ServerId, Server.Name, OriginalMemorySearch,
            UiText.Tr(
                "Поиск по сохранённым фактам (внутренняя память Buddy). Ищи здесь словами " +
                "пользователя и синонимами; короткие обозначения (QA, AI, номера задач) — " +
                "важная тема. Если результатов мало — уточни формулировку и вызови снова.",
                "Search saved facts (Buddy internal memory). Search with the user's words and " +
                "synonyms; short identifiers (QA, AI, ticket numbers) are meaningful. If few " +
                "results, refine the query and call again."),
            JsonDocument.Parse("""
            {"type":"object","properties":{
              "query":{"type":"string","description":"Поисковый запрос словами пользователя"},
              "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Максимум фактов (по умолчанию 20)"}
            },"required":["query"]}
            """).RootElement.Clone(), McpActionRisk.ReadOnly),
        new McpToolSpec(AdapterMeetingsSearch, ServerId, Server.Name, OriginalMeetingsSearch,
            UiText.Tr(
                $"Поиск по транскриптам прошлых встреч (кто, когда и что сказал). Сегодня {today}; " +
                "относительные даты (вчера, 22 октября) переводи в date_from/date_to включительно. " +
                "Если пользователь называет дату или период, ВСЕГДА передавай date_from/date_to, " +
                "иначе слово «вчера» будет искаться внутри транскрипта. Для перечисления встреч " +
                "периода достаточно дат без query. Используй для договорённостей и деталей " +
                "прошедших встреч.",
                $"Search past meeting transcripts (who said what and when). Today is {today}; " +
                "convert relative dates (yesterday, 22 October) to inclusive date_from/date_to. " +
                "Whenever the user names a date or period, ALWAYS pass date_from/date_to, otherwise " +
                "the date word is searched inside transcripts. Dates alone list meetings in the " +
                "period. Use for past-meeting decisions and details."),
            JsonDocument.Parse($$"""
            {"type":"object","properties":{
              "query":{"type":"string","description":"Что искать в прошлых встречах (слова пользователя, не словарь)"},
              "date_from":{"type":"string","description":"Начало периода включительно: YYYY-MM-DD или вчера/сегодня"},
              "date_to":{"type":"string","description":"Конец периода включительно: YYYY-MM-DD; для одного дня равен date_from"},
              "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Максимум фрагментов (по умолчанию 10)"}
            },"required":[]}
            """).RootElement.Clone(), McpActionRisk.ReadOnly),
        new McpToolSpec(AdapterSourceRead, ServerId, Server.Name, OriginalSourceRead,
            UiText.Tr(
                "Прочитать найденный источник по его source: факт памяти, фрагмент транскрипта " +
                "или резюме встречи. Возвращает только найденный фрагмент, а не полный транскрипт встречи.",
                "Read a found source by its source id: a memory fact, a transcript fragment or a " +
                "meeting summary. Returns only the found fragment, not the full meeting transcript."),
            JsonDocument.Parse("""
            {"type":"object","properties":{
              "source":{"type":"string","description":"ТОЧНЫЙ source из результатов поиска, без изменений: mem:<id> или meeting:<id>:<line>. Не выдумывай и не сокращай id — повтори чтение с id из выдачи."}
            },"required":["source"]}
            """).RootElement.Clone(), McpActionRisk.ReadOnly),
    }
            .Where(s => s.OriginalName switch
            {
                OriginalMemorySearch => memoryOn,
                OriginalMeetingsSearch => meetingsOn,
                OriginalSourceRead => memoryOn || meetingsOn,
                _ => false,
            })
            .ToList();
        }
    }

    public Task<IReadOnlyList<McpToolSpec>> ListToolsAsync(McpOutgoingServer server, CancellationToken ct)
    {
        if (server.Id != ServerId)
            return _fallback?.ListToolsAsync(server, ct)
                ?? Task.FromResult<IReadOnlyList<McpToolSpec>>(Array.Empty<McpToolSpec>());
        return Task.FromResult<IReadOnlyList<McpToolSpec>>(Specs);
    }

    public Task<McpToolCallResult> CallToolAsync(
        McpOutgoingServer server, McpToolSpec tool, string argsJson,
        IProgress<McpToolProgress>? progress, CancellationToken ct)
    {
        if (server.Id != ServerId)
            return _fallback?.CallToolAsync(server, tool, argsJson, progress, ct)
                ?? Task.FromResult(new McpToolCallResult(
                    "ERROR: встроенный поиск не настроен, внешний MCP-сервер недоступен", true, 0));
        return tool.OriginalName switch
        {
            OriginalMemorySearch => SearchMemoryAsync(argsJson, progress, ct),
            OriginalMeetingsSearch => SearchMeetingsAsync(argsJson, progress, ct),
            OriginalSourceRead => ReadSourceAsync(argsJson, progress, ct),
            _ => Task.FromResult(new McpToolCallResult(
                $"ERROR: неизвестный встроенный инструмент «{tool.OriginalName}»", true, 0)),
        };
    }

    private async Task<McpToolCallResult> SearchMemoryAsync(
        string argsJson, IProgress<McpToolProgress>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = Current;
        var args = ParseArgs(argsJson);
        var query = args.Query;
        if (string.IsNullOrWhiteSpace(query))
            return Fail("нужен непустой query", sw);
        var limit = Math.Clamp(args.Limit ?? 20, 1, 50);
        // Точный повтор того же запроса в этом ответе не даст новых фрагментов
        // (выданные source уже дедуплицированы) — сразу говорим модели об этом.
        var signature = "memory|" + query.ToLowerInvariant() + "|" + limit;
        lock (state.Gate)
        {
            if (!state.SearchSeen.Add(signature))
            {
                state.LastStop = "duplicate-query";
                return DuplicateSearch(sw);
            }
        }

        int remaining;
        bool overLimit;
        lock (state.Gate)
        {
            state.Calls++;
            overLimit = state.Calls > MaxSearchCalls;
            remaining = state.Budget - state.Spent;
        }
        if (overLimit)
            return SearchLimitReached(sw, state);
        if (remaining < 300)
            return BudgetExhausted(sw, state);

        progress?.Report(new McpToolProgress(null, null,
            UiText.Tr("Ищу в памяти…", "Searching memory…")));
        var hits = await _searchMemory(query, limit, Math.Min(remaining, 8000), ct)
            .ConfigureAwait(false);

        var payload = new StringBuilder();
        payload.Append("{\"scope\":").Append(JsonSerializer.Serialize(_scopeLabel() ?? "all"));
        payload.Append(",\"results\":[");
        var returned = 0;
        var duplicates = 0;
        var truncated = false;
        foreach (var hit in hits)
        {
            var id = "mem:" + hit.Item.Id;
            lock (state.Gate)
            {
                if (!state.Returned.Add(id)) { duplicates++; continue; }
            }
            var text = hit.Item.Content?.Trim() ?? "";
            int budgetLeft;
            lock (state.Gate) budgetLeft = Math.Max(0, state.Budget - state.Spent);
            var maxChars = budgetLeft * 4; // токены → символы (оценка)
            if (text.Length > maxChars)
            {
                text = text[..Math.Max(0, maxChars)] + "…";
                truncated = true;
            }
            if (returned > 0) payload.Append(',');
            var provenance = ContextBlockBuilder.SourceLabel(hit.Item.Source);
            payload.Append("{\"source\":").Append(JsonSerializer.Serialize(id))
                   .Append(",\"kind\":\"fact\"")
                   .Append(",\"provenance\":").Append(JsonSerializer.Serialize(provenance))
                   .Append(",\"meetingId\":").Append(hit.Item.SourceMeetingId is { } smid
                       ? smid.ToString() : "null")
                   .Append(",\"speaker\":").Append(JsonSerializer.Serialize(hit.Item.Speaker))
                   .Append(",\"score\":").Append(JsonSerializer.Serialize(Math.Round(hit.Score, 3)))
                   .Append(",\"text\":").Append(JsonSerializer.Serialize(text)).Append('}');
            returned++;
            var cost = text.Length / 4 + 20;
            lock (state.Gate)
            {
                state.Spent += cost;
                state.MemoryTokens += cost;
                var full = hit.Item.Content ?? text;
                state.Sources[id] = new SourceEntry(full, "fact", provenance,
                    hit.Item.SourceMeetingId?.ToString(), null, null, hit.Item.Speaker);
                state.CitationList.Add(SourceCitation(id, full, null));
            }
            if (state.Spent >= state.Budget) { truncated = true; break; }
        }
        payload.Append(']');
        payload.Append(",\"returned\":").Append(returned);
        payload.Append(",\"found\":").Append(hits.Count);
        var stopAfterEmpty = TrackSearchOutcome(state, returned);
        var note = BuildSearchNote(state, returned, duplicates, truncated, hits.Count, stopAfterEmpty);
        if (note != null) payload.Append(",\"note\":").Append(JsonSerializer.Serialize(note));
        payload.Append('}');

        _log?.Info($"MEMDIAG rid={state.RequestId} tool=memory_search q=\"{Clip(query, 120)}\" " +
                   $"limit={limit} found={hits.Count} returned={returned} dup={duplicates} " +
                   $"tokens={state.Spent}/{state.Budget} {sw.ElapsedMilliseconds}ms", "Memory");
        return new McpToolCallResult(payload.ToString(), false, sw.ElapsedMilliseconds);
    }

    private async Task<McpToolCallResult> SearchMeetingsAsync(
        string argsJson, IProgress<McpToolProgress>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = Current;
        var args = ParseArgs(argsJson);
        var query = args.Query?.Trim() ?? "";
        var today = _today?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now);
        DateOnly? from = null, to = null;
        if (!string.IsNullOrWhiteSpace(args.DateFrom))
        {
            if (!MeetingDateQuery.TryParse(args.DateFrom, today, out var d))
                return Fail("не понял date_from: укажи YYYY-MM-DD или вчера/сегодня", sw);
            from = d;
        }
        if (!string.IsNullOrWhiteSpace(args.DateTo))
        {
            if (!MeetingDateQuery.TryParse(args.DateTo, today, out var d))
                return Fail("не понял date_to: укажи YYYY-MM-DD или вчера/сегодня", sw);
            to = d;
        }
        // Явные date_from/date_to сильнее даты в тексте. Без них «вчера» и
        // «22 октября» превращаем в фильтр здесь, чтобы диагностика и
        // сигнатура запроса видели дату, а не искали её слово в транскрипте.
        if (from == null && to == null && query.Length > 0 &&
            MeetingDateQuery.TryExtractPeriod(query, today,
                out var inferredFrom, out var inferredTo, out var cleaned))
        {
            from = inferredFrom;
            to = inferredTo;
            query = cleaned.Trim();
        }
        if (from == null && to != null) from = to;
        if (to == null && from != null) to = from;
        if (from != null && to != null && from > to)
            return Fail("date_from позже date_to: поменяй границы периода", sw);
        if (query.Length == 0 && from == null)
            return Fail("нужен query или date_from/date_to", sw);
        if (query.Length == 0) query = ""; // поиск только по дате — листинг встреч периода
        var limit = Math.Clamp(args.Limit ?? 10, 1, 50);
        var signature = "meeting|" + query.ToLowerInvariant() + "|" +
                        (from?.ToString("yyyy-MM-dd") ?? "-") + "|" +
                        (to?.ToString("yyyy-MM-dd") ?? "-") + "|" + limit;
        lock (state.Gate)
        {
            if (!state.SearchSeen.Add(signature))
            {
                state.LastStop = "duplicate-query";
                return DuplicateSearch(sw);
            }
        }

        int remaining;
        bool overLimit;
        lock (state.Gate)
        {
            state.Calls++;
            overLimit = state.Calls > MaxSearchCalls;
            remaining = state.Budget - state.Spent;
        }
        if (overLimit)
            return SearchLimitReached(sw, state);
        if (remaining < 300)
            return BudgetExhausted(sw, state);

        progress?.Report(new McpToolProgress(null, null,
            UiText.Tr("Проверяю прошлые встречи…", "Checking past meetings…")));
        var options = new MeetingSearchOptions
        {
            DateFrom = from,
            DateTo = to,
            IncludeMeetingsInRange = true,
            ExcludeMeetingId = _currentMeetingId?.Invoke(),
        };
        var result = await _searchMeetings(query, options, ct).ConfigureAwait(false);

        var payload = new StringBuilder();
        payload.Append("{\"today\":").Append(JsonSerializer.Serialize(today.ToString("yyyy-MM-dd")))
               .Append(",\"dateFilter\":{\"from\":")
               .Append(from == null ? "null" : JsonSerializer.Serialize(MeetingDateQuery.Format(from.Value)))
               .Append(",\"to\":")
               .Append(to == null ? "null" : JsonSerializer.Serialize(MeetingDateQuery.Format(to.Value)))
               .Append("},\"results\":[");
        var returned = 0;
        var duplicates = 0;
        var truncated = false;
        foreach (var hit in result.Hits.Take(limit))
        {
            var id = "meeting:" + hit.MeetingId + ":" +
                     (hit.LineIndex >= 0 ? hit.LineIndex.ToString() : "summary");
            lock (state.Gate)
            {
                if (!state.Returned.Add(id)) { duplicates++; continue; }
            }
            // Только сама найденная строка: склейка соседних реплик без роли
            // приписывала слова участника ответу ИИ (и source_read не могла
            // вернуть цитату). Контекст при необходимости читается отдельным source.
            var fullText = hit.LineText;
            if (string.IsNullOrWhiteSpace(fullText)) fullText = hit.Snippet;
            fullText = fullText.Trim();
            var text = fullText;
            int budgetLeft;
            lock (state.Gate) budgetLeft = Math.Max(0, state.Budget - state.Spent);
            var maxChars = budgetLeft * 4;
            if (text.Length > maxChars)
            {
                text = text[..Math.Max(0, maxChars)] + "…";
                truncated = true;
            }
            var kind = hit.Role == "summary" ? "summary"
                : hit.Role == "meeting" ? "meeting" : "transcript";
            if (returned > 0) payload.Append(',');
            payload.Append("{\"source\":").Append(JsonSerializer.Serialize(id))
                   .Append(",\"meeting\":").Append(JsonSerializer.Serialize(hit.MeetingTitle))
                   .Append(",\"meetingDate\":").Append(JsonSerializer.Serialize(
                       MeetingSearchOptions.LocalDate(hit.MeetingStartedAt).ToString("yyyy-MM-dd")))
                   .Append(",\"time\":").Append(JsonSerializer.Serialize(hit.Ts))
                   .Append(",\"speaker\":").Append(JsonSerializer.Serialize(hit.SpeakerLabel))
                   .Append(",\"role\":").Append(JsonSerializer.Serialize(hit.Role))
                   .Append(",\"kind\":").Append(JsonSerializer.Serialize(kind))
                   .Append(",\"provenance\":").Append(JsonSerializer.Serialize(
                       hit.Role == "answer" ? "ai_answer"
                       : hit.Role == "summary" ? "ai_summary"
                       : hit.Role == "meeting" ? "meeting_card" : "participant"))
                   .Append(",\"semantic\":").Append(hit.SemanticMatch ? "true" : "false")
                   .Append(",\"score\":").Append(hit.Score.ToString("0.###",
                       System.Globalization.CultureInfo.InvariantCulture))
                   .Append(",\"text\":").Append(JsonSerializer.Serialize(text)).Append('}');
            returned++;
            var cost = text.Length / 4 + 20;
            lock (state.Gate)
            {
                state.Spent += cost;
                state.MeetingTokens += cost;
                state.Sources[id] = new SourceEntry(
                    fullText, kind,
                    hit.Role == "answer" ? "ai_answer"
                    : hit.Role == "summary" ? "ai_summary"
                    : hit.Role == "meeting" ? "meeting_card" : "participant",
                    hit.MeetingTitle,
                    MeetingSearchOptions.LocalDate(hit.MeetingStartedAt).ToString("yyyy-MM-dd"),
                    hit.Ts, hit.SpeakerLabel);
                state.CitationList.Add(SourceCitation(id, text, hit));
            }
            if (state.Spent >= state.Budget) { truncated = true; break; }
        }
        payload.Append(']');
        payload.Append(",\"returned\":").Append(returned);
        payload.Append(",\"found\":").Append(result.Hits.Count);
        payload.Append(",\"dateFallback\":").Append(result.DateFallback ? "true" : "false");
        payload.Append(",\"status\":{\"incomplete\":").Append(result.Status.Incomplete ? "true" : "false")
               .Append(",\"semanticAvailable\":").Append(result.Status.SemanticAvailable ? "true" : "false")
               .Append(",\"semanticPending\":").Append(result.Status.SemanticPending ? "true" : "false")
               .Append('}');
        var stopAfterEmpty = TrackSearchOutcome(state, returned);
        string? note;
        if (from == null && to == null)
        {
            note = BuildSearchNote(state, returned, duplicates, truncated, result.Hits.Count, stopAfterEmpty)
                ?? (result.Status.Incomplete
                    ? UiText.Tr("Поиск по встречам выполнен не полностью — не утверждай, что данных нет.",
                        "Past-meeting search did not complete — do not claim there is no data.")
                    : !result.Status.SemanticAvailable
                        ? UiText.Tr("Смысловой поиск по встречам недоступен: искались только точные совпадения.",
                            "Semantic meeting search is unavailable: only exact matches were searched.")
                        : result.Status.SemanticPending
                            ? UiText.Tr("Смысловой индекс встреч ещё строится: перефразированные совпадения " +
                                        "могут не найтись сейчас.",
                                "The meeting semantic index is still building: paraphrased matches may be missing.")
                            : null);
        }
        else if (result.Hits.Count == 0)
        {
            note = UiText.Tr(
                $"За период {MeetingDateQuery.Format(from ?? to ?? today)}" +
                (from != null && to != null && from != to
                    ? $" – {MeetingDateQuery.Format(to.Value)}" : "") +
                " встреч не найдено. Это не ответ на вопрос: не выдумывай факты.",
                $"No meetings found for the selected period. This is not an answer: do not invent facts.");
        }
        else
        {
            note = BuildSearchNote(state, returned, duplicates, truncated, result.Hits.Count, stopAfterEmpty);
            if (result.DateFallback)
            {
                var prefix = UiText.Tr(
                    "Точных совпадений по запросу нет. Ниже — встречи выбранного периода " +
                    "(резюме/первые реплики), это НЕ подтверждение слов пользователя.",
                    "No exact matches for the query. Below are meetings from the selected period " +
                    "(summary/first lines); this is NOT confirmation of the user's words.");
                note = note == null ? prefix : prefix + " " + note;
            }
            else if (note == null && result.Status.Incomplete)
                note = UiText.Tr("Поиск по встречам выполнен не полностью — не утверждай, что данных нет.",
                    "Past-meeting search did not complete — do not claim there is no data.");
        }
        if (note != null) payload.Append(",\"note\":").Append(JsonSerializer.Serialize(note));
        payload.Append('}');

        _log?.Info($"MEMDIAG rid={state.RequestId} tool=meetings_search q=\"{Clip(query, 120)}\" " +
                   $"dateFrom={from?.ToString("yyyy-MM-dd") ?? "-"} " +
                   $"dateTo={to?.ToString("yyyy-MM-dd") ?? "-"} " +
                   $"found={result.Hits.Count} returned={returned} dup={duplicates} " +
                   $"dateFallback={result.DateFallback} " +
                   $"incomplete={result.Status.Incomplete} " +
                   $"semanticAvailable={result.Status.SemanticAvailable} " +
                   $"semanticPending={result.Status.SemanticPending} " +
                   $"semanticTimedOut={result.Status.SemanticTimedOut} " +
                   $"tokens={state.Spent}/{state.Budget} {sw.ElapsedMilliseconds}ms", "Memory");
        return new McpToolCallResult(payload.ToString(), false, sw.ElapsedMilliseconds);
    }

    private Task<McpToolCallResult> ReadSourceAsync(
        string argsJson, IProgress<McpToolProgress>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = Current;
        var source = ParseArgs(argsJson).Source;
        if (string.IsNullOrWhiteSpace(source))
            return Task.FromResult(Fail("нужен source из результата поиска", sw));

        progress?.Report(new McpToolProgress(null, null,
            UiText.Tr("Читаю найденные источники…", "Reading found sources…")));
        SourceEntry? entry;
        lock (state.Gate) state.Sources.TryGetValue(source.Trim(), out entry);
        if (entry == null || string.IsNullOrWhiteSpace(entry.Text))
        {
            // Неуспешное чтение - не вызов поиска: бюджет вызовов не тратится,
            // модель может повторить чтение с точным source из выдачи.
            var available = AvailableSourceIds(state);
            _log?.Info($"MEMDIAG rid={state.RequestId} tool=source_read source=\"{Clip(source, 80)}\" " +
                       $"result=unknown available={available.Count}", "Memory");
            var note = UiText.Tr(
                "Источник не найден. Повтори чтение с ТОЧНЫМ source из результатов поиска. " +
                "Доступные source: {list}.",
                "Source not found. Repeat the read with the EXACT source from the search results. " +
                "Available sources: {list}.");
            return Task.FromResult(new McpToolCallResult(
                "{\"source\":" + JsonSerializer.Serialize(source.Trim()) +
                ",\"results\":[],\"returned\":0,\"availableSources\":" + JsonSerializer.Serialize(available) +
                ",\"note\":" + JsonSerializer.Serialize(
                    note.Replace("{list}", string.Join(", ", available))) + "}",
                true, sw.ElapsedMilliseconds));
        }

        int remaining;
        bool overLimit;
        lock (state.Gate)
        {
            state.Calls++;
            overLimit = state.Calls > MaxSearchCalls;
            remaining = state.Budget - state.Spent;
        }
        if (overLimit)
            return Task.FromResult(SearchLimitReached(sw, state));
        if (remaining < 200)
            return Task.FromResult(BudgetExhausted(sw, state));

        var totalChars = entry.Text.Length;
        var text = entry.Text;
        var truncated = false;
        var maxChars = Math.Max(0, remaining) * 4;
        if (text.Length > maxChars)
        {
            text = text[..Math.Max(0, maxChars)] + "…";
            truncated = true;
        }
        lock (state.Gate)
        {
            state.Spent += text.Length / 4 + 10;
            state.ReadTokens += text.Length / 4 + 10;
            state.EmptyStreak = 0;
        }
        var payload = "{\"source\":" + JsonSerializer.Serialize(source.Trim()) +
                      ",\"kind\":" + JsonSerializer.Serialize(entry.Kind) +
                      ",\"scope\":" + JsonSerializer.Serialize(SourceScope(entry.Kind)) +
                      ",\"provenance\":" + JsonSerializer.Serialize(entry.Provenance) +
                      ",\"meeting\":" + JsonSerializer.Serialize(entry.Meeting) +
                      ",\"meetingDate\":" + JsonSerializer.Serialize(entry.MeetingDate) +
                      ",\"time\":" + JsonSerializer.Serialize(entry.Time) +
                      ",\"speaker\":" + JsonSerializer.Serialize(entry.Speaker) +
                      ",\"chars\":" + text.Length +
                      ",\"totalChars\":" + totalChars +
                      ",\"text\":" + JsonSerializer.Serialize(text) +
                      ",\"returned\":1" +
                      (truncated ? ",\"note\":" + JsonSerializer.Serialize(
                          UiText.Tr("Источник прочитан частично — достигнут бюджет поиска.",
                              "Source read partially — search budget reached.")) : "") + "}";
        _log?.Info($"MEMDIAG rid={state.RequestId} tool=source_read source=\"{Clip(source, 80)}\" " +
                   $"kind={entry.Kind} chars={text.Length}/{totalChars} truncated={truncated} " +
                   $"tokens={state.Spent}/{state.Budget} {sw.ElapsedMilliseconds}ms", "Memory");
        return Task.FromResult(new McpToolCallResult(payload, false, sw.ElapsedMilliseconds));
    }

    private bool TrackSearchOutcome(RequestState state, int returned)
    {
        lock (state.Gate)
        {
            if (returned > 0) { state.EmptyStreak = 0; state.LastStop = null; return false; }
            state.EmptyStreak++;
            if (state.EmptyStreak < 3) return false;
            state.LastStop = "no-new-results";
            return true;
        }
    }

    /// <summary>
    /// Доступные source из уже выполненных поисков этого запроса (до 20) -
    /// чтобы модель повторила чтение с точным id, а не угадывала.
    /// </summary>
    private static List<string> AvailableSourceIds(RequestState state)
    {
        lock (state.Gate)
            return state.Sources.Keys.Take(20).ToList();
    }

    private static string? BuildSearchNote(RequestState state, int returned, int duplicates,
        bool truncated, int found, bool stopAfterEmpty = false)
    {
        var notes = new List<string>();
        if (returned == 0)
            notes.Add(found == 0
                ? UiText.Tr("Совпадений нет. Попробуй другие слова: синонимы, имена, номера задач.",
                    "No matches. Try other words: synonyms, names, ticket numbers.")
                : UiText.Tr("Новых результатов нет (эти фрагменты уже выданы). Уточни запрос, " +
                            "если нужна другая информация.",
                    "No new results (these fragments were already returned). Refine the query " +
                    "if you need different information."));
        else if (duplicates > 0)
            notes.Add(UiText.Tr("Повторные фрагменты пропущены.", "Repeated fragments were skipped."));
        if (truncated || state.Spent >= state.Budget)
            notes.Add(UiText.Tr("Достигнут бюджет поиска: результаты могут быть неполными. " +
                                "Сообщи пользователю об ограничении.",
                "Search budget reached: results may be incomplete. Tell the user about the limit."));
        if (stopAfterEmpty)
            notes.Add(UiText.Tr("Несколько уточнений подряд не дали новых данных. Не вызывай поиск " +
                                "снова: ответь по уже найденному; если ответа нет — прямо скажи, " +
                                "что в проверенных источниках нет данных, и что поиск ограничен.",
                "Several refinements in a row returned no new data. Do not search again: answer " +
                "from what was found; if there is no answer, say directly that the checked " +
                "sources contain no such data and that the search was limited."));
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>Точный повтор запроса: ничего не ищем и не тратим бюджет.</summary>
    private static McpToolCallResult DuplicateSearch(System.Diagnostics.Stopwatch sw)
        => new("{\"results\":[],\"returned\":0,\"duplicate\":true,\"note\":" +
               JsonSerializer.Serialize(UiText.Tr(
                   "Этот запрос уже выполнялся в этом ответе; повтор без новых условий не даст " +
                   "новых данных. Используй уже полученные фрагменты/source_read или ответь по ним.",
                   "This query already ran in this answer; repeating it without new conditions " +
                   "will not produce new data. Use the fragments/source_read you already have, " +
                   "or answer from them.")) + "}",
            false, sw.ElapsedMilliseconds);

    private McpToolCallResult SearchLimitReached(System.Diagnostics.Stopwatch sw, RequestState state)
    {
        lock (state.Gate) state.LastStop = "call-limit";
        _log?.Info($"MEMDIAG rid={state.RequestId} tool=search_limit " +
                   $"calls={state.Calls}/{MaxSearchCalls} " +
                   $"tokens={state.Spent}/{state.Budget}", "Memory");
        return new McpToolCallResult(
            "{\"results\":[],\"returned\":0,\"limitReached\":true,\"note\":" + JsonSerializer.Serialize(
                UiText.Tr("Лимит поисковых запросов исчерпан. Не вызывай поиск снова: ответь по уже " +
                          "найденному. Если данных нет — прямо скажи: «в проверенных источниках " +
                          "нет данных», и что поиск ограничен.",
                    "The search-request limit is exhausted. Do not search again: answer from what " +
                    "was found. If there is no data, say directly that the checked sources do not " +
                    "contain it and that the search was limited.")) + "}",
            false, sw.ElapsedMilliseconds);
    }

    private McpToolCallResult BudgetExhausted(System.Diagnostics.Stopwatch sw, RequestState state)
    {
        lock (state.Gate) state.LastStop = "budget";
        _log?.Info($"MEMDIAG rid={state.RequestId} tool=budget_exhausted " +
                   $"tokens={state.Spent}/{state.Budget}", "Memory");
        return new McpToolCallResult(
            "{\"results\":[],\"returned\":0,\"note\":" + JsonSerializer.Serialize(
                UiText.Tr("Бюджет дополнительного поиска исчерпан. Ответь по уже найденному и " +
                          "сообщи, что поиск ограничен бюджетом.",
                    "Extra search budget is exhausted. Answer from what was found and tell the " +
                    "user the search is budget-limited.")) + "}",
            false, sw.ElapsedMilliseconds);
    }

    private static McpToolCallResult Fail(string message, System.Diagnostics.Stopwatch sw)
        => new("ERROR: " + message, true, sw.ElapsedMilliseconds);

    /// <summary>Фактический объём источника: факт, фрагмент или резюме.</summary>
    private static string SourceScope(string kind) => kind switch
    {
        "fact" => "fact",
        "summary" => "meeting_summary",
        "meeting" => "meeting_card",
        _ => "transcript_fragment",
    };

    private static string SourceCitation(string id, string text, MeetingSearchHit? hit)
    {
        var body = Clip(text.Replace('\n', ' ').Trim(), 140);
        if (hit == null) return $"Факт {id}: {body}";
        var place = hit.MeetingTitle + (hit.Ts.Length > 0 ? " · " + hit.Ts : "") +
                    " · " + hit.SpeakerLabel;
        return hit.Role switch
        {
            "summary" => $"Резюме встречи {place}: {body}",
            "meeting" => $"Карточка встречи {place}: {body}",
            _ => $"Фрагмент встречи {place}: {body}",
        };
    }

    private static string Clip(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Сохранённый источник для source_read: найденный фрагмент и метаданные.</summary>
    internal sealed record SourceEntry(string Text, string Kind, string Provenance,
        string? Meeting, string? MeetingDate, string? Time, string? Speaker);

    private sealed record ParsedArgs(string? Query, int? Limit, string? Source,
        string? DateFrom = null, string? DateTo = null);

    private static ParsedArgs ParseArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return new ParsedArgs(null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Int(string name) => root.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
            return new ParsedArgs(Str("query"), Int("limit"), Str("source"),
                Str("date_from"), Str("date_to"));
        }
        catch (JsonException)
        {
            return new ParsedArgs(null, null, null);
        }
    }
}