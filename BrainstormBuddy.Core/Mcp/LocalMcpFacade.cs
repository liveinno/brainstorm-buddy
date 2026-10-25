using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Transcription;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Реализация IMcpFacade поверх живых сторов приложения: журналы встреч
/// (MeetingTranscriptStore), memory.db (IMemoryStore — может отсутствовать при
/// выключенной памяти), вложения (AttachmentStore). Все чтения идут через
/// McpScopeGuard.CheckRead: вне-scope элементы молча отфильтровываются из
/// списков/поиска (TotalScanned показывает объём сканирования), прямое чтение
/// чужой сущности — -32002 SCOPE_DENIED.
///
/// Идентификаторы в reference-строках:
///   meeting:{memId|negHash}  — mem_meeting.id либо FNV-1a журнального id
///                              (отрицательный) для встреч вне memory.db;
///   memory:{itemId}          — mem_item.id (требует MemoryIncluded);
///   attachment:{documentId}  — attachments.db documents.id;
///   project:{projectId}      — mem_project.id.
///
/// Задания kind="search"/"research" исполняются встроенным воркером
/// (последовательно, в фоне): search → те же хиты, что SearchAsync; research →
/// search + текст топ-хита. Остальные kind сразу завершаются Failed.
/// </summary>
public sealed class LocalMcpFacade : IMcpFacade, IDisposable
{
    private const int MaxSearchLimit = 50;
    private const int MaxListLimit = 200;
    private const int SnippetLen = 220;
    private const int MaxLinesPerMeeting = 4000;
    /// <summary>Потолок символьного «текста встречи» для поиска/чтения.</summary>
    private const int MaxMeetingTextChars = 1_500_000;
    /// <summary>Страница чтения источника по умолчанию — 128 KiB UTF-8 (спека §6.3).</summary>
    private const int DefaultPageBytes = 128 * 1024;
    /// <summary>Верхняя граница страницы.</summary>
    private const int MaxPageBytes = 2 * 1024 * 1024;
    /// <summary>Потолок выдачи устаревшему ReadSourceAsync без offset/cursor.</summary>
    private const int LegacyFullReadBytes = 2 * 1024 * 1024;

    /// <summary>Нормализованная проекция встречи из двух источников.</summary>
    private sealed class MeetingRow
    {
        public long Id;                    // >0 — mem_meeting.id; <0 — -fnv64(journalId)
        public string? JournalId;          // id журнала (basename .jsonl)
        public string? JournalPath;        // абсолютный путь .jsonl
        public long? ProjectId;
        public string Title = "";
        public DateTime StartedUtc;
        public int DurationSec;
        public int LineCount;
        public int AttachmentCount;
        public string[] Tags = [];
        public string Summary = "";
    }

    private readonly MeetingTranscriptStore _transcripts;
    private readonly IMemoryStore? _memory;
    /// <summary>
    /// Провайдер живого стора памяти. Память поднимается ПОСЛЕ старта MCP-хоста
    /// (инициализация SQLite + эмбеддеров), поэтому стор нельзя захватывать
    /// конструктором: иначе addFact и поиск по памяти вечно отвечают
    /// «memory not included». Провайдер вызывается на каждый запрос.
    /// </summary>
    private readonly Func<IMemoryStore?>? _memoryProvider;
    private readonly AttachmentStore? _attachments;
    private readonly McpJobStore _jobs;
    private readonly McpConfig _config;
    /// <summary>
    /// Делегат подтверждения опасных операций (удаление факта) — карточка
    /// «Разрешить/Отклонить» в оверлее. null — подтверждение недоступно,
    /// удаление отклоняется (явным отказом, а не выполняется молча).
    /// </summary>
    private Func<McpApprovalRequest, Task<bool>>? _approvalHandler;
    /// <summary>Потолок ожидания подтверждения владельца — запрос не должен висеть.</summary>
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);
    private readonly BrainstormBuddy.Services.LoggingService? _log;
    /// <summary>Не обязателен: нужен только чтобы отличать «Доступ отозван» (гранта нет).</summary>
    private readonly McpPermissionStore? _permissions;
    /// <summary>Журнал входящего канала (опционально): пишет все обращения клиентов.</summary>
    private readonly McpIncomingJournal? _journal;
    private readonly SemaphoreSlim _jobWorker = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly McpToolResultStore _toolResults;

    // ── Runtime-состояние для карточек (спека §6.1) ──────────────────────────
    // transport последнего контакта и «реального запроса» по connectionId.
    private readonly Dictionary<string, string> _seenTransport = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastRequestUtc = new(StringComparer.Ordinal);
    // Здоровье транспортов (listener/funnel): «Нет связи» с причиной.
    private readonly Dictionary<string, (bool Alive, string? Reason)> _transportState =
        new(StringComparer.Ordinal);
    private readonly object _presenceGate = new();
    private McpJsonRpcDispatch? _httpDispatch;
    private IPendingClientSource? _pendingSource;

    public LocalMcpFacade(
        MeetingTranscriptStore transcripts,
        IMemoryStore? memory,
        AttachmentStore? attachments,
        McpJobStore jobs,
        McpConfig config,
        BrainstormBuddy.Services.LoggingService? log = null,
        McpPermissionStore? permissions = null,
        string? toolResultsDir = null,
        McpIncomingJournal? journal = null,
        Func<IMemoryStore?>? memoryProvider = null)
    {
        _transcripts = transcripts;
        _memory = memory;
        _memoryProvider = memoryProvider;
        _attachments = attachments;
        _jobs = jobs;
        _config = config;
        _log = log;
        _permissions = permissions;
        _journal = journal;
        _toolResults = new McpToolResultStore(
            toolResultsDir ?? McpToolResultStore.DefaultDir(
                // McpJobStore пишет {appDataDir}\mcp\jobs.jsonl — берём корень данных оттуда.
                Path.GetDirectoryName(Path.GetDirectoryName(jobs.JournalPath)!)!));
    }

    public McpJobStore Jobs => _jobs;

    /// <summary>
    /// Чтение без права read — явный отказ, а не пустая выдача. Иначе агент
    /// без полномочий на чтение принимает «ничего не найдено» за истину и
    /// строит выводы по неполной картине. Право на запись (add-fact) само по
    /// себе чтение не даёт: <see cref="McpScopeGuard.CheckScope"/> проверяет
    /// область, <see cref="McpScopeGuard.CheckRead"/> — область И право read.
    /// </summary>
    private static void RequireRead(McpResolvedConnection c)
    {
        if (!c.Rights.Contains(McpRight.Read))
            throw McpRpcException.ScopeDenied("no read right");
    }

    /// <summary>Живой стор памяти на момент вызова (null - память выключена/не поднялась).</summary>
    private IMemoryStore? Mem => _memoryProvider?.Invoke() ?? _memory;

    // в”Ђв”Ђ IMcpFacade в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    public Task<McpServerStatus> GetStatusAsync(CancellationToken ct)
        => Task.FromResult(new McpServerStatus(
            Version: typeof(LocalMcpFacade).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ProfileName: Environment.UserName,
            HttpEnabled: _config.HttpEnabled,
            TunnelActive: _config.TunnelMode != "off",
            ConnectionsCount: _config.Connections.Count,
            PendingApprovals: _jobs.List().Count(j => j.State == McpJobState.WaitingApproval),
            UtcNow: DateTime.UtcNow));

    public Task<IReadOnlyList<McpProjectInfo>> ListProjectsAsync(
        McpResolvedConnection c, CancellationToken ct)
    {
        RequireRead(c);
        var rows = LoadMeetingRows();
        var result = new List<McpProjectInfo>();
        var mem = Mem;
        if (mem != null)
        {
            foreach (var p in mem.ListProjects())
            {
                if (!McpScopeGuard.CheckRead(c, p.Id, null)) continue;
                result.Add(new McpProjectInfo(
                    p.Id, p.Name, string.IsNullOrEmpty(p.Description) ? null : p.Description,
                    rows.Count(r => r.ProjectId == p.Id), p.CreatedAtUtc));
            }
        }
        return Task.FromResult<IReadOnlyList<McpProjectInfo>>(result);
    }

    public Task<IReadOnlyList<McpMeetingInfo>> ListMeetingsAsync(
        McpResolvedConnection c, long? projectId, int limit, string? cursor, CancellationToken ct)
    {
        RequireRead(c);
        var offset = ParseCursor(cursor);
        var take = Math.Clamp(limit <= 0 ? MaxListLimit : limit, 1, MaxListLimit);
        var result = LoadMeetingRows()
            .Where(r => projectId == null || r.ProjectId == projectId)
            .Where(r => McpScopeGuard.CheckRead(c, r.ProjectId, r.Id))
            .OrderByDescending(r => r.StartedUtc)
            .Skip(offset).Take(take)
            .Select(r => new McpMeetingInfo(
                r.Id, r.ProjectId, r.Title, r.StartedUtc, r.DurationSec,
                r.LineCount, r.AttachmentCount, r.Tags))
            .ToList();
        return Task.FromResult<IReadOnlyList<McpMeetingInfo>>(result);
    }

    /// <summary>Устаревшая сигнатура — делегирует в расширенный запрос (relevant, без фильтров).</summary>
    public Task<McpSearchResult> SearchAsync(
        McpResolvedConnection c, string query, McpSearchTypes types,
        long[]? projectIds, int limit, CancellationToken ct)
        => SearchAsync(c, new McpSearchRequest(query, types, projectIds, limit), ct);

    /// <summary>
    /// Полнота поиска (спека §6.3): relevant — топ-N по рангу; all — обход всех
    /// совпадений с курсором q:{kindIdx}:{entityIdx}. Ищется внутри текстов:
    /// реплики журнала (с #t= позицией), извлечённый текст вложений, факты памяти.
    /// Exhaustive=false честно показывает неполное покрытие (memory выключена,
    /// текст встречи обрезан потолком, FTS вернул не всё) — «ничего не найдено»
    /// безусловным не делается.
    /// </summary>
    public Task<McpSearchResult> SearchAsync(
        McpResolvedConnection c, McpSearchRequest req, CancellationToken ct)
    {
        RequireRead(c);
        if (string.IsNullOrWhiteSpace(req.Query))
            throw McpRpcException.InvalidParams("query is empty");
        var all = string.Equals(req.Mode, "all", StringComparison.OrdinalIgnoreCase);
        var take = Math.Clamp(req.Limit <= 0 ? MaxSearchLimit : req.Limit, 1,
            all ? MaxListLimit : MaxSearchLimit);
        var query = req.Query;
        var types = req.Types;
        var projectFilter = req.ProjectIds == null ? null : new HashSet<long>(req.ProjectIds);
        var resume = ParseSearchCursor(req.Cursor, out var curKind, out var curIdx, out var carried);
        var hits = new List<McpSearchHit>();
        var scanned = 0;
        // В mode=all счётчик накапливается в курсоре: на последней странице
        // totalMatches = полное число совпадений, а не «сколько нашли на этой».
        var total = carried;
        var mem = Mem;
        var incomplete = false;
        string? incompleteReason = null;
        var rows = LoadMeetingRows().OrderBy(r => r.Id).ToList();
        bool InPeriod(DateTime t) =>
            (req.FromUtc == null || t >= req.FromUtc) && (req.ToUtc == null || t <= req.ToUtc);

        // ── Проекты (kindIdx 0): имя/описание. ──
        var projIdx = 0;
        if (types.HasFlag(McpSearchTypes.Projects) && mem != null && curKind <= 0)
        {
            foreach (var p in mem.ListProjects())
            {
                scanned++;
                if (resume && curKind == 0 && projIdx < curIdx) { projIdx++; continue; }
                projIdx++;
                if (projectFilter != null && !projectFilter.Contains(p.Id)) continue;
                if (!InPeriod(p.CreatedAtUtc)) continue;
                if (!McpScopeGuard.CheckRead(c, p.Id, null)) continue;
                var m = Match(p.Name, query) ? 1.0
                      : Match(p.Description, query) ? 0.6 : 0.0;
                if (m <= 0) continue;
                // Счётчик после проверки лимита: в mode=all совпадение, на котором
                // страница закончилась, не считается дважды — его доведёт следующая.
                if (all && hits.Count >= take) { curKind = 0; curIdx = projIdx - 1; resume = true; goto done; }
                total++;
                hits.Add(new McpSearchHit("project", $"project:{p.Id}", p.Name,
                    SnippetOf(p.Description), m, p.CreatedAtUtc, p.Id, null));
            }
            curKind = 0; curIdx = 0;
        }

        // ── Встречи (kindIdx 1): заголовок/саммари → текст журнала с #t=. ──
        var mtIdx = 0;
        if (types.HasFlag(McpSearchTypes.Meetings) && curKind <= 1)
        {
            foreach (var r in rows)
            {
                scanned++;
                if (resume && curKind == 1 && mtIdx < curIdx) { mtIdx++; continue; }
                mtIdx++;
                ct.ThrowIfCancellationRequested();
                if (projectFilter != null && (r.ProjectId == null || !projectFilter.Contains(r.ProjectId.Value)))
                    continue;
                if (!InPeriod(r.StartedUtc)) continue;
                if (!McpScopeGuard.CheckRead(c, r.ProjectId, r.Id)) continue;
                var score = Match(r.Title, query) ? 1.0
                          : Match(r.Summary, query) ? 0.85 : 0.0;
                string? snippet = score > 0 ? SnippetOf(score >= 1.0 ? r.Title : r.Summary) : null;
                var reference = $"meeting:{r.Id}";
                int? charOff = null; double? tSec = null; int? occ = null;
                if (score == 0 || all)
                {
                    var (text, textCapped) = LoadMeetingTextCapped(r, ct);
                    if (text != null)
                    {
                        if (textCapped) { incomplete = true; incompleteReason ??= "meeting-text-capped"; }
                        if (FindFirstMatch(text, query, out var idx))
                        {
                            var cnt = CountMatches(text, query);
                            if (score == 0) { score = 0.7; snippet = SnippetAround(text, idx); }
                            charOff = idx; occ = cnt;
                            var ts = TimestampAtChar(text, idx);
                            if (ts.HasValue) { tSec = ts.Value; reference = $"meeting:{r.Id}#t={tSec.Value:0.#}"; }
                        }
                    }
                }
                if (score <= 0) continue;
                if (all && hits.Count >= take) { curKind = 1; curIdx = mtIdx - 1; resume = true; goto done; }
                total++;
                hits.Add(new McpSearchHit("meeting", reference, r.Title,
                    snippet ?? "", score, r.StartedUtc, r.ProjectId, r.Id,
                    charOff, tSec, occ));
            }
            curKind = 1; curIdx = 0;
        }

        // ── Память (kindIdx 2): FTS по mem_item. ──
        var memIdx = 0;
        if (types.HasFlag(McpSearchTypes.Memory) && curKind <= 2)
        {
            if (mem == null)
            {
                incomplete = true; incompleteReason ??= "memory-disabled";
            }
            else if (c.MemoryIncluded)
            {
                var fts = mem.SearchFts(query, take * 4).ToList();
                if (fts.Count >= take * 4) { incomplete = true; incompleteReason ??= "fts-partial"; }
                foreach (var (item, f) in fts.OrderBy(i => i.Item.Id))
                {
                    scanned++;
                    if (resume && curKind == 2 && memIdx < curIdx) { memIdx++; continue; }
                    memIdx++;
                    if (item.Status is MemoryItemStatus.Invalidated or MemoryItemStatus.Superseded)
                        continue;
                    if (!InPeriod(item.UpdatedAtUtc)) continue;
                    var pid = item.ScopeType == MemoryScopeType.Project ? item.ScopeId : null;
                    var mid = item.ScopeType == MemoryScopeType.Meeting
                        ? item.ScopeId ?? item.SourceMeetingId
                        : item.SourceMeetingId;
                    if (projectFilter != null && (pid == null || !projectFilter.Contains(pid.Value)))
                        continue;
                    if (!McpScopeGuard.CheckRead(c, pid, mid)) continue;
                    if (all && hits.Count >= take) { curKind = 2; curIdx = memIdx - 1; resume = true; goto done; }
                    total++;
                    FindFirstMatch(item.Content, query, out var off);
                    int? offVal = off >= 0 ? off : null;
                    hits.Add(new McpSearchHit("memory",
                        offVal.HasValue ? $"memory:{item.Id}#off={offVal.Value}" : $"memory:{item.Id}",
                        item.Type.ToString(),
                        offVal.HasValue ? SnippetAround(item.Content, offVal.Value) : SnippetOf(item.Content),
                        Math.Clamp(f, 0.0, 1.0), item.UpdatedAtUtc, pid, mid,
                        offVal, null, offVal.HasValue ? CountMatches(item.Content, query) : null));
                }
                curKind = 2; curIdx = 0;
            }
        }

        // ── Файлы/документы (kindIdx 3): имя+расширение и извлечённый текст. ──
        var fileIdx = 0;
        if (types.HasFlag(McpSearchTypes.Files) && _attachments != null && curKind <= 3)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var flat = rows.Where(r => r.JournalId != null)
                .SelectMany(r => _attachments.ListMeetingDocuments(r.JournalId!)
                    .Select(ma => (row: r, ma)))
                .OrderBy(x => x.ma.Doc.DocumentId, StringComparer.Ordinal)
                .ToList();
            foreach (var (r, ma) in flat)
            {
                ct.ThrowIfCancellationRequested();
                var doc = ma.Doc;
                if (!seen.Add(doc.DocumentId)) continue;
                scanned++;
                if (resume && curKind == 3 && fileIdx < curIdx) { fileIdx++; continue; }
                fileIdx++;
                if (!InPeriod(doc.CreatedUtc)) continue;
                if (!McpScopeGuard.CheckRead(c, r.ProjectId, r.Id)) continue;
                var hay = doc.DisplayName + " " + doc.Extension;
                var reference = $"attachment:{doc.DocumentId}";
                string snippet; int? off = null; int? occ = null; double score;
                if (Match(hay, query))
                {
                    score = 0.8;
                    snippet = $"{doc.Kind} В· {doc.SizeBytes} bytes";
                }
                else
                {
                    var text = LoadAttachmentText(doc.DocumentId, ct);
                    if (text == null || !FindFirstMatch(text, query, out var idx)) continue;
                    score = 0.7;
                    snippet = SnippetAround(text, idx);
                    off = idx; occ = CountMatches(text, query);
                    reference = $"attachment:{doc.DocumentId}#off={idx}";
                }
                if (all && hits.Count >= take) { curKind = 3; curIdx = fileIdx - 1; resume = true; goto done; }
                total++;
                hits.Add(new McpSearchHit("document", reference, doc.DisplayName,
                    snippet, score, doc.CreatedUtc, r.ProjectId, r.Id, off, null, occ));
            }
            curKind = 3; curIdx = 0;
        }

        resume = false; // досканировали всё — продолжения нет
        done:
        var ordered = all ? hits : hits.OrderByDescending(h => h.Score).Take(take).ToList();
        var nextCursor = resume ? $"q:{curKind}:{curIdx}:{total}" : null;
        var exhaustive = !incomplete && !resume;
        if (resume) incompleteReason ??= "more-results";
        return Task.FromResult(new McpSearchResult(ordered, nextCursor, scanned,
            exhaustive, exhaustive ? null : incompleteReason, total, resume));

        /// <summary>
        /// Курсор обхода: "q:{kindIdx}:{entityIdx}" (старый формат) или
        /// "q:{kindIdx}:{entityIdx}:{total}" — в новом формате четвёртое поле
        /// несёт накопленный счётчик совпадений, чтобы totalMatches на последней
        /// странице был полным.
        /// </summary>
        static bool ParseSearchCursor(string? c2, out int ki, out int i, out int total)
        {
            ki = 0; i = 0; total = 0;
            if (string.IsNullOrEmpty(c2) || !c2.StartsWith("q", StringComparison.Ordinal)) return false;
            var p = c2.Split(':');
            if (p.Length == 4 && int.TryParse(p[1], out ki) && int.TryParse(p[2], out i)
                && int.TryParse(p[3], out total))
                return true;
            if (p.Length == 3 && int.TryParse(p[1], out ki) && int.TryParse(p[2], out i))
            {
                total = 0;
                return true;
            }
            ki = 0; i = 0; total = 0; return false;
        }
    }

    /// <summary>Устаревшая сигнатура: читает с начала в пределах 2 MiB; дальше —
    /// явный Truncated + NextCursor (молчаливой обрезки больше нет).</summary>
    public Task<McpSourceContent> ReadSourceAsync(
        McpResolvedConnection c, string reference, CancellationToken ct)
        => ReadSourcePageAsync(c, reference, offset: 0, cursor: null,
            limitBytes: LegacyFullReadBytes, ct);

    /// <summary>
    /// Постраничное чтение (спека §6.3): ответ ≤limitBytes UTF-8 (умолчание 128 KiB,
    /// потолок 2 MiB), hasMore/nextCursor/totalChars. Продолжение — offset'ом
    /// (символы) или непрозрачным cursor'ом из прошлого ответа. Хвост, который
    /// нельзя получить (нет extracted text, текст обрезан потолком сборки) —
    /// Truncated=true + TruncatedReason, не молчаливый отброс.
    /// </summary>
    public async Task<McpSourceContent> ReadSourcePageAsync(
        McpResolvedConnection c, string reference,
        long? offset, string? cursor, int? limitBytes, CancellationToken ct)
    {
        RequireRead(c);
        // Фрагмент-якорь (#t=, #off=) — часть reference, срезаем.
        var frag = reference.IndexOf('#');
        var baseRef = frag >= 0 ? reference[..frag] : reference;
        var (kind, idText) = ParseReference(baseRef);
        long idPart = 0;
        if (kind != "attachment" && !long.TryParse(idText, out idPart))
            throw McpRpcException.InvalidParams($"bad reference id '{baseRef}'");

        var start = McpSearchCursor.DecodeSource(cursor) ?? Math.Max(0, offset ?? 0);
        var budget = Math.Clamp(limitBytes ?? DefaultPageBytes, 1, MaxPageBytes);

        string title; string? text; string mime; long? pid; long? mid;
        var capped = false; string? capReason = null;
        switch (kind)
        {
            case "meeting":
            {
                var row = LoadMeetingRows().FirstOrDefault(r => r.Id == idPart)
                          ?? LoadMemoryMeetingRow(idPart)
                          ?? throw McpRpcException.InvalidParams("meeting not found");
                if (!McpScopeGuard.CheckRead(c, row.ProjectId, row.Id))
                    throw McpRpcException.ScopeDenied("meeting not in scope");
                (text, capped) = LoadMeetingTextCapped(row, ct);
                if (capped) capReason = "meeting-text-capped";
                title = row.Title; mime = "text/plain"; pid = row.ProjectId; mid = row.Id;
                break;
            }
            case "memory":
            {
                var memStore = Mem;
                if (!c.MemoryIncluded || memStore == null)
                    throw McpRpcException.ScopeDenied("memory not included for this connection");
                var item = memStore.GetItem(idPart)
                           ?? throw McpRpcException.InvalidParams("memory item not found");
                pid = item.ScopeType == MemoryScopeType.Project ? item.ScopeId : null;
                mid = item.ScopeType == MemoryScopeType.Meeting
                    ? item.ScopeId ?? item.SourceMeetingId
                    : item.SourceMeetingId;
                if (!McpScopeGuard.CheckRead(c, pid, mid))
                    throw McpRpcException.ScopeDenied("memory item not in scope");
                var sb = new StringBuilder(item.Content);
                var tags = memStore.GetTags(item.Id);
                if (tags.Count > 0) sb.Append("\n\ntags: ").Append(string.Join(", ", tags));
                text = sb.ToString(); title = item.Type.ToString(); mime = "text/plain";
                break;
            }
            case "attachment":
            {
                var doc = _attachments?.Get(idText);
                if (doc == null || doc.Deleted)
                    throw McpRpcException.InvalidParams("attachment not found");
                var meetingIds = _attachments!.ListMeetingIdsFor(idText);
                var rows = LoadMeetingRows()
                    .Where(r => r.JournalId != null && meetingIds.Contains(r.JournalId))
                    .ToList();
                var allowed = rows.FirstOrDefault(r => McpScopeGuard.CheckRead(c, r.ProjectId, r.Id))
                              ?? throw McpRpcException.ScopeDenied("attachment not in scope");
                text = await LoadAttachmentTextAsync(idText, ct).ConfigureAwait(false);
                if (text == null) capReason = "no-extracted-text";
                mime = doc.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? "application/pdf" : "application/octet-stream";
                title = doc.DisplayName; pid = allowed.ProjectId; mid = allowed.Id;
                break;
            }
            default:
                throw McpRpcException.InvalidParams($"unknown reference kind '{kind}'");
        }

        if (text == null)
        {
            return new McpSourceContent(baseRef, title, null, null, mime, pid, mid,
                Offset: 0, TotalChars: 0, HasMore: false, NextCursor: null,
                Truncated: true, TruncatedReason: capReason ?? "no-text");
        }
        if (start >= text.Length && text.Length > 0)
        {
            return new McpSourceContent(baseRef, title, "", null, mime, pid, mid,
                Offset: start, TotalChars: text.Length, HasMore: false, NextCursor: null);
        }
        var (chunk, next) = McpToolResultStore.SliceUtf8(text, (int)Math.Min(start, text.Length), budget);
        var hasMore = next < text.Length;
        var truncated = !hasMore && capped;
        return new McpSourceContent(baseRef, title, chunk, null, mime, pid, mid,
            Offset: start, TotalChars: text.Length, HasMore: hasMore,
            NextCursor: hasMore ? McpSearchCursor.EncodeSource(next) : null,
            Truncated: truncated, TruncatedReason: truncated ? capReason : null);
    }

    /// <summary>Вложения встречи + статус извлечённого текста (buddy_list_documents).</summary>
    public Task<IReadOnlyList<McpMeetingDocumentItem>> ListMeetingDocumentsAsync(
        McpResolvedConnection c, string meetingRef, CancellationToken ct)
    {
        RequireRead(c);
        var row = ResolveMeeting(meetingRef)
                  ?? throw McpRpcException.InvalidParams($"meeting '{meetingRef}' not found");
        if (!McpScopeGuard.CheckRead(c, row.ProjectId, row.Id))
            throw McpRpcException.ScopeDenied("meeting not in scope");
        var result = new List<McpMeetingDocumentItem>();
        if (_attachments == null || row.JournalId == null)
            return Task.FromResult<IReadOnlyList<McpMeetingDocumentItem>>(result);
        foreach (var ma in _attachments.ListMeetingDocuments(row.JournalId))
        {
            ct.ThrowIfCancellationRequested();
            var d = ma.Doc;
            var status = d.Deleted ? "deleted"
                : d.CharCount > 0 ? "ok" : "not-extracted";
            result.Add(new McpMeetingDocumentItem(
                $"attachment:{d.DocumentId}", d.DocumentId, d.DisplayName,
                d.Extension, d.Kind.ToString().ToLowerInvariant(), d.SizeBytes, d.PageCount,
                d.CharCount > 0 ? d.CharCount : null, status,
                row.ProjectId, row.Id, ma.AddedUtc));
        }
        return Task.FromResult<IReadOnlyList<McpMeetingDocumentItem>>(result);
    }

    /// <summary>
    /// buddy_add_fact: идемпотентная запись факта. Дедуп — совпадение текста
    /// (OrdinalIgnoreCase, trim) в том же scope → Duplicate=true, существующий id.
    /// </summary>
    public Task<McpFactAddResult> AddFactAsync(
        McpResolvedConnection c, string text,
        string? sourceRef, long? projectId, long? meetingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw McpRpcException.InvalidParams("text is empty");
        if (!c.Rights.Contains(McpRight.AddFact) || c.Suspended)
            throw McpRpcException.ScopeDenied("no add_fact right");
        var mem = Mem;
        if (!c.MemoryIncluded || mem == null)
            throw McpRpcException.ScopeDenied("memory not included for this connection");
        long? srcMeeting = null; string? srcDoc = null;
        if (!string.IsNullOrEmpty(sourceRef))
        {
            try
            {
                var (k, idt) = ParseReference(sourceRef);
                var idOnly = idt.Split('#')[0];
                if (k == "meeting" && long.TryParse(idOnly, out var m2)) srcMeeting = m2;
                else if (k == "attachment") srcDoc = idOnly;
            }
            catch (McpRpcException) { /* незнакомый ref — остаётся anchor'ом */ }
        }
        var mid = meetingId ?? srcMeeting;
        var scope = projectId.HasValue ? MemoryScopeType.Project
            : mid.HasValue ? MemoryScopeType.Meeting
            : MemoryScopeType.Global;
        var scopeId = scope == MemoryScopeType.Project ? projectId : mid;
        if (!McpScopeGuard.CheckScope(c, projectId, mid))
            throw McpRpcException.ScopeDenied("scope not allowed for this connection");

        var trimmed = text.Trim();
        var existing = mem.SearchFts(trimmed, limit: 20)
            .Select(f => f.Item)
            .Concat(mem.ListItems(scope, scopeId, MemoryItemStatus.Active, limit: 200)
                .Where(i => i.Content.StartsWith(
                    trimmed[..Math.Min(40, trimmed.Length)], StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(i => i.Id);
        var dup = existing.FirstOrDefault(i =>
            string.Equals(i.Content.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)
            && i.Status != MemoryItemStatus.Invalidated);
        var scopeName = scope.ToString().ToLowerInvariant();
        if (dup != null)
            return Task.FromResult(new McpFactAddResult(dup.Id.ToString(), true,
                scopeName, $"memory:{dup.Id}"));

        var item = new MemoryItem
        {
            ScopeType = scope,
            ScopeId = scopeId,
            Type = MemoryItemType.Fact,
            Content = trimmed,
            Importance = 0.6,
            Confidence = 0.8,
            Source = "mcp",
            SourceMeetingId = srcMeeting ?? (scope == MemoryScopeType.Meeting ? mid : null),
            SourceDocumentId = srcDoc,
            SourceAnchor = sourceRef,
        };
        var id = mem.InsertItem(item);
        return Task.FromResult(new McpFactAddResult(id.ToString(), false,
            scopeName, $"memory:{id}"));
    }

    /// <summary>
    /// buddy_update_fact: замена текста факта по его id. Право change +
    /// область подключения; факт вне области — отказ (SCOPE_DENIED).
    /// </summary>
    public Task<McpFactUpdateResult> UpdateFactAsync(
        McpResolvedConnection c, string factId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw McpRpcException.InvalidParams("text is empty");
        if (!McpScopeGuard.CheckRight(c, McpRight.Change))
            throw McpRpcException.ScopeDenied("no change right");
        var mem = Mem;
        if (!c.MemoryIncluded || mem == null)
            throw McpRpcException.ScopeDenied("memory not included for this connection");
        if (!TryParseFactId(factId, out var id))
            throw McpRpcException.InvalidParams("factId must be a numeric id");
        var item = mem.GetItem(id);
        if (item == null)
            return Task.FromResult(new McpFactUpdateResult(id.ToString(), false, true, "",
                "", "Факт не найден"));
        var (projectId, meetingId) = FactScope(mem, item);
        if (!McpScopeGuard.CheckScope(c, projectId, meetingId))
            throw McpRpcException.ScopeDenied("fact not in scope");
        if (item.Status != MemoryItemStatus.Active && item.Status != MemoryItemStatus.Pending)
            return Task.FromResult(new McpFactUpdateResult(id.ToString(), false, true,
                ScopeName(item), $"memory:{id}", "Факт неактивен (удалён или заменён)"));
        item.Content = text.Trim();
        // Правка текста факта — как ручная правка в центре памяти: тот же Source.
        item.Source = "user-edited";
        item.UpdatedAtUtc = DateTime.UtcNow;
        mem.UpdateItem(item);
        return Task.FromResult(new McpFactUpdateResult(id.ToString(), true, false,
            ScopeName(item), $"memory:{id}", "Факт обновлён"));
    }

    /// <summary>
    /// buddy_delete_fact: мягкое удаление факта (статус → Invalidated, запись
    /// остаётся в базе). Требует права delete, попадания в область и явного
    /// подтверждения владельца; отказ/отмена/таймаут — факт остаётся.
    /// </summary>
    public async Task<McpFactDeleteResult> DeleteFactAsync(
        McpResolvedConnection c, string factId, CancellationToken ct)
    {
        if (!McpScopeGuard.CheckRight(c, McpRight.Delete))
            throw McpRpcException.ScopeDenied("no delete right");
        var mem = Mem;
        if (!c.MemoryIncluded || mem == null)
            throw McpRpcException.ScopeDenied("memory not included for this connection");
        if (!TryParseFactId(factId, out var id))
            throw McpRpcException.InvalidParams("factId must be a numeric id");
        // Без карточки подтверждения удаление запрещено — молчаливого удаления нет.
        if (_approvalHandler == null)
            throw McpRpcException.ScopeDenied("owner approval is unavailable — delete rejected");
        var item = mem.GetItem(id);
        if (item == null)
            return new McpFactDeleteResult(id.ToString(), false, true, "",
                "", "Факт не найден");
        var (projectId, meetingId) = FactScope(mem, item);
        if (!McpScopeGuard.CheckScope(c, projectId, meetingId))
            throw McpRpcException.ScopeDenied("fact not in scope");
        if (item.Status == MemoryItemStatus.Invalidated)
            return new McpFactDeleteResult(id.ToString(), false, true,
                ScopeName(item), $"memory:{id}", "Факт уже удалён");
        await RequestApprovalAsync(new McpApprovalRequest(
            Summary: $"Удалить факт #{id}?",
            Details: TruncateFactText(item.Content),
            Operation: "delete-fact"), ct).ConfigureAwait(false);
        mem.SetStatus(id, MemoryItemStatus.Invalidated);
        return new McpFactDeleteResult(id.ToString(), true, false,
            ScopeName(item), $"memory:{id}", "Факт помечен удалённым");
    }

    /// <summary>Делегат подтверждения опасных операций (карточка в оверлее).</summary>
    public void SetApprovalHandler(Func<McpApprovalRequest, Task<bool>>? handler)
        => _approvalHandler = handler;

    /// <summary>
    /// Подтверждение владельца. Отказ, отмена или таймаут (5 минут) → отказ
    /// операции (McpRpcException): данные не меняются, запрос не висит.
    /// </summary>
    private async Task RequestApprovalAsync(McpApprovalRequest request, CancellationToken ct)
    {
        var handler = _approvalHandler;
        if (handler == null)
            throw McpRpcException.ScopeDenied("owner approval is unavailable — operation rejected");
        Task<bool> answer;
        try
        {
            answer = handler(request) ?? Task.FromResult(false);
        }
        catch (Exception ex)
        {
            throw McpRpcException.ScopeDenied($"approval request failed: {ex.Message}");
        }
        var done = await Task.WhenAny(answer, Task.Delay(ApprovalTimeout, ct)).ConfigureAwait(false);
        if (done != answer)
        {
            ct.ThrowIfCancellationRequested();
            throw McpRpcException.ScopeDenied("owner approval timed out — operation rejected");
        }
        bool approved;
        try
        {
            approved = await answer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw McpRpcException.ScopeDenied($"approval request failed: {ex.Message}");
        }
        if (!approved)
            throw McpRpcException.ScopeDenied("owner rejected the operation — data kept");
    }

    /// <summary>id факта из строки (числовой) — иначе ошибка параметров.</summary>
    private static bool TryParseFactId(string? factId, out long id)
    {
        id = 0;
        var s = factId?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        // Принимаем и reference вида "memory:42" — агенты часто копируют его целиком.
        var sep = s.LastIndexOf(':');
        if (sep >= 0 && sep < s.Length - 1) s = s[(sep + 1)..].Trim();
        return long.TryParse(s, out id) && id > 0;
    }

    /// <summary>Область факта в терминах подключения: (проект, встреча).</summary>
    private static (long? ProjectId, long? MeetingId) FactScope(IMemoryStore mem, MemoryItem item)
    {
        switch (item.ScopeType)
        {
            case MemoryScopeType.Project:
                return (item.ScopeId, null);
            case MemoryScopeType.Meeting:
            {
                var mid = item.ScopeId;
                var pid = mid != null ? mem.GetMeeting(mid.Value)?.ProjectId : null;
                return (pid, mid);
            }
            default:
                return (null, null);
        }
    }

    /// <summary>Имя области памяти для ответа ("global"|"project"|"meeting").</summary>
    private static string ScopeName(MemoryItem item)
        => item.ScopeType.ToString().ToLowerInvariant();

    /// <summary>Текст факта для карточки подтверждения (обрезан).</summary>
    private static string TruncateFactText(string? text)
    {
        var s = (text ?? "").Trim();
        return s.Length <= 300 ? s : s[..300] + "…";
    }

    /// <summary>resume/continue: вернуть джобу в очередь (McpJobStore.Requeue).</summary>
    public Task<McpJob?> ResumeJobAsync(McpResolvedConnection c, string jobId, CancellationToken ct)
    {
        var job = _jobs.Get(jobId);
        if (job == null) return Task.FromResult<McpJob?>(null);
        if (job.ConnectionId != c.Id)
            throw McpRpcException.ScopeDenied("job belongs to another connection");
        if (_jobs.Requeue(jobId) && IsBuiltinJobKind(job.Kind))
            _ = RunJobWorkerAsync(c, jobId);
        return Task.FromResult<McpJob?>(_jobs.Get(jobId));
    }

    /// <summary>Страница сохранённого полного результата инструмента.</summary>
    public Task<McpToolResultPage> ReadToolResultAsync(
        McpResolvedConnection c, string id, long offset, int? limitBytes, CancellationToken ct)
    {
        var page = _toolResults.ReadPage(id, offset, limitBytes)
                   ?? throw McpRpcException.InvalidParams($"tool result '{id}' not found");
        var entry = _toolResults.GetEntry(id);
        if (entry?.JobId != null)
        {
            var job = _jobs.Get(entry.JobId);
            if (job != null && job.ConnectionId != c.Id)
                throw McpRpcException.ScopeDenied("tool result belongs to another connection");
        }
        return Task.FromResult(page);
    }

    public Task<McpJob> EnqueueJobAsync(
        McpResolvedConnection c, string kind, string argsJson, string idempotencyKey, CancellationToken ct)
    {
        // Исполнительские kinds переживают рестарт (checkpoint → WaitingResume).
        var resumable = kind is "transcribe" or "extract_facts" or "research";
        var job = _jobs.Enqueue(c.Id, kind ?? "custom", argsJson ?? "{}", idempotencyKey,
            resumable: resumable);
        // Только что созданное задание подхватывает воркер; повтор по
        // idempotency-key возвращает существующее — второй запуск не нужен.
        // Внешние runner'ы (transcribe/extract_facts) встроенным не стартуем.
        if (job.State == McpJobState.Queued && IsBuiltinJobKind(job.Kind))
            _ = RunJobWorkerAsync(c, job.Id);
        return Task.FromResult(job);
    }

    public Task<McpJob?> GetJobAsync(McpResolvedConnection c, string jobId, CancellationToken ct)
    {
        var job = _jobs.Get(jobId);
        if (job != null && job.ConnectionId != c.Id)
            throw McpRpcException.ScopeDenied("job belongs to another connection");
        return Task.FromResult(job);
    }

    public Task<bool> CancelJobAsync(McpResolvedConnection c, string jobId, CancellationToken ct)
    {
        var job = _jobs.Get(jobId);
        if (job != null && job.ConnectionId != c.Id)
            throw McpRpcException.ScopeDenied("job belongs to another connection");
        return Task.FromResult(_jobs.Cancel(jobId));
    }

    // ── исполнитель заданий ─────────────────────────────────────────────────

    /// <summary>
    /// Минимальный встроенный раннер: задания выполняются строго последовательно
    /// (semaphore), результат — в jobs.jsonl. Не заменяет будущий полноценный
    /// движок задач (approvals/budget), но даёт честные переходы состояний для
    /// search/research уже сейчас.
    /// </summary>
    private async Task RunJobWorkerAsync(McpResolvedConnection c, string jobId)
    {
        try
        {
            await _jobWorker.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            var job = _jobs.Get(jobId);
            if (job == null || job.State != McpJobState.Queued) return;
            _jobs.Update(jobId, McpJobState.Running, stage: "executing");
            try
            {
                var result = await ExecuteJobAsync(c, job, _disposeCts.Token).ConfigureAwait(false);
                _jobs.Update(jobId, McpJobState.Succeeded, stage: "done", resultJson: result);
            }
            catch (OperationCanceledException) { /* shutdown — статус решит реплей */ }
            catch (Exception ex)
            {
                _jobs.Update(jobId, McpJobState.Failed, error: ex.Message);
                _log?.Warn($"MCP job {jobId} failed: {ex.Message}", "Mcp");
            }
        }
        finally
        {
            _jobWorker.Release();
        }
    }

    private async Task<string> ExecuteJobAsync(McpResolvedConnection c, McpJob job, CancellationToken ct)
    {
        var kind = job.Kind?.ToLowerInvariant() ?? "";
        var args = ParseArgsJson(job.ArgsJson);
        switch (kind)
        {
            case "search":
            {
                var query = ArgString(args, "query") ?? "";
                var res = await SearchAsync(c, query, McpSearchTypes.All, null, 20, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(res);
            }
            case "research":
            {
                var query = ArgString(args, "query") ?? "";
                var res = await SearchAsync(c, query, McpSearchTypes.All, null, 5, ct).ConfigureAwait(false);
                _jobs.Update(job.Id, McpJobState.Running, stage: $"found {res.Hits.Count} hits",
                    done: 0, total: res.Hits.Count);
                string? topText = null;
                if (res.Hits.Count > 0)
                {
                    var src = await ReadSourceAsync(c, res.Hits[0].Reference, ct).ConfigureAwait(false);
                    topText = src.TextContent is { Length: > 4000 } t ? t[..4000] : src.TextContent;
                }
                _jobs.Update(job.Id, McpJobState.Running, stage: "reading top hit",
                    done: res.Hits.Count, total: res.Hits.Count);
                return JsonSerializer.Serialize(new { hits = res.Hits, topReferenceContent = topText });
            }
            default:
                throw new NotSupportedException($"job kind '{job.Kind}' is not supported by the built-in runner");
        }
    }

    // ── источники данных ────────────────────────────────────────────────────

    /// <summary>
    /// Единый список встреч: mem_meeting (когда память активна) + журнальные
    /// встречи без строки в mem_meeting. JournalId связывает миры:
    /// mem_meeting.journal_path — абсолютный путь jsonl, журнальный id = basename.
    /// </summary>
    private List<MeetingRow> LoadMeetingRows()
    {
        var journals = _transcripts.LoadAll();
        var journalById = new Dictionary<string, MeetingInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in journals)
            journalById[j.Id] = j;
        var attachCounts = SafeCountByMeeting();
        var rows = new List<MeetingRow>();
        var seenJournals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var mem = Mem;
        if (mem != null)
        {
            foreach (var m in mem.ListMeetings(1000))
            {
                var journalId = JournalIdFromPath(m.JournalPath);
                journalById.TryGetValue(journalId ?? "", out var info);
                if (journalId != null) seenJournals.Add(journalId);
                rows.Add(new MeetingRow
                {
                    Id = m.Id,
                    JournalId = journalId ?? info?.Id,
                    JournalPath = !string.IsNullOrEmpty(m.JournalPath) ? m.JournalPath : info?.JournalPath,
                    ProjectId = m.ProjectId,
                    Title = string.IsNullOrEmpty(m.Title)
                        ? (info != null ? JournalTitle(info) : $"Meeting {m.Id}")
                        : m.Title,
                    StartedUtc = m.StartedAtUtc != default ? m.StartedAtUtc
                        : info?.StartedAt ?? m.StartedAtUtc,
                    DurationSec = m.EndedAtUtc.HasValue
                        ? Math.Max(0, (int)(m.EndedAtUtc.Value - m.StartedAtUtc).TotalSeconds)
                        : info != null ? Math.Max(0, (int)(info.LastLineAt - info.StartedAt).TotalSeconds) : 0,
                    LineCount = info?.LineCount ?? 0,
                    AttachmentCount = journalId != null && attachCounts.TryGetValue(journalId, out var ac) ? ac : 0,
                    Summary = string.IsNullOrEmpty(m.Summary) ? info?.Summary ?? "" : m.Summary,
                });
            }
        }

        // Журнальные встречи вне memory.db: стабильный отрицательный id.
        foreach (var j in journals)
        {
            if (seenJournals.Contains(j.Id)) continue;
            var mid = -StableId64(j.Id);
            rows.Add(new MeetingRow
            {
                Id = mid,
                JournalId = j.Id,
                JournalPath = j.JournalPath,
                ProjectId = null,
                Title = JournalTitle(j),
                StartedUtc = j.StartedAt,
                DurationSec = Math.Max(0, (int)(j.LastLineAt - j.StartedAt).TotalSeconds),
                LineCount = j.LineCount,
                AttachmentCount = attachCounts.TryGetValue(j.Id, out var ac2) ? ac2 : 0,
                Summary = j.Summary ?? "",
            });
        }
        return rows;
    }

    private MeetingRow? LoadMemoryMeetingRow(long memId)
    {
        var m = Mem?.GetMeeting(memId);
        if (m == null) return null;
        return new MeetingRow
        {
            Id = m.Id,
            JournalId = JournalIdFromPath(m.JournalPath),
            JournalPath = m.JournalPath,
            ProjectId = m.ProjectId,
            Title = string.IsNullOrEmpty(m.Title) ? $"Meeting {m.Id}" : m.Title,
            StartedUtc = m.StartedAtUtc,
            Summary = m.Summary,
        };
    }


    private IReadOnlyDictionary<string, int> SafeCountByMeeting()
    {
        try { return _attachments?.CountByMeeting() ?? new Dictionary<string, int>(); }
        catch { return new Dictionary<string, int>(); }
    }

    // в”Ђв”Ђ helpers в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    private static string JournalTitle(MeetingInfo j)
        => j.StartedAt != default ? j.StartedAt.ToString("dd.MM.yyyy HH:mm") : j.Id;

    /// <summary>Журнальный id = basename jsonl без расширения; "" если путь пуст/чужой.</summary>
    internal static string? JournalIdFromPath(string? journalPath)
    {
        if (string.IsNullOrEmpty(journalPath)) return null;
        var name = Path.GetFileNameWithoutExtension(journalPath);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>FNV-1a 64 → положительный long (для отрицательных journal-only id).</summary>
    internal static long StableId64(string s)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            var v = (long)(h & 0x7FFFFFFFFFFFFFFFUL);
            return v == 0 ? 1 : v;
        }
    }

    private static bool Match(string? haystack, string query)
        => !string.IsNullOrEmpty(haystack) &&
           haystack.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string SnippetOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.Replace('\n', ' ').Trim();
        return t.Length <= SnippetLen ? t : t[..SnippetLen] + "...";
    }


    private static int ParseCursor(string? cursor)
        => int.TryParse(cursor, out var n) && n > 0 ? n : 0;

    /// <summary>"meeting:123" → ("meeting", "123"). Id текстовый — для attachment это documentId.</summary>
    private static (string kind, string idText) ParseReference(string reference)
    {
        var sep = reference.IndexOf(':');
        if (sep <= 0 || sep == reference.Length - 1)
            throw McpRpcException.InvalidParams($"bad reference '{reference}'");
        return (reference[..sep].ToLowerInvariant(), reference[(sep + 1)..]);
    }


    private string? ArgString(Dictionary<string, JsonElement>? args, string name)
    {
        if (args != null && args.TryGetValue(name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static Dictionary<string, JsonElement>? ParseArgsJson(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson); }
        catch { return null; }
    }


    // ── входящий HTTP + карточки состояния (IMcpFacade, спека §6.1) ─────────

    /// <summary>Общий с pipe dispatch: тот же routing facade.*, auth по resolved conn.</summary>
    public Task<string?> TryDispatchHttpJsonRpcAsync(
        string json, McpResolvedConnection conn, CancellationToken ct)
        => HttpDispatch().DispatchLineAsync(json, conn, ct);

    /// <summary>Конвейер dispatch (pipe/HTTP) зовёт это имя для facade.* методов.
    /// Методы спеки §6.4 обслуживаем здесь; неизвестные — общему dispatch'у.</summary>
    public async Task<JsonElement> InvokeFacadeAsync(
        string method, JsonElement prms, McpResolvedConnection conn, CancellationToken ct)
    {
        // pipe/HTTP-диспетчер зовёт «facade.<method>» — префикс снимаем здесь,
        // иначе богатая реализация (mode/cursor/type/limit) не попадает
        // в собственный switch и запрос уходит в общий dispatch.
        var m = method.StartsWith("facade.", StringComparison.Ordinal)
            ? method["facade.".Length..]
            : method;
        switch (m)
        {
            case "search":
                return Serialize(await SearchAsync(conn, BuildSearchRequest(prms), ct).ConfigureAwait(false));
            case "readSource":
                return Serialize(await ReadSourcePageAsync(conn, Param(prms, "reference") ?? "",
                    ParamLong(prms, "offset"), Param(prms, "cursor"),
                    ParamInt(prms, "limitBytes") ?? ParamInt(prms, "limit"), ct).ConfigureAwait(false));
            case "listMeetingDocuments":
                return Serialize(await ListMeetingDocumentsAsync(conn,
                    Param(prms, "meetingRef") ?? Param(prms, "meetingId") ?? "", ct).ConfigureAwait(false));
            case "addFact":
                return Serialize(await AddFactAsync(conn, Param(prms, "text") ?? "",
                    Param(prms, "sourceRef"), ParamLong(prms, "projectId"),
                    ParamLong(prms, "meetingId"), ct).ConfigureAwait(false));
            case "updateFact":
                return Serialize(await UpdateFactAsync(conn,
                    Param(prms, "factId") ?? Param(prms, "id") ?? "",
                    Param(prms, "text") ?? "", ct).ConfigureAwait(false));
            case "deleteFact":
                return Serialize(await DeleteFactAsync(conn,
                    Param(prms, "factId") ?? Param(prms, "id") ?? "", ct).ConfigureAwait(false));
            case "resumeJob":
                return Serialize(await ResumeJobAsync(conn, Param(prms, "jobId") ?? "", ct).ConfigureAwait(false));
            case "readToolResult":
                return Serialize(await ReadToolResultAsync(conn, Param(prms, "id") ?? "",
                    ParamLong(prms, "offset") ?? 0, ParamInt(prms, "limitBytes"), ct).ConfigureAwait(false));
            case "enqueueJob":
            {
                var k = Param(prms, "kind") ?? "custom";
                if (k is "resume" or "continue")
                    return Serialize(await ResumeJobAsync(conn, Param(prms, "jobId") ?? "", ct).ConfigureAwait(false));
                ValidateJobKind(k, prms);
                return Serialize(await EnqueueJobAsync(conn, k,
                    Param(prms, "argsJson") ?? Param(prms, "args") ?? "{}",
                    Param(prms, "idempotencyKey") ?? "", ct).ConfigureAwait(false));
            }
            default:
                // Неизвестные facade-методы — в общий dispatch (status/listMeetings/
                // enqueueJob...); его default даёт -32601 MethodNotFound, а не
                // -32602 от ResolveAuth (для unknown method токен не обязателен).
                return await HttpDispatch().InvokeFacadeAsync(m, prms, conn, ct)
                    .ConfigureAwait(false);
        }

        static string? Param(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                : null;
        static long? ParamLong(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
                    : long.TryParse(v.GetString(), out var n2) ? n2 : null
                : null;
        static int? ParamInt(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
                    : int.TryParse(v.GetString(), out var n2) ? n2 : null
                : null;
        static JsonElement Serialize(object? v) =>
            JsonSerializer.SerializeToElement(v, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>HTTP-вход зовёт общий dispatch; расширенные методы перехватываем в фасаде.</summary>
    private McpJsonRpcDispatch HttpDispatch()
    {
        if (_httpDispatch != null) return _httpDispatch;
        lock (_presenceGate)
            return _httpDispatch ??= new McpJsonRpcDispatch(
                this, permissions: null, connections: null, _log, transport: "http", journal: _journal);
    }

    /// <summary>
    /// Сборка расширенного запроса поиска из params диспетчера.
    /// Общая точка для pipe/HTTP-диспетчера и фасада: одинаковые имена
    /// параметров (query/type(s)/mode/limit/cursor/from/to/projectIds) и
    /// одинаковые значения по умолчанию — описание инструмента не может
    /// разойтись с поведением.
    /// </summary>
    internal static McpSearchRequest BuildSearchRequest(JsonElement prms)
    {
        string? q = null, cursor = null, mode = null;
        McpSearchTypes types = McpSearchTypes.Meetings | McpSearchTypes.Memory;
        var limit = 50; DateTime? from = null, to = null; List<long>? pids = null;
        if (prms.ValueKind == JsonValueKind.Object)
        {
            if (prms.TryGetProperty("query", out var e)) q = e.GetString();
            if (prms.TryGetProperty("cursor", out e)) cursor = e.GetString();
            if (prms.TryGetProperty("mode", out e)) mode = e.GetString();
            if (prms.TryGetProperty("limit", out e) && e.TryGetInt32(out var l)) limit = l;
            if (prms.TryGetProperty("types", out e) || prms.TryGetProperty("type", out e))
            {
                var t = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (t != null)
                {
                    types = 0;
                    foreach (var part in t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        types |= part.ToLowerInvariant() switch
                        {
                            "transcript" or "meeting" or "meetings" => McpSearchTypes.Meetings,
                            "memory" or "fact" or "facts" => McpSearchTypes.Memory,
                            "document" or "file" or "files" or "attachment" => McpSearchTypes.Files,
                            "project" or "projects" => McpSearchTypes.Projects,
                            "all" => McpSearchTypes.All,
                            _ => 0,
                        };
                    if (types == 0) types = McpSearchTypes.Meetings | McpSearchTypes.Memory;
                }
            }
            if (prms.TryGetProperty("fromUtc", out e) || prms.TryGetProperty("from", out e))
                if (DateTime.TryParse(e.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) from = d;
            if (prms.TryGetProperty("toUtc", out e) || prms.TryGetProperty("to", out e))
                if (DateTime.TryParse(e.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)) to = d;
            if (prms.TryGetProperty("projectIds", out e) && e.ValueKind == JsonValueKind.Array)
            {
                pids = [];
                foreach (var el in e.EnumerateArray())
                    if (el.TryGetInt64(out var id)) pids.Add(id);
            }
            else if (prms.TryGetProperty("projectId", out e) && e.TryGetInt64(out var one))
                pids = [one];
        }
        return new McpSearchRequest(q ?? "", types, pids, limit, mode, from, to, cursor);
    }

    /// <summary>Разрешённые kind'ы очереди + валидация обязательных аргументов.</summary>
    private static void ValidateJobKind(string kind, JsonElement prms)
    {
        switch (kind)
        {
            case "search":
            case "research":
            case "custom":
            case "tool-call":
                return;
            case "transcribe":
            {
                var hasFile = prms.TryGetProperty("filePath", out _) || prms.TryGetProperty("file_path", out _);
                var hasImport = prms.TryGetProperty("importId", out _) || prms.TryGetProperty("import_id", out _);
                if (!hasFile && !hasImport)
                    throw McpRpcException.InvalidParams("transcribe: filePath or importId required");
                return;
            }
            case "extract_facts":
                return; // meetingId|all — оба опциональны
            default:
                throw McpRpcException.InvalidParams($"unknown job kind '{kind}'");
        }
    }

    /// <summary>kind'ы, исполняемые встроенным последовательным воркером.</summary>
    private static bool IsBuiltinJobKind(string? kind) =>
        kind is "search" or "research" or "custom" or "tool-call";

    /// <summary>Handshake/аутентичный контакт: фиксирует транспорт; «Подключено» НЕ ставит.</summary>
    public void UpdateConnectionLastSeen(string connectionId, string transport)
    {
        if (string.IsNullOrEmpty(connectionId)) return;
        lock (_presenceGate)
            _seenTransport[connectionId] = string.IsNullOrEmpty(transport) ? "unknown" : transport;
    }

    /// <summary>
    /// Реальный запрос после handshake — единственное место, где ставится
    /// «Подключено» и persisted entry.LastSeenUtc («время последнего обращения»).
    /// </summary>
    public void SetLastRequest(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId)) return;
        var now = DateTime.UtcNow;
        lock (_presenceGate)
            _lastRequestUtc[connectionId] = now;
        var entry = _config.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (entry != null) entry.LastSeenUtc = now;
        // Сигнал UI: карточка подключения обновляет статус без переоткрытия окна.
        // Подписчик не должен ломать путь запроса — его исключения гасим.
        try { RequestObserved?.Invoke(connectionId, now); }
        catch (Exception ex) { _log?.Warn($"MCP request-observer failed: {ex.Message}", "Mcp"); }
    }

    /// <summary>Реальный запрос клиента (id подключения, UTC) — сигнал карточкам в UI.</summary>
    public event Action<string, DateTime>? RequestObserved;

    /// <summary>Транспорт здоров/упал: только для «Нет связи» с причиной, «Подключено» не ставит.</summary>
    public void ReportTransportState(string transport, bool alive, string? reason = null)
    {
        if (string.IsNullOrEmpty(transport)) return;
        lock (_presenceGate)
            _transportState[transport] = (alive, reason);
    }

    /// <summary>Последний транспорт контакта подключения (pipe|http|unknown).</summary>
    private string TransportOf(string connectionId)
    {
        lock (_presenceGate)
            return _seenTransport.TryGetValue(connectionId, out var t) ? t : "unknown";
    }

    /// <summary>
    /// Состояния подключений по спеке §6.1: «Подключено» требует реального
    /// запроса после handshake — entry.LastSeenUtc/_lastRequestUtc пишутся
    /// только SetLastRequest, факт запущенного listener'а статус не ставит.
    /// </summary>
    public IReadOnlyList<McpConnectionStatus> GetConnectionStatuses()
    {
        var result = new List<McpConnectionStatus>(Math.Max(1, _config.Connections.Count));
        foreach (var c in _config.Connections)
        {
            DateTime? lastRequest;
            lock (_presenceGate)
                lastRequest = _lastRequestUtc.TryGetValue(c.Id, out var t) ? t : c.LastSeenUtc;

            var presence = PresenceOf(c, lastRequest, out var reason);
            result.Add(new McpConnectionStatus(
                c.Id,
                string.IsNullOrWhiteSpace(c.Name) ? c.ClientKind : c.Name,
                TransportOf(c.Id),
                presence,
                lastRequest,
                reason));
        }
        return result;
    }

    private McpConnectionPresence PresenceOf(
        McpConnectionEntry c, DateTime? lastRequest, out string? reason)
    {
        reason = null;
        if (c.Suspended) { reason = "подключение приостановлено"; return McpConnectionPresence.NoConnection; }
        // Доступ отозван: запись в конфиге есть, а гранта (токена) уже нет.
        if (_permissions != null && !_permissions.HasGrant(c.Id))
            return McpConnectionPresence.Revoked;
        if (lastRequest == null) return McpConnectionPresence.AwaitingConnection;
        // Был реальный запрос: транспорт, по которому он шёл, должен быть жив.
        var transport = TransportOf(c.Id);
        if (TransportDown(transport, out var down))
        {
            reason = down;
            return McpConnectionPresence.NoConnection;
        }
        return McpConnectionPresence.Connected;
    }

    private bool TransportDown(string transport, out string? reason)
    {
        reason = null;
        if (transport is "pipe" or "http" or "funnel" or "tailscale-funnel")
        {
            lock (_presenceGate)
            {
                if (_transportState.TryGetValue(transport, out var st) && !st.Alive)
                {
                    reason = st.Reason ?? $"транспорт {transport} недоступен";
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Живое состояние HTTP-ingress: listener + tunnel из конфига и репортов.</summary>
    public McpHttpStatus GetHttpStatus()
    {
        var local = $"http://127.0.0.1:{_config.HttpPort}/mcp";
        var mode = _config.TunnelMode;
        if (mode == "tailscale") mode = "tailscale-funnel"; // легаси-значение мастера
        (bool alive, string? reason) listener = (true, null);
        (bool alive, string? reason) funnel = (true, null);
        lock (_presenceGate)
        {
            if (_transportState.TryGetValue("http", out var l)) listener = l;
            if (_transportState.TryGetValue("funnel", out var f)) funnel = f;
            if (_transportState.TryGetValue("tailscale-funnel", out var f2)) funnel = f2;
        }
        string? publicUrl = mode switch
        {
            "manual" => string.IsNullOrWhiteSpace(_config.PublicBaseUrl) ? null : _config.PublicBaseUrl.TrimEnd('/'),
            "tailscale-funnel" => funnel.alive
                ? (string.IsNullOrWhiteSpace(_config.PublicBaseUrl) ? null : _config.PublicBaseUrl.TrimEnd('/'))
                : null,
            _ => null,
        };
        var down = !listener.alive ? listener.reason
            : mode == "tailscale-funnel" && !funnel.alive ? funnel.reason
            : mode == "manual" && publicUrl == null ? "не задан PublicBaseUrl"
            : null;
        return new McpHttpStatus(
            _config.HttpEnabled && listener.alive,
            _config.HttpEnabled ? local : null,
            publicUrl,
            mode,
            down);
    }

    // в”Ђв”Ђ OAuth first-contact consent (HTTP) в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

    /// <summary>HTTP-listener регистрирует auth store как источник pending-консентов.</summary>
    public void SetPendingClientSource(IPendingClientSource? source)
    {
        IPendingClientSource? prev;
        lock (_presenceGate)
        {
            prev = _pendingSource;
            _pendingSource = source;
        }
        if (prev != null) prev.PendingAdded -= OnPendingAdded;
        if (source != null) source.PendingAdded += OnPendingAdded;
    }

    private void OnPendingAdded(McpPendingClient p) => PendingClientAdded?.Invoke(p);

    /// <summary>Ожидающие решения владельца OAuth-клиенты (пусто без HTTP-слушателя).</summary>
    public IReadOnlyList<McpPendingClient> ListPendingClients()
    {
        lock (_presenceGate) { var s = _pendingSource; return s?.ListPending() ?? []; }
    }

    /// <summary>Решение владельца по pending-консенту (approve → запомнить клиента).</summary>
    public bool ResolvePendingClient(string pendingId, bool approve)
    {
        lock (_presenceGate) { var s = _pendingSource; return s?.ResolvePending(pendingId, approve) ?? false; }
    }

    /// <summary>Сигнал UI: появился OAuth-клиент, ждущий разрешения владельца.</summary>
    public event Action<McpPendingClient>? PendingClientAdded;

    /// <summary>Публичный endpoint для клиента: {PublicBaseUrl}/mcp или null.</summary>
    public string? PublicEndpointUrl()
    {
        var st = GetHttpStatus();
        return st.PublicBaseUrl == null ? null : st.PublicBaseUrl.TrimEnd('/') + "/mcp";
    }

    // ── пагинация/полнота §6.3: вспомогательные ────────────────────────────

    /// <summary>Текст встречи + признак обрезки потолком MaxMeetingTextChars.</summary>
    private (string? Text, bool Capped) LoadMeetingTextCapped(MeetingRow row, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(row.JournalPath) && File.Exists(row.JournalPath))
        {
            var lines = _transcripts.ReadLines(row.JournalPath);
            var capped = false;
            if (lines.Count > MaxLinesPerMeeting) { lines = lines.Take(MaxLinesPerMeeting).ToList(); capped = true; }
            if (lines.Count > 0)
            {
                var text = MeetingTranscriptStore.FormatTranscript(lines);
                if (text.Length > MaxMeetingTextChars) { text = text[..MaxMeetingTextChars]; capped = true; }
                return (text, capped);
            }
        }
        var memSeg = Mem;
        if (memSeg != null && row.Id > 0)
        {
            ct.ThrowIfCancellationRequested();
            var segs = memSeg.GetSegments(row.Id);
            if (segs.Count > 0)
            {
                var text = string.Join("\n", segs.OrderBy(s => s.Seq).Select(s => s.Text));
                var capped = false;
                if (text.Length > MaxMeetingTextChars) { text = text[..MaxMeetingTextChars]; capped = true; }
                return (text, capped);
            }
        }
        return (null, false);
    }

    private async Task<string?> LoadAttachmentTextAsync(string documentId, CancellationToken ct)
    {
        if (_attachments == null) return null;
        var segs = await _attachments.LoadSegmentsAsync(documentId, ct).ConfigureAwait(false);
        return segs.Count > 0 ? string.Join("\n", segs.Select(s => s.Text)) : null;
    }

    private string? LoadAttachmentText(string documentId, CancellationToken ct)
    {
        try { return LoadAttachmentTextAsync(documentId, ct).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>meetingRef: "meeting:{id}", числовой id или journal id файла.</summary>
    private MeetingRow? ResolveMeeting(string meetingRef)
    {
        var rows = LoadMeetingRows();
        var s = meetingRef.Trim();
        if (s.StartsWith("meeting:", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        if (long.TryParse(s, out var id))
            return rows.FirstOrDefault(r => r.Id == id) ?? LoadMemoryMeetingRow(id);
        return rows.FirstOrDefault(r => string.Equals(r.JournalId, s, StringComparison.OrdinalIgnoreCase));
    }

    private static bool FindFirstMatch(string text, string query, out int index)
    {
        index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
                index = text.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
        }
        return index >= 0;
    }

    private static int CountMatches(string text, string query)
    {
        var n = 0; var i = 0;
        while ((i = text.IndexOf(query, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        { n++; i += Math.Max(1, query.Length); }
        return n;
    }

    private static string SnippetAround(string text, int index)
    {
        var start = Math.Max(0, index - 60);
        var len = Math.Min(SnippetLen, text.Length - start);
        return (start > 0 ? "…" : "") + text.Substring(start, len).Replace('\n', ' ') +
               (start + len < text.Length ? "…" : "");
    }

    /// <summary>Секунды от первого «[hh:mm:ss]» до ближайшего таймстампа перед index.</summary>
    private static double? TimestampAtChar(string text, int index)
    {
        double? first = null; double? at = null;
        var i = 0;
        while ((i = text.IndexOf('[', i)) >= 0 && i <= index)
        {
            var close = text.IndexOf(']', i);
            if (close < 0) break;
            if (TimeSpan.TryParse(text.AsSpan(i + 1, close - i - 1), out var ts))
            {
                first ??= ts.TotalSeconds;
                at = ts.TotalSeconds;
            }
            i = close + 1;
        }
        if (at == null || first == null) return null;
        return Math.Max(0, at.Value - first.Value);
    }

    /// <summary>Освобождение ресурсов. Повторный вызов безопасен (Dispose идемпотентен).</summary>
    public void Dispose()
    {
        // Dispose идемпотентен: хост останавливается и через StopAsync, и через
        // DisposeAsync — второй вызов не должен бросать ObjectDisposedException.
        try { _disposeCts.Cancel(); }
        catch (ObjectDisposedException) { /* уже освобождён */ }
        _disposeCts.Dispose();
    }
}
