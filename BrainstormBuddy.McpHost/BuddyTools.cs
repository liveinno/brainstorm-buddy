using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BrainstormBuddy.McpHost;

/// <summary>
/// Инструменты входящего MCP-канала Buddy. Каждый вызов — один JSON-RPC запрос
/// через named pipe в запущенный BrainstormBuddy.exe (McpIncomingHost).
/// connection_token клиенту передавать не обязательно: хост сам подставляет
/// токен из файла, указанного в argv (его пишет Buddy при выдаче подключения).
/// Возвращаемое значение — JSON-строка результата, понятная любому клиенту.
///
/// Ошибки: обёртка вызова перехватывает исключения моста и поднимает
/// <see cref="McpException"/> с понятной причиной и request ID запроса —
/// клиент получает JSON-RPC ошибку, в которой видно, ЧТО сломалось
/// (нет приложения / отказ токена / метод не найден), а не безликое
/// «An error occurred invoking …». Тот же request ID пишется в журналы
/// McpHost ({appData}/logs/mcphost.log) и Buddy ({appData}/logs/mcp.log),
/// поэтому записи двух сторон связываются между собой.
/// </summary>
[McpServerToolType]
public sealed class BuddyTools
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly BuddyPipeClient _pipe;

    public BuddyTools(BuddyPipeClient pipe) => _pipe = pipe;

    private static string Render(JsonElement el) => el.GetRawText() is { } raw
        ? JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(raw), Pretty)
        : "null";

    /// <summary>
    /// Вызов facade-метода с прозрачной обработкой ошибок: любая поломка
    /// превращается в McpException с причиной и request ID.
    /// </summary>
    private async Task<string> CallAsync(
        string method, Dictionary<string, object?> prms, CancellationToken ct)
    {
        try
        {
            return Render(await _pipe.CallAsync(method, prms, ct));
        }
        catch (BuddyPipeClient.BuddyBridgeException ex)
        {
            throw new McpException(
                $"mcp_call_failed: {ex.Message} (requestId={ex.RequestId ?? "n/a"}, method={method})",
                ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(
                $"mcp_call_failed: {ex.GetType().Name}: {ex.Message} (method={method})", ex);
        }
    }

    [McpServerTool(Name = "buddy_status"),
     Description("BrainstormBuddy server status: version, counts, tunnel state.")]
    public async Task<string> Status(
        [Description("Connection token issued by Buddy (optional — token file is used by default)")]
        string? connection_token = null,
        CancellationToken ct = default)
        => await CallAsync("status", TokenParam(connection_token), ct);

    [McpServerTool(Name = "buddy_list_projects"),
     Description("List projects available to this connection.")]
    public async Task<string> ListProjects(
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
        => await CallAsync("listProjects", TokenParam(connection_token), ct);

    [McpServerTool(Name = "buddy_list_meetings"),
     Description("List meetings (transcripts), newest first. Optionally filter by project id. The reply is a plain list of at most `limit` meetings (1..200, default 200) — to page further pass cursor=<number of meetings already received> as the offset.")]
    public async Task<string> ListMeetings(
        [Description("Max results in the page (1..200, default 200)")] int limit = 0,
        [Description("Offset: number of meetings already received (integer)")] string? cursor = null,
        [Description("Filter by project id")] long? project_id = null,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        var prms = TokenParam(connection_token);
        if (limit > 0) prms["limit"] = limit;
        if (cursor != null) prms["cursor"] = cursor;
        if (project_id.HasValue) prms["projectId"] = project_id.Value;
        return await CallAsync("listMeetings", prms, ct);
    }

    [McpServerTool(Name = "buddy_search"),
     Description("Search meetings (transcript text), memory facts, attachment extracted text and projects. type filters the sources ('meeting'/'transcript', 'memory'/'fact', 'document'/'file', 'project', 'all'; default meeting+memory). mode='relevant' (default) returns top-N by rank; mode='all' walks every match and is paged with nextCursor. The reply carries totalMatches, hasMore, nextCursor and exhaustive — totalMatches counts every match found so far in the walk (on the last page it is the full total), exhaustive=false means coverage was partial (e.g. FTS limited), so keep paging with nextCursor. Every hit has a stable 'reference' for buddy_read_source.")]
    public async Task<string> Search(
        [Description("Search query text")] string query,
        [Description("Comma-separated types: meeting|transcript, memory|fact, document|file, project, all")] string? type = null,
        [Description("'relevant' (default, top-N by rank) | 'all' (walk all matches, page with nextCursor)")] string? mode = null,
        [Description("From date, ISO 8601 UTC")] string? from = null,
        [Description("To date, ISO 8601 UTC")] string? to = null,
        [Description("Restrict to these project ids")] long[]? project_ids = null,
        [Description("Cursor from a previous response's nextCursor")] string? cursor = null,
        [Description("Max hits per page: 1..50 in 'relevant' mode, 1..200 in 'all' mode (0 = server default: 50 / 200)")] int limit = 0,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "{\"error\":\"query is empty\"}";
        var prms = TokenParam(connection_token);
        prms["query"] = query;
        if (type != null) prms["type"] = type;
        if (mode != null) prms["mode"] = mode;
        if (from != null) prms["fromUtc"] = from;
        if (to != null) prms["toUtc"] = to;
        if (project_ids != null) prms["projectIds"] = project_ids;
        if (cursor != null) prms["cursor"] = cursor;
        if (limit > 0) prms["limit"] = limit;
        return await CallAsync("search", prms, ct);
    }

    [McpServerTool(Name = "buddy_read_source"),
     Description("Read content of a reference from buddy_search: meeting:{id} (transcript), memory:{id} (fact), attachment:{docId} (extracted text). The reply is a page: offset (start position in characters), totalChars, hasMore and nextCursor. Page until hasMore=false — nothing is silently dropped. Use nextCursor (preferred) or offset to continue; a source whose text is unavailable comes back truncated=true with truncatedReason.")]
    public async Task<string> ReadSource(
        [Description("Reference string, e.g. 'meeting:123' or 'attachment:abc-...'")] string reference,
        [Description("Character offset to start from (default 0)")] long offset = 0,
        [Description("Opaque cursor from a previous response's nextCursor (preferred over offset)")] string? cursor = null,
        [Description("Page budget in UTF-8 bytes, 1..2097152 (0 = server default 131072)")] int? limit = null,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        var prms = TokenParam(connection_token);
        prms["reference"] = reference;
        if (offset > 0) prms["offset"] = offset;
        if (cursor != null) prms["cursor"] = cursor;
        if (limit.HasValue) prms["limitBytes"] = limit.Value;
        return await CallAsync("readSource", prms, ct);
    }

    [McpServerTool(Name = "buddy_list_documents"),
     Description("List attachments of a meeting with extracted-text status and document ids usable as 'attachment:{id}' references.")]
    public async Task<string> ListDocuments(
        [Description("Meeting reference: 'meeting:123', numeric id or journal id")] string meeting_id,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        var prms = TokenParam(connection_token);
        prms["meetingRef"] = meeting_id;
        return await CallAsync("listMeetingDocuments", prms, ct);
    }

    [McpServerTool(Name = "buddy_add_fact"),
     Description("Save a fact into Buddy memory (idempotent — same text+scope returns duplicate=true with the existing id).")]
    public async Task<string> AddFact(
        [Description("Fact text to store")] string text,
        [Description("Optional source reference, e.g. 'meeting:12#t=45.0' or 'attachment:doc-…'")] string? source_ref = null,
        [Description("Optional project id scope")] long? project_id = null,
        [Description("Optional meeting id scope")] long? meeting_id = null,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "{\"error\":\"text is empty\"}";
        var prms = TokenParam(connection_token);
        prms["text"] = text;
        if (source_ref != null) prms["sourceRef"] = source_ref;
        if (project_id.HasValue) prms["projectId"] = project_id.Value;
        if (meeting_id.HasValue) prms["meetingId"] = meeting_id.Value;
        return await CallAsync("addFact", prms, ct);
    }

    [McpServerTool(Name = "buddy_update_fact"),
     Description("Изменить текст факта в памяти Buddy по его id (id из buddy_add_fact или buddy_search). Требует права change на подключение; факт вне области подключения — отказ. Возвращает {id, updated, notFound, scope, reference, message}.")]
    public async Task<string> UpdateFact(
        [Description("Id факта (числом или 'memory:{id}')")] string fact_id,
        [Description("Новый текст факта")] string text,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "{\"error\":\"text is empty\"}";
        var prms = TokenParam(connection_token);
        prms["factId"] = fact_id;
        prms["text"] = text;
        return await CallAsync("updateFact", prms, ct);
    }

    [McpServerTool(Name = "buddy_delete_fact"),
     Description("Удалить факт из памяти Buddy по его id (id из buddy_add_fact или buddy_search). Требует права delete на подключение и явного подтверждения владельца в карточке внутри оверлея Buddy: при отказе, отмене или таймауте факт остаётся. Удаление мягкое — факт помечается удалённым, запись не стирается. Возвращает {id, deleted, notFound, scope, reference, message}.")]
    public async Task<string> DeleteFact(
        [Description("Id факта (числом или 'memory:{id}')")] string fact_id,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fact_id))
            return "{\"error\":\"fact_id is empty\"}";
        var prms = TokenParam(connection_token);
        prms["factId"] = fact_id;
        return await CallAsync("deleteFact", prms, ct);
    }

    [McpServerTool(Name = "buddy_read_tool_result"),
     Description("Read a stored full tool result by tool_result_id (results larger than the context limit are saved out-of-band; a pointer is embedded in the truncated message).")]
    public async Task<string> ReadToolResult(
        [Description("tool_result_id from the saved-result pointer")] string id,
        [Description("Character offset to start from (default 0)")] long offset = 0,
        [Description("Page size in bytes, 1..2097152 (default 131072)")] int? limit = null,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        var prms = TokenParam(connection_token);
        prms["id"] = id;
        if (offset > 0) prms["offset"] = offset;
        if (limit.HasValue) prms["limitBytes"] = limit.Value;
        return await CallAsync("readToolResult", prms, ct);
    }

    [McpServerTool(Name = "buddy_run_job"),
     Description("Enqueue a Buddy background job. kind: 'search'/'research' (args {\"query\":\"...\"}), 'transcribe' (args {\"file_path\":\"...\"} or {\"import_id\":\"...\"}), 'extract_facts' (args {\"meeting_id\":N} or {\"all\":true}), 'resume'/'continue' (job_id). Returns the job record — poll with buddy_job_status.")]
    public async Task<string> RunJob(
        [Description("Job kind: search | research | transcribe | extract_facts | resume | continue")] string kind,
        [Description("Job arguments JSON, e.g. {\"query\":\"budget review\"}")] string? args_json = null,
        [Description("Existing job id — required for kind=resume/continue")] string? job_id = null,
        [Description("Idempotency key — repeated calls with the same key return the same job")] string? idempotency_key = null,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        var prms = TokenParam(connection_token);
        prms["kind"] = kind;
        if (job_id != null) prms["jobId"] = job_id;
        if (args_json != null)
        {
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(args_json);
                prms["argsJson"] = el;
                // Валидация фасада смотрит и top-level ключи — дублируем
                // аргументы туда для transcribe/extract_facts.
                if (el.ValueKind == JsonValueKind.Object)
                    foreach (var p in el.EnumerateObject())
                        if (!prms.ContainsKey(p.Name)) prms[p.Name] = p.Value.Clone();
            }
            catch (JsonException) { return "{\"error\":\"args_json is not valid JSON\"}"; }
        }
        if (idempotency_key != null) prms["idempotencyKey"] = idempotency_key;
        return await CallAsync("enqueueJob", prms, ct);
    }

    [McpServerTool(Name = "buddy_job_status"),
     Description("Get a Buddy background job state by id (from buddy_run_job).")]
    public async Task<string> JobStatus(
        [Description("Job id, e.g. 'mcpjob-…'")] string job_id,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        var prms = TokenParam(connection_token);
        prms["jobId"] = job_id;
        return await CallAsync("getJob", prms, ct);
    }

    [McpServerTool(Name = "buddy_cancel_job"),
     Description("Cancel a running/queued Buddy background job.")]
    public async Task<string> CancelJob(
        [Description("Job id, e.g. 'mcpjob-…'")] string job_id,
        [Description("Connection token issued by Buddy (optional)")] string? connection_token = null,
        CancellationToken ct = default)
    {
        var prms = TokenParam(connection_token);
        prms["jobId"] = job_id;
        return await CallAsync("cancelJob", prms, ct);
    }

    /// <summary>
    /// Параметры запроса: если клиент передал connection_token явно — он
    /// перекрывает токен из файла (продвинутый сценарий, один host на несколько
    /// подключений). Иначе McpPipeClient подставит файловый.
    /// </summary>
    private Dictionary<string, object?> TokenParam(string? connectionToken)
    {
        var prms = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(connectionToken))
            prms["connectionToken"] = connectionToken;
        return prms;
    }
}
