using System.Text.Json;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Core.Mcp.Http;

/// <summary>
/// Отображение MCP tools/list + tools/call поверх общего facade-диспатча.
/// Набор инструментов зеркалит BrainstormBuddy.McpHost.BuddyTools (13 шт.),
/// чтобы pipe- и HTTP-клиенты видели идентичную поверхность.
/// Аргументы tools/call (snake_case) перекладываются в params facade.* (camelCase).
/// </summary>
public static class McpHttpToolBridge
{
    public sealed record ToolDef(string Name, string Description, string InputSchemaJson);

    private static readonly ToolDef[] Tools =
    {
        new("buddy_status",
            "BrainstormBuddy server status: version, counts, tunnel state.",
            Obj(Prop("connection_token", "string", "Connection token (не нужен при Bearer)"))),
        new("buddy_list_projects",
            "List projects available to this connection.",
            Obj()),
        new("buddy_list_meetings",
            "List meetings (transcripts), newest first. Optionally filter by project id. The reply is a plain list of at most `limit` meetings (1..200, default 200) — to page further pass cursor=<number of meetings already received> as the offset.",
            Obj(
                Prop("limit", "integer", "Max results in the page (1..200, default 200)"),
                Prop("cursor", "string", "Offset: number of meetings already received (integer)"),
                Prop("project_id", "integer", "Filter by project id"))),
        new("buddy_search",
            "Search meetings (transcript text), memory facts, attachment extracted text and projects. type filters the sources ('meeting'/'transcript', 'memory'/'fact', 'document'/'file', 'project', 'all'; default meeting+memory). mode='relevant' (default) returns top-N by rank; mode='all' walks every match and is paged with nextCursor. The reply carries totalMatches, hasMore, nextCursor and exhaustive — totalMatches counts every match found so far in the walk (on the last page it is the full total), exhaustive=false means coverage was partial (e.g. FTS limited), so keep paging with nextCursor. Every hit has a stable 'reference' for buddy_read_source.",
            ObjReq("\"query\"",
                Prop("query", "string", "Search query text"),
                Prop("type", "string", "Comma-separated types: meeting|transcript, memory|fact, document|file, project, all"),
                Prop("mode", "string", "'relevant' (default, top-N by rank) | 'all' (walk all matches, page with nextCursor)"),
                Prop("from", "string", "From date, ISO 8601 UTC"),
                Prop("to", "string", "To date, ISO 8601 UTC"),
                Prop("project_ids", "array", "Restrict to these project ids", "\"items\":{\"type\":\"integer\"}"),
                Prop("cursor", "string", "Cursor from a previous response's nextCursor"),
                Prop("limit", "integer", "Max hits per page: 1..50 in 'relevant' mode, 1..200 in 'all' mode (0 = server default: 50 / 200)"))),
        new("buddy_read_source",
            "Read content of a reference from buddy_search: meeting:{id} (transcript), memory:{id} (fact), attachment:{docId} (extracted text). The reply is a page: offset (start position in characters), totalChars, hasMore and nextCursor. Page until hasMore=false — nothing is silently dropped. Use nextCursor (preferred) or offset to continue; a source whose text is unavailable comes back truncated=true with truncatedReason.",
            ObjReq("\"reference\"",
                Prop("reference", "string", "Reference string, e.g. 'meeting:123' or 'attachment:abc-...'"),
                Prop("offset", "integer", "Character offset to start from (default 0)"),
                Prop("cursor", "string", "Opaque cursor from a previous response's nextCursor (preferred over offset)"),
                Prop("limit", "integer", "Page budget in UTF-8 bytes, 1..2097152 (0 = server default 131072)"))),
        new("buddy_list_documents",
            "List attachments of a meeting with extracted-text status and document ids usable as 'attachment:{id}' references.",
            ObjReq("\"meeting_id\"",
                Prop("meeting_id", "string", "Meeting reference: 'meeting:123', numeric id or journal id"))),
        new("buddy_add_fact",
            "Save a fact into Buddy memory (idempotent — same text+scope returns duplicate=true with the existing id).",
            ObjReq("\"text\"",
                Prop("text", "string", "Fact text to store"),
                Prop("source_ref", "string", "Optional source reference, e.g. 'meeting:12#t=45.0' or 'attachment:doc-…'"),
                Prop("project_id", "integer", "Optional project id scope"),
                Prop("meeting_id", "integer", "Optional meeting id scope"))),
        new("buddy_update_fact",
            "Изменить текст факта в памяти Buddy по его id (id из buddy_add_fact или buddy_search). Требует права change на подключение; факт вне области подключения — отказ. Возвращает {id, updated, notFound, scope, reference, message}.",
            ObjReq("\"fact_id\",\"text\"",
                Prop("fact_id", "string", "Id факта (числом или 'memory:{id}')"),
                Prop("text", "string", "Новый текст факта"))),
        new("buddy_delete_fact",
            "Удалить факт из памяти Buddy по его id (id из buddy_add_fact или buddy_search). Требует права delete на подключение и явного подтверждения владельца в карточке внутри оверлея Buddy: при отказе, отмене или таймауте факт остаётся. Удаление мягкое — факт помечается удалённым, запись не стирается. Возвращает {id, deleted, notFound, scope, reference, message}.",
            ObjReq("\"fact_id\"",
                Prop("fact_id", "string", "Id факта (числом или 'memory:{id}')"))),
        new("buddy_read_tool_result",
            "Read a stored full tool result by tool_result_id (results larger than the context limit are saved out-of-band; a pointer is embedded in the truncated message).",
            ObjReq("\"id\"",
                Prop("id", "string", "tool_result_id from the saved-result pointer"),
                Prop("offset", "integer", "Character offset to start from (default 0)"),
                Prop("limit", "integer", "Page size in bytes, 1..2097152 (default 131072)"))),
        new("buddy_run_job",
            "Enqueue a Buddy background job. kind: 'search'/'research' (args {\"query\":\"...\"}), 'transcribe' (args {\"file_path\":\"...\"} or {\"import_id\":\"...\"}), 'extract_facts' (args {\"meeting_id\":N} or {\"all\":true}), 'resume'/'continue' (job_id). Returns the job record — poll with buddy_job_status.",
            ObjReq("\"kind\"",
                Prop("kind", "string", "Job kind: search | research | transcribe | extract_facts | resume | continue"),
                Prop("args_json", "string", "Job arguments JSON, e.g. {\"query\":\"budget review\"}"),
                Prop("job_id", "string", "Existing job id — required for kind=resume/continue"),
                Prop("idempotency_key", "string", "Idempotency key — repeated calls return the same job"))),
        new("buddy_job_status",
            "Get a Buddy background job state by id (from buddy_run_job).",
            ObjReq("\"job_id\"", Prop("job_id", "string", "Job id, e.g. 'mcpjob-…'"))),
        new("buddy_cancel_job",
            "Cancel a running/queued Buddy background job.",
            ObjReq("\"job_id\"", Prop("job_id", "string", "Job id"))),
    };

    /// <summary>Все buddy-инструменты (тот же набор, что и у stdio-хоста).</summary>
    public static IReadOnlyList<ToolDef> ToolList => Tools;

    /// <summary>JSON для tools/list: [{name, description, inputSchema}].</summary>
    public static string ToolListJson()
    {
        var sb = new System.Text.StringBuilder(2048);
        sb.Append('[');
        for (var i = 0; i < Tools.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var t = Tools[i];
            sb.Append("{\"name\":").Append(JsonSerializer.Serialize(t.Name))
              .Append(",\"description\":").Append(JsonSerializer.Serialize(t.Description))
              .Append(",\"inputSchema\":").Append(t.InputSchemaJson)
              .Append('}');
        }
        return sb.Append(']').ToString();
    }

    /// <summary>
    /// Имя инструмента + его arguments → (facade-метод, params JsonElement).
    /// false — неизвестный инструмент (MethodNotFound).
    /// </summary>
    public static bool TryMapCall(
        string toolName, JsonElement arguments,
        out string facadeMethod, out JsonElement facadeParams)
    {
        facadeMethod = "";
        facadeParams = default;
        switch (toolName)
        {
            case "buddy_status":
                facadeMethod = "facade.status";
                facadeParams = BuildParams();
                return true;
            case "buddy_list_projects":
                facadeMethod = "facade.listProjects";
                facadeParams = BuildParams();
                return true;
            case "buddy_list_meetings":
                facadeMethod = "facade.listMeetings";
                facadeParams = BuildParams(
                    CopyInt(arguments, "limit", "limit"),
                    CopyStr(arguments, "cursor", "cursor"),
                    CopyInt(arguments, "project_id", "projectId"));
                return true;
            case "buddy_search":
                facadeMethod = "facade.search";
                facadeParams = BuildParams(
                    CopyStr(arguments, "query", "query"),
                    CopyStr(arguments, "type", "type"),
                    CopyStr(arguments, "types", "types"),
                    CopyStr(arguments, "mode", "mode"),
                    CopyStr(arguments, "from", "fromUtc"),
                    CopyStr(arguments, "to", "toUtc"),
                    CopyRaw(arguments, "project_ids", "projectIds"),
                    CopyStr(arguments, "cursor", "cursor"),
                    CopyInt(arguments, "limit", "limit"));
                return true;
            case "buddy_read_source":
                facadeMethod = "facade.readSource";
                facadeParams = BuildParams(
                    CopyStr(arguments, "reference", "reference"),
                    CopyInt(arguments, "offset", "offset"),
                    CopyStr(arguments, "cursor", "cursor"),
                    CopyInt(arguments, "limit", "limitBytes"));
                return true;
            case "buddy_list_documents":
                facadeMethod = "facade.listMeetingDocuments";
                facadeParams = BuildParams(
                    CopyStr(arguments, "meeting_id", "meetingRef"));
                return true;
            case "buddy_add_fact":
                facadeMethod = "facade.addFact";
                facadeParams = BuildParams(
                    CopyStr(arguments, "text", "text"),
                    CopyStr(arguments, "source_ref", "sourceRef"),
                    CopyInt(arguments, "project_id", "projectId"),
                    CopyInt(arguments, "meeting_id", "meetingId"));
                return true;
            case "buddy_update_fact":
                facadeMethod = "facade.updateFact";
                facadeParams = BuildParams(
                    CopyStr(arguments, "fact_id", "factId"),
                    CopyStr(arguments, "text", "text"));
                return true;
            case "buddy_delete_fact":
                facadeMethod = "facade.deleteFact";
                facadeParams = BuildParams(
                    CopyStr(arguments, "fact_id", "factId"));
                return true;
            case "buddy_read_tool_result":
                facadeMethod = "facade.readToolResult";
                facadeParams = BuildParams(
                    CopyStr(arguments, "id", "id"),
                    CopyInt(arguments, "offset", "offset"),
                    CopyInt(arguments, "limit", "limitBytes"));
                return true;
            case "buddy_run_job":
                facadeMethod = "facade.enqueueJob";
                facadeParams = BuildRunJobParams(arguments);
                return true;
            case "buddy_job_status":
                facadeMethod = "facade.getJob";
                facadeParams = BuildParams(
                    CopyStr(arguments, "job_id", "jobId"));
                return true;
            case "buddy_cancel_job":
                facadeMethod = "facade.cancelJob";
                facadeParams = BuildParams(
                    CopyStr(arguments, "job_id", "jobId"));
                return true;
            default:
                return false;
        }
    }

    // ── JSON schema builders (маленькие, без внешних зависимостей) ──────────

    /// <summary>
    /// params для facade.enqueueJob. Кроме kind/args_json/idempotency_key
    /// пробрасываем job_id (нужен для resume/continue) и дублируем ключи
    /// args_json на верхний уровень: фасад валидирует transcribe/extract_facts
    /// по top-level полям — так же, как это делает stdio-мост BuddyTools.RunJob.
    /// </summary>
    private static JsonElement BuildRunJobParams(JsonElement arguments)
    {
        var parts = new List<string>(8);
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name, string rawJson)
        {
            if (!used.Add(name)) return;
            parts.Add(JsonSerializer.Serialize(name) + ":" + rawJson);
        }
        if (TryGetString(arguments, "kind", out var kind)) Add("kind", JsonSerializer.Serialize(kind));
        if (TryGetString(arguments, "args_json", out var argsJson))
        {
            Add("argsJson", JsonSerializer.Serialize(argsJson));
            if (TryParseObject(argsJson, out var obj))
                foreach (var p in obj.EnumerateObject())
                    Add(p.Name, p.Value.GetRawText());
        }
        if (TryGetString(arguments, "job_id", out var jobId)) Add("jobId", JsonSerializer.Serialize(jobId));
        if (TryGetString(arguments, "idempotency_key", out var idem))
            Add("idempotencyKey", JsonSerializer.Serialize(idem));
        return BuildRawParams(parts);
    }

    private static bool TryGetString(JsonElement args, string name, out string value)
    {
        value = "";
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? "";
            return true;
        }
        return false;
    }

    private static bool TryParseObject(string json, out JsonElement obj)
    {
        obj = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            obj = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static JsonElement BuildRawParams(List<string> parts)
    {
        using var doc = JsonDocument.Parse("{" + string.Join(",", parts) + "}");
        return doc.RootElement.Clone();
    }

    private static string Prop(string name, string type, string? description = null, string extra = "")
    {
        var desc = description == null ? "" : ",\"description\":" + JsonSerializer.Serialize(description);
        var ex = string.IsNullOrEmpty(extra) ? "" : "," + extra;
        return $"\"{name}\":{{\"type\":\"{type}\"{desc}{ex}}}";
    }

    private static string Obj(params string[] props)
        => "{\"type\":\"object\",\"properties\":{" +
           (props.Length == 0 ? "" : string.Join(",", props)) + "}}";

    private static string ObjReq(string requiredJson, params string[] props)
        => "{\"type\":\"object\",\"properties\":{" +
           (props.Length == 0 ? "" : string.Join(",", props)) +
           "},\"required\":[" + requiredJson + "]}";

    private static ParamCopy? CopyStr(JsonElement args, string from, string to)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(from, out var el) && el.ValueKind == JsonValueKind.String)
            return new ParamCopy($"\"{to}\":{el.GetRawText()}");
        return null;
    }

    private static ParamCopy? CopyInt(JsonElement args, string from, string to)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(from, out var el) && el.ValueKind == JsonValueKind.Number)
            return new ParamCopy($"\"{to}\":{el.GetRawText()}");
        return null;
    }

    private static ParamCopy? CopyRaw(JsonElement args, string from, string to)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(from, out var el) &&
            (el.ValueKind == JsonValueKind.Array || el.ValueKind == JsonValueKind.Object))
            return new ParamCopy($"\"{to}\":{el.GetRawText()}");
        return null;
    }

    private static JsonElement BuildParams(params ParamCopy?[] copies)
    {
        var parts = copies.Where(c => c != null).Select(c => c!.Json);
        var json = "{" + string.Join(",", parts) + "}";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Отрендеренный фрагмент JSON-поля для сборки params-объекта.</summary>
    private sealed record ParamCopy(string Json);
}
