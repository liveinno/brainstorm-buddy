using System.Text;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Общий JSON-RPC 2.0 dispatch входящего MCP-канала — единственный код
/// маршрутизации facade.* методов, используемый обоими транспортами:
/// named pipe (McpIncomingHost, line-delimited) и HTTP (McpHttpIngress,
/// POST /mcp). Извлечён из McpIncomingHost без изменения семантики:
/// тот же набор методов, те же коды ошибок (McpRpcError), та же
/// camelCase-сериализация результатов.
///
/// Разница транспортов сведена к auth: pipe-клиент передаёт connectionToken
/// в params (Resolve по McpPermissionStore), HTTP-клиент уже аутентифицирован
/// Bearer'ом — вызывающий передаёт готовый McpResolvedConnection в
/// DispatchLineAsync/InvokeFacadeAsync и token-резолв пропускается.
///
/// Учёт активности: auth.resolve помечает handshake (UpdateConnectionLastSeen),
/// любой успешный facade.* с conn — реальное обращение (SetLastRequest).
/// Транспорт при этом передаётся параметром для карточек состояния.
/// </summary>
public sealed class McpJsonRpcDispatch
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IMcpFacade _facade;
    private readonly McpPermissionStore? _permissions;
    private readonly Func<IReadOnlyList<McpConnectionEntry>>? _connections;
    private readonly LoggingService? _log;
    private readonly McpIncomingJournal? _journal;
    private readonly string _transport;

    /// <param name="transport">Метка транспорта для статуса («pipe»|«http»).</param>
    /// <param name="journal">Журнал входящего канала (опционально): пишет
    /// метод, параметры, request ID, длительность и исход каждого вызова.</param>
    public McpJsonRpcDispatch(
        IMcpFacade facade,
        McpPermissionStore? permissions,
        Func<IReadOnlyList<McpConnectionEntry>>? connections,
        LoggingService? log = null,
        string transport = "pipe",
        McpIncomingJournal? journal = null)
    {
        _facade = facade;
        _permissions = permissions;
        _connections = connections;
        _log = log;
        _transport = transport;
        _journal = journal;
    }

    public IMcpFacade Facade => _facade;
    public string Transport => _transport;

    /// <summary>
    /// Один JSON-RPC документ (строка для pipe, тело POST для HTTP) →
    /// сериализованный ответ или null для notification. preResolved != null —
    /// HTTP-путь: Bearer уже отображён в подключение, params.connectionToken
    /// не требуется.
    /// </summary>
    public async Task<string?> DispatchLineAsync(
        string line, McpResolvedConnection? preResolved, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException)
        {
            return SerializeError(default, McpRpcError.ParseError, "parse error");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("method", out var methodEl) ||
                methodEl.ValueKind != JsonValueKind.String)
                return SerializeError(GetId(root), McpRpcError.InvalidParams, "invalid request");

            var id = GetId(root);
            var hasId = root.TryGetProperty("id", out _);
            var method = methodEl.GetString()!;
            root.TryGetProperty("params", out var prms);
            // request ID клиента (mcph-…) — общий ключ для журналов McpHost и Buddy.
            var requestId = Param(prms, "requestId") ?? ("srv-" + Guid.NewGuid().ToString("N")[..8]);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _journal?.Info(
                $"req={requestId} transport={_transport} method={method} params={McpIncomingJournal.DescribeParams(prms)}");

            try
            {
                var result = await InvokeAsync(method, prms, preResolved, ct).ConfigureAwait(false);
                _journal?.Info(
                    $"req={requestId} transport={_transport} method={method} ok ms={sw.ElapsedMilliseconds} result={result.GetRawText().Length} bytes");
                if (!hasId) return null; // notification
                return SerializeResult(id, result);
            }
            catch (McpRpcException rex)
            {
                _journal?.Warn(
                    $"req={requestId} transport={_transport} method={method} rpc-error code={rex.Code} message={rex.Message} ms={sw.ElapsedMilliseconds}");
                return hasId ? SerializeError(id, rex.Code, rex.Message, requestId) : null;
            }
            catch (OperationCanceledException)
            {
                _journal?.Warn(
                    $"req={requestId} transport={_transport} method={method} canceled ms={sw.ElapsedMilliseconds}");
                return null;
            }
            catch (Exception ex)
            {
                _log?.Warn($"MCP method {method} threw: {ex.Message}", "Mcp");
                _journal?.Error(
                    $"req={requestId} transport={_transport} method={method} failed ms={sw.ElapsedMilliseconds}", ex);
                // Причина в ответе: клиент (локальный, доверенный) должен видеть,
                // ЧТО сломалось, а не безликое «internal error». Полный трейс — в журнале.
                var reason = $"{ex.GetType().Name}: {ex.Message}";
                return hasId
                    ? SerializeError(id, McpRpcError.InternalError, $"internal error — {reason}", requestId)
                    : null;
            }
        }
    }

    /// <summary>
    /// Вызов facade-метода с уже известным подключением — точка входа для
    /// HTTP-ingress (tools/call) и прямых вызовов фасада. Метод без префикса
    /// «facade.» тоже принимается (удобно мосту инструментов).
    /// </summary>
    /// <summary>Методы, обслуживаемые фасадом (superset: общие + расширенные §6.4).
    /// Pipe-диспетчер проверяет имя до auth — неизвестный метод = -32601, не -32602.</summary>
    private static readonly HashSet<string> KnownFacadeMethods = new(StringComparer.Ordinal)
    {
        "status", "listProjects", "listMeetings", "search", "readSource",
        "enqueueJob", "getJob", "cancelJob",
        "listMeetingDocuments", "addFact", "updateFact", "deleteFact",
        "resumeJob", "readToolResult",
    };
    private static bool IsKnownFacadeMethod(string m) => KnownFacadeMethods.Contains(m);

    public async Task<JsonElement> InvokeFacadeAsync(
        string method, JsonElement prms, McpResolvedConnection conn, CancellationToken ct)
    {
        var m = method.StartsWith("facade.", StringComparison.Ordinal)
            ? method["facade.".Length..]
            : method;
        JsonElement result;
        switch (m)
        {
            case "status":
                result = ToJson(await _facade.GetStatusAsync(ct).ConfigureAwait(false));
                break;
            case "listProjects":
                result = ToJson(await _facade.ListProjectsAsync(conn, ct).ConfigureAwait(false));
                break;
            case "listMeetings":
            {
                var projectId = ParamLongOrNull(prms, "projectId");
                var limit = ParamInt(prms, "limit") ?? 0;
                var cursor = Param(prms, "cursor");
                result = ToJson(await _facade.ListMeetingsAsync(conn, projectId, limit, cursor, ct).ConfigureAwait(false));
                break;
            }
            case "search":
            {
                // Расширенный запрос (mode/cursor/type/фильтры) — общий сборщик
                // с фасадом, иначе диспетчер и фасад разошлись бы по умолчаниям.
                var req = LocalMcpFacade.BuildSearchRequest(prms);
                if (string.IsNullOrWhiteSpace(req.Query))
                    throw McpRpcException.InvalidParams("query required");
                result = ToJson(await _facade.SearchAsync(conn, req, ct).ConfigureAwait(false));
                break;
            }
            case "readSource":
            {
                if (Param(prms, "reference") == null)
                    throw McpRpcException.InvalidParams("reference required");
                // Страничный вариант (offset/cursor/limitBytes) — иначе отрезок
                // читался бы с нуля и запрошенная часть молча терялась бы.
                result = await _facade.InvokeFacadeAsync("facade.readSource", prms, conn, ct)
                    .ConfigureAwait(false);
                break;
            }
            case "enqueueJob":
            {
                var kind = Param(prms, "kind") ?? "custom";
                var argsJson = prms.ValueKind == JsonValueKind.Object &&
                               prms.TryGetProperty("argsJson", out var aj)
                    ? aj.GetRawText() : "{}";
                var idem = Param(prms, "idempotencyKey") ?? "";
                result = ToJson(await _facade.EnqueueJobAsync(conn, kind, argsJson, idem, ct).ConfigureAwait(false));
                break;
            }
            case "getJob":
            {
                var jobId = Param(prms, "jobId")
                    ?? throw McpRpcException.InvalidParams("jobId required");
                result = ToJson(await _facade.GetJobAsync(conn, jobId, ct).ConfigureAwait(false));
                break;
            }
            case "cancelJob":
            {
                var jobId = Param(prms, "jobId")
                    ?? throw McpRpcException.InvalidParams("jobId required");
                result = ToJson(await _facade.CancelJobAsync(conn, jobId, ct).ConfigureAwait(false));
                break;
            }
            case "updateFact":
            {
                var factId = ParamText(prms, "factId") ?? ParamText(prms, "id")
                    ?? throw McpRpcException.InvalidParams("factId required");
                var text = ParamText(prms, "text")
                    ?? throw McpRpcException.InvalidParams("text required");
                result = ToJson(await _facade.UpdateFactAsync(conn, factId, text, ct).ConfigureAwait(false));
                break;
            }
            case "deleteFact":
            {
                var factId = ParamText(prms, "factId") ?? ParamText(prms, "id")
                    ?? throw McpRpcException.InvalidParams("factId required");
                result = ToJson(await _facade.DeleteFactAsync(conn, factId, ct).ConfigureAwait(false));
                break;
            }
            default:
                throw new McpRpcException(McpRpcError.MethodNotFound, $"unknown method '{method}'");
        }
        // Реальное обращение к данным (не факт запущенного listener'а).
        _facade.SetLastRequest(conn.Id);
        return result;
    }

    private async Task<JsonElement> InvokeAsync(
        string method, JsonElement prms, McpResolvedConnection? preResolved, CancellationToken ct)
    {
        switch (method)
        {
            case "auth.resolve":
            {
                // Pipe-клиент сам спрашивает «кто я по этому токену»; для HTTP
                // auth уже пройден Bearer'ом — возвращаем то же resolved-вью.
                var conn = preResolved ?? ResolveByToken(prms);
                if (conn != null)
                    _facade.UpdateConnectionLastSeen(conn.Id, _transport);
                return ToJson(conn);
            }
            case "facade.status":
            {
                var st = await _facade.GetStatusAsync(ct).ConfigureAwait(false);
                if (preResolved != null) _facade.SetLastRequest(preResolved.Id);
                else
                {
                    // Pipe-путь: подключение определяется по токену внутри
                    // запроса. Реальный вызов клиента обязан отражаться в
                    // карточке («Подключено»), иначе статус остаётся
                    // «Ожидаем подключение агента» до переоткрытия окна.
                    var conn = ResolveByToken(prms);
                    if (conn != null) _facade.SetLastRequest(conn.Id);
                }
                return ToJson(st);
            }
            default:
            {
                if (!method.StartsWith("facade.", StringComparison.Ordinal))
                    throw new McpRpcException(McpRpcError.MethodNotFound, $"unknown method '{method}'");
                // Известность метода проверяем ДО auth: неизвестный → -32601 даже
                // без connectionToken (иначе первым выстреливал -32602 от ResolveAuth).
                if (!IsKnownFacadeMethod(method["facade.".Length..]))
                    throw new McpRpcException(McpRpcError.MethodNotFound, $"unknown method '{method}'");
                var conn = preResolved ?? ResolveAuth(prms);
                // Фасад — канонический dispatch (superset методов: свои + HTTP-общие).
                return await _facade.InvokeFacadeAsync(method, prms, conn, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>connectionToken → resolved connection; suspended/unknown → -32001.</summary>
    private McpResolvedConnection ResolveAuth(JsonElement prms)
    {
        var token = Param(prms, "connectionToken")
            ?? throw McpRpcException.InvalidParams("connectionToken required");
        var c = ResolveByToken(token);
        if (c == null) throw McpRpcException.Unauthorized("unknown or revoked connection token");
        if (c.Suspended) throw McpRpcException.Unauthorized("connection is suspended");
        return c;
    }

    /// <summary>Токен из params или null — без требования параметра (auth.resolve).</summary>
    private McpResolvedConnection? ResolveByToken(JsonElement prms)
    {
        var token = Param(prms, "connectionToken") ?? Param(prms, "token") ?? "";
        return ResolveByToken(token);
    }

    private McpResolvedConnection? ResolveByToken(string token)
    {
        if (_permissions == null || _connections == null || string.IsNullOrWhiteSpace(token))
            return null;
        return _permissions.Resolve(token, _connections());
    }

    // ── params / serialization helpers ──────────────────────────────────────

    internal static string? Param(JsonElement prms, string name)
    {
        if (prms.ValueKind == JsonValueKind.Object &&
            prms.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    internal static int? ParamInt(JsonElement prms, string name)
    {
        if (prms.ValueKind == JsonValueKind.Object &&
            prms.TryGetProperty(name, out var el) &&
            el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
            return v;
        return null;
    }

    /// <summary>Параметр как текст: строка — как есть, число — его запись.</summary>
    internal static string? ParamText(JsonElement prms, string name)
    {
        if (prms.ValueKind == JsonValueKind.Object &&
            prms.TryGetProperty(name, out var el))
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        return null;
    }

    internal static long? ParamLongOrNull(JsonElement prms, string name)
    {
        if (prms.ValueKind == JsonValueKind.Object &&
            prms.TryGetProperty(name, out var el) &&
            el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v))
            return v;
        return null;
    }

    internal static JsonElement GetId(JsonElement root)
        => root.TryGetProperty("id", out var el) ? el.Clone() : default;

    internal static JsonElement ToJson<T>(T value)
        => JsonSerializer.SerializeToElement(value, JsonOpts);

    internal static string SerializeResult(JsonElement id, JsonElement result)
    {
        using var w = new JsonStringWriter();
        w.Writer.WriteStartObject();
        w.Writer.WriteString("jsonrpc", "2.0");
        w.Writer.WritePropertyName("id");
        if (id.ValueKind == JsonValueKind.Undefined) w.Writer.WriteNullValue();
        else id.WriteTo(w.Writer);
        w.Writer.WritePropertyName("result");
        result.WriteTo(w.Writer);
        w.Writer.WriteEndObject();
        return w.ToString();
    }

    internal static string SerializeError(JsonElement id, int code, string message, string? requestId = null)
    {
        using var w = new JsonStringWriter();
        w.Writer.WriteStartObject();
        w.Writer.WriteString("jsonrpc", "2.0");
        w.Writer.WritePropertyName("id");
        if (id.ValueKind == JsonValueKind.Undefined) w.Writer.WriteNullValue();
        else id.WriteTo(w.Writer);
        w.Writer.WritePropertyName("error");
        w.Writer.WriteStartObject();
        w.Writer.WriteNumber("code", code);
        w.Writer.WriteString("message", message);
        if (!string.IsNullOrEmpty(requestId))
        {
            // JSON-RPC 2.0: error.data — произвольные данные об ошибке.
            // Кладём request ID, чтобы клиент сопоставил отказ со своей записью.
            w.Writer.WritePropertyName("data");
            w.Writer.WriteStartObject();
            w.Writer.WriteString("requestId", requestId);
            w.Writer.WriteEndObject();
        }
        w.Writer.WriteEndObject();
        w.Writer.WriteEndObject();
        return w.ToString();
    }

    /// <summary>Пишет JSON в строку без промежуточного DOM.</summary>
    internal sealed class JsonStringWriter : IDisposable
    {
        private readonly MemoryStream _ms = new();
        public readonly Utf8JsonWriter Writer;
        public JsonStringWriter() => Writer = new Utf8JsonWriter(_ms);
        public override string ToString()
        {
            Writer.Flush();
            return Encoding.UTF8.GetString(_ms.GetBuffer(), 0, (int)_ms.Length);
        }
        public void Dispose() => Writer.Dispose();
    }
}
