using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>
/// Результат лёгкого пред-флайта (POST initialize) к MCP-серверу по HTTP.
/// Даёт HTTP-статус, версию протокола и serverInfo без установки SDK-клиента.
/// </summary>
public sealed record McpHttpProbeResult(
    bool Ok,
    int? HttpStatus,
    string? ProtocolVersion,
    string? ServerName,
    string? ServerVersion,
    string? ErrorText,
    McpOutgoingErrorKind ErrorKind,
    long DurationMs);

/// <summary>
/// Пред-флайт-зонд исходящего HTTP-MCP: один POST initialize с Bearer-заголовком.
/// Нужен, чтобы отличить «токен отклонён (401)» от «сервер недоступен» и «таймаут»
/// ДО установки SDK-клиента. Секрет нигде не логируется и не возвращается наружу.
/// </summary>
public static class McpHttpProbe
{
    /// <summary>Общий HTTP-клиент зонда (одна долгоживущая сессия на процесс).</summary>
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        // Таймаут задаётся через CancellationToken на каждый вызов.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Тело initialize-запроса (JSON-RPC 2.0).</summary>
    private const string InitializeBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
        "\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}," +
        "\"clientInfo\":{\"name\":\"BrainstormBuddy\",\"version\":\"1.0\"}}}";

    /// <summary>
    /// POST initialize к серверу. Никогда не бросает — ошибка возвращается в
    /// McpHttpProbeResult с классификацией (401/403/таймаут/недоступен/прочее).
    /// </summary>
    public static async Task<McpHttpProbeResult> InitializeAsync(
        string url, string? bearer, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new McpHttpProbeResult(false, null, null, null, null,
                "Некорректный URL сервера", McpOutgoingErrorKind.Unreachable, 0);
        }

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var auth = BuildAuthorization(bearer);
            if (auth != null) request.Headers.TryAddWithoutValidation("Authorization", auth);

            using var response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
            sw.Stop();

            var status = (int)response.StatusCode;
            var body = await SafeReadAsync(response, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = new HttpRequestException(
                    $"HTTP {status}", null, response.StatusCode);
                var classified = McpOutgoingErrorInfo.Classify(error, timeoutSeconds);
                return new McpHttpProbeResult(false, status, null, null, null,
                    BuildErrorText(status, body, classified.Detail),
                    classified.Kind, sw.ElapsedMilliseconds);
            }

            var json = ExtractJson(body);
            if (json == null)
            {
                var classified = McpOutgoingErrorInfo.Classify(
                    new HttpRequestException("Ответ сервера не разобран как JSON-RPC", null,
                        response.StatusCode), timeoutSeconds);
                return new McpHttpProbeResult(false, status, null, null, null,
                    "Ответ сервера не разобран как JSON-RPC",
                    classified.Kind, sw.ElapsedMilliseconds);
            }

            var (protocol, serverName, serverVersion, rpcError) = ParseResult(json);
            if (rpcError != null)
            {
                var classified = McpOutgoingErrorInfo.Classify(
                    new HttpRequestException(rpcError, null, response.StatusCode), timeoutSeconds);
                return new McpHttpProbeResult(false, status, protocol, serverName, serverVersion,
                    McpJournal.Redact(rpcError), classified.Kind, sw.ElapsedMilliseconds);
            }
            return new McpHttpProbeResult(true, status, protocol, serverName, serverVersion,
                null, McpOutgoingErrorKind.Unknown, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var classified = McpOutgoingErrorInfo.Classify(ex, timeoutSeconds);
            return new McpHttpProbeResult(false, null, null, null, null,
                classified.Detail, classified.Kind, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Bearer-заголовок: значение без префикса → «Bearer &lt;значение&gt;».</summary>
    private static string? BuildAuthorization(string? bearer)
    {
        if (string.IsNullOrWhiteSpace(bearer)) return null;
        var value = bearer.Trim();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value
            : "Bearer " + value;
    }

    private static async Task<string> SafeReadAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            // Тело читается «как есть»: маскирование ломает JSON. Маскируется
            // только текст, который уходит в ошибку/лог.
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) { return ""; }
    }

    /// <summary>JSON-объект ответа: обычный JSON или первый блок «data: {…}» (SSE).</summary>
    private static string? ExtractJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{')) return trimmed;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            return payload;
        }
        return null;
    }

    /// <summary>Разбор JSON-RPC ответа: protocolVersion, serverInfo, ошибка.</summary>
    private static (string? Protocol, string? ServerName, string? ServerVersion, string? Error)
        ParseResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null, "Ответ не JSON-объект");

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
                return (null, null, null, $"JSON-RPC error {code}: {msg}");
            }

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return (null, null, null, "В ответе нет поля result");

            string? protocol = null, serverName = null, serverVersion = null;
            if (result.TryGetProperty("protocolVersion", out var pv)
                && pv.ValueKind == JsonValueKind.String)
                protocol = pv.GetString();
            if (result.TryGetProperty("serverInfo", out var si) && si.ValueKind == JsonValueKind.Object)
            {
                if (si.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    serverName = n.GetString();
                if (si.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    serverVersion = v.GetString();
            }
            return (protocol, serverName, serverVersion, null);
        }
        catch (JsonException ex)
        {
            return (null, null, null, "Ответ не разобран как JSON-RPC: " + ex.Message);
        }
    }

    /// <summary>Текст ошибки: HTTP-статус + безопасное тело ответа.</summary>
    private static string BuildErrorText(int status, string body, string detail)
    {
        var head = string.IsNullOrWhiteSpace(body)
            ? detail
            : McpJournal.Redact(body.Trim());
        if (head.Length > 300) head = head[..300] + "…";
        return $"HTTP {status}: {head}";
    }
}
