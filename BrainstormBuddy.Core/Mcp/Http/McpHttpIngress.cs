using System.Net;
using System.Text;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Mcp.Http;

/// <summary>
/// Входящий Streamable-HTTP MCP endpoint (спека §6.1): HttpListener на
/// 127.0.0.1:{McpConfig.HttpPort} — публичность даёт только внешний
/// Funnel/manual-туннель, сам listener loopback-only.
///
/// Маршруты:
///   GET  /.well-known/oauth-authorization-server  — метаданные AS
///   GET  /.well-known/oauth-protected-resource    — ресурс-метаданные (RFC 9728)
///   POST /register                                — RFC 7591 dynamic client registration
///   GET  /authorize                               — authorization_code выдача (consent
///                                                   уже дан локально permission-моделью)
///   POST /token                                   — code/refresh → access_token
///   POST /mcp                                     — JSON-RPC MCP (initialize, tools/list,
///                                                   tools/call, ping; Bearer обязателен)
///   GET|DELETE /mcp                               — 405 (Streamable HTTP без SSE-сессий)
/// Маршрутизация facade.* — общий McpJsonRpcDispatch (тот же код, что у pipe).
/// Consent-модель (advisory-fix): первый контакт OAuth-клиента показывает
/// HTML-интерстишел «ждём владельца» + регистрирует pending-консент; решение
/// принимает владелец в Настройки → MCP (спека: повторных подтверждений нет —
/// уже approved клиент получает code сразу, без интерстишела).
/// </summary>
public sealed class McpHttpIngress : IAsyncDisposable
{
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private const string McpProtocolVersion = "2025-06-18";
    private const string ServerVersion = "2.0.0-http";

    private readonly IMcpFacade _facade;
    private readonly McpConfig _config;
    private readonly McpHttpAuthStore _auth;
    private readonly Func<IReadOnlyList<McpConnectionEntry>> _connections;
    private readonly LoggingService? _log;
    private readonly string _resourceUrl;    // https://pub/…/mcp или http://127.0.0.1:P/mcp
    private readonly string _issuerUrl;      // base URL без /mcp
    private HttpListener? _listener;
    private Task? _acceptLoop;
    private readonly CancellationTokenSource _stopCts = new();

    public McpHttpIngress(
        IMcpFacade facade,
        McpConfig config,
        McpHttpAuthStore authStore,
        Func<IReadOnlyList<McpConnectionEntry>> connections,
        LoggingService? log = null)
    {
        _facade = facade;
        _config = config;
        _auth = authStore;
        _connections = connections;
        _log = log;
        var baseUrl = !string.IsNullOrWhiteSpace(config.PublicBaseUrl)
            ? config.PublicBaseUrl.TrimEnd('/')
            : $"http://127.0.0.1:{config.HttpPort}";
        _issuerUrl = baseUrl;
        _resourceUrl = baseUrl + "/mcp";
    }

    public McpHttpAuthStore AuthStore => _auth;
    /// <summary>Общий фасад — нужен wizard'у для GetHttpStatus (тикер «ожидаем»).</summary>
    public IMcpFacade Facade => _facade;
    public bool Running => _listener?.IsListening == true;
    public string LocalPrefix => $"http://127.0.0.1:{_config.HttpPort}/";

    /// <summary>
    /// Старт по Mcp.HttpEnabled: возвращает инстанс всегда (даже при ошибке bind —
    /// транспорт помечен упавшим, статус «Нет связи» покажет причину).
    /// null — только когда HttpEnabled=false.
    /// </summary>
    public static async Task<McpHttpIngress?> TryStartAsync(
        IMcpFacade facade,
        McpConfig config,
        McpHttpAuthStore authStore,
        Func<IReadOnlyList<McpConnectionEntry>> connections,
        LoggingService? log = null)
    {
        if (!config.Enabled || !config.HttpEnabled) return null;
        var ingress = new McpHttpIngress(facade, config, authStore, connections, log);
        await ingress.StartAsync().ConfigureAwait(false);
        return ingress;
    }

    public Task StartAsync()
    {
        _listener = new HttpListener();
        try
        {
            _listener.Prefixes.Add(LocalPrefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            var reason = ex is HttpListenerException hle
                ? $"порт {_config.HttpPort}: {hle.Message} (код {hle.ErrorCode})"
                : ex.Message;
            _facade.ReportTransportState("http", alive: false, reason);
            _log?.Warn($"MCP HTTP listener failed: {reason}", "Mcp");
            try { _listener.Close(); } catch { }
            _listener = null;
            return Task.CompletedTask;
        }
        _facade.ReportTransportState("http", alive: true);
        _facade.SetPendingClientSource(_auth);
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log?.Info($"MCP HTTP ingress listening on {LocalPrefix} (issuer {_issuerUrl})", "Mcp");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopCts.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* петля уже завершилась/отменена */ }
        }
        _facade.ReportTransportState("http", alive: false, "listener остановлен");
        _facade.SetPendingClientSource(null);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }

    // ── accept loop ─────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        while (!_stopCts.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log?.Warn($"MCP HTTP accept failed: {ex.Message}", "Mcp");
                continue;
            }
            _ = Task.Run(() => SafeHandleAsync(ctx));
        }
    }

    private async Task SafeHandleAsync(HttpListenerContext ctx)
    {
        try { await HandleAsync(ctx).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log?.Warn($"MCP HTTP {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}: {ex.Message}", "Mcp");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
        finally
        {
            try { ctx.Response.OutputStream.Dispose(); } catch { }
        }
    }

    // ── routing ─────────────────────────────────────────────────────────────

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "/";

        switch (path)
        {
            case "/.well-known/oauth-authorization-server":
            case "/.well-known/openid-configuration":
                if (req.HttpMethod == "GET") await SendAuthorizationServerMetadata(ctx);
                else await SendMethodNotAllowed(ctx);
                return;
            case "/.well-known/oauth-protected-resource":
            case "/.well-known/oauth-protected-resource/mcp":
                if (req.HttpMethod == "GET") await SendProtectedResourceMetadata(ctx);
                else await SendMethodNotAllowed(ctx);
                return;
            case "/register":
                if (req.HttpMethod == "POST") await HandleRegisterAsync(ctx);
                else await SendMethodNotAllowed(ctx);
                return;
            case "/authorize":
                if (req.HttpMethod == "GET") await HandleAuthorizeAsync(ctx);
                else await SendMethodNotAllowed(ctx);
                return;
            case "/token":
                if (req.HttpMethod == "POST") await HandleTokenAsync(ctx);
                else await SendMethodNotAllowed(ctx);
                return;
            case "/oauth/consent-status":
                if (req.HttpMethod == "GET") await HandleConsentStatusAsync(ctx);
                else await SendMethodNotAllowed(ctx);
                return;
            case "/mcp":
                if (req.HttpMethod == "POST") await HandleMcpAsync(ctx);
                else await SendMethodNotAllowed(ctx);
                return;
            default:
                await SendJsonAsync(ctx, 404, "{\"error\":\"not_found\"}");
                return;
        }
    }

    // ── OAuth metadata ──────────────────────────────────────────────────────

    private Task SendAuthorizationServerMetadata(HttpListenerContext ctx)
    {
        // RFC 8414: issuer без /mcp; эндпоинты — абсолютные URL.
        var json = "{"
            + $"\"issuer\":{J(_issuerUrl)},"
            + $"\"authorization_endpoint\":{J(_issuerUrl + "/authorize")},"
            + $"\"token_endpoint\":{J(_issuerUrl + "/token")},"
            + $"\"registration_endpoint\":{J(_issuerUrl + "/register")},"
            + "\"response_types_supported\":[\"code\"],"
            + "\"grant_types_supported\":[\"authorization_code\",\"refresh_token\"],"
            + "\"token_endpoint_auth_methods_supported\":[\"none\"],"
            + "\"code_challenge_methods_supported\":[\"S256\"],"
            + "\"scopes_supported\":[\"mcp\"]"
            + "}";
        return SendJsonAsync(ctx, 200, json);
    }

    private Task SendProtectedResourceMetadata(HttpListenerContext ctx)
    {
        var json = "{"
            + $"\"resource\":{J(_resourceUrl)},"
            + $"\"authorization_servers\":[{J(_issuerUrl)}],"
            + "\"bearer_methods_supported\":[\"header\"]"
            + "}";
        return SendJsonAsync(ctx, 200, json);
    }

    // ── RFC 7591 register ───────────────────────────────────────────────────

    private async Task HandleRegisterAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);
        List<string>? redirects = null;
        string name = "mcp-client";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("redirect_uris", out var ru) && ru.ValueKind == JsonValueKind.Array)
                redirects = ru.EnumerateArray()
                              .Where(e => e.ValueKind == JsonValueKind.String)
                              .Select(e => e.GetString()!)
                              .ToList();
            if (root.TryGetProperty("client_name", out var cn) && cn.ValueKind == JsonValueKind.String)
                name = cn.GetString() ?? name;
        }
        catch (JsonException)
        {
            await SendJsonAsync(ctx, 400, "{\"error\":\"invalid_client_metadata\"}");
            return;
        }
        if (redirects == null || redirects.Count == 0)
        {
            await SendJsonAsync(ctx, 400,
                "{\"error\":\"invalid_redirect_uri\",\"error_description\":\"redirect_uris required\"}");
            return;
        }
        var reg = _auth.RegisterClient(redirects, name);
        if (reg == null)
        {
            // DCR-кап: все слоты заняты approved/used клиентами — вытеснять нельзя.
            await SendJsonAsync(ctx, 429,
                "{\"error\":\"temporarily_unavailable\",\"error_description\":\"client store is full\"}");
            return;
        }
        var json = "{"
            + $"\"client_id\":{J(reg.ClientId)},"
            + "\"token_endpoint_auth_method\":\"none\","
            + "\"grant_types\":[\"authorization_code\",\"refresh_token\"],"
            + "\"response_types\":[\"code\"],"
            + $"\"redirect_uris\":[{string.Join(",", reg.RedirectUris.Select(J))}],"
            + $"\"client_name\":{J(name)}"
            + "}";
        await SendJsonAsync(ctx, 201, json);
    }

    // ── authorize ───────────────────────────────────────────────────────────

    /// <summary>
    /// GET /authorize?response_type=code&client_id=..&redirect_uri=..&state=..&
    /// code_challenge=..&code_challenge_method=S256[&connection_id=..].
    /// Consent-модель (advisory-fix): первый контакт client_id → pending-консент +
    /// HTML-интерстишел с поллингом /oauth/consent-status; решение принимает
    /// владелец локально (Настройки → MCP). Уже approved клиент → auto-consent
    /// (повторные подтверждения не нужны — спека).
    /// PKCE: только S256; plain/невалидный method и отсутствие challenge → 400.
    /// </summary>
    private async Task HandleAuthorizeAsync(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        var responseType = q["response_type"] ?? "";
        var clientId = q["client_id"] ?? "";
        var redirectUri = q["redirect_uri"] ?? "";
        var state = q["state"];
        var challenge = q["code_challenge"] ?? "";
        var challengeMethod = q["code_challenge_method"];
        var connectionId = q["connection_id"];

        var errorRedirect = new Func<string, Task>(err =>
        {
            var sep = redirectUri.Contains('?') ? "&" : "?";
            var loc = redirectUri + sep + "error=" + Uri.EscapeDataString(err)
                      + (state != null ? "&state=" + Uri.EscapeDataString(state) : "");
            return RedirectAsync(ctx, loc);
        });

        if (responseType != "code") { await errorRedirect("unsupported_response_type"); return; }
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri))
        {
            await SendJsonAsync(ctx, 400, "{\"error\":\"invalid_request\"}");
            return;
        }
        if (!_auth.RedirectUriAllowed(clientId, redirectUri))
        {
            await SendJsonAsync(ctx, 400, "{\"error\":\"invalid_request\",\"error_description\":\"redirect_uri\"}");
            return;
        }
        // PKCE обязателен и только S256 (advertised в metadata). Отсутствие
        // code_challenge_method при наличии challenge = S256 по RFC 7636.
        if (string.IsNullOrEmpty(challenge) ||
            (challengeMethod != null && challengeMethod != "S256"))
        {
            await SendJsonAsync(ctx, 400,
                "{\"error\":\"invalid_request\",\"error_description\":\"PKCE S256 required\"}");
            return;
        }

        var target = ResolveAuthorizationConnection(connectionId);
        if (target == null)
        {
            await SendJsonAsync(ctx, 403,
                "{\"error\":\"access_denied\",\"error_description\":\"no active Buddy MCP connection\"}");
            return;
        }

        // Consent gate: неизвестный owner'у клиент код не получает, пока владелец
        // Buddy не разрешит (карточка в Настройки → MCP). Approved → код сразу.
        if (!_auth.IsApproved(clientId))
        {
            var scopeLabel = $"{target.Name} ({target.Scope})";
            var decision = _auth.EnsurePending(
                clientId, redirectUri, challenge, state, target.Id, scopeLabel);
            if (decision == null)
            {
                await SendJsonAsync(ctx, 429,
                    "{\"error\":\"temporarily_unavailable\",\"error_description\":\"too many pending consents\"}");
                return;
            }
            if (decision.Value.Resolved)
            {
                // Повторный /authorize по уже решённому запросу (meta-refresh).
                await RedirectAsync(ctx, decision.Value.RedirectUrl!);
                return;
            }
            await SendConsentPageAsync(ctx, decision.Value.Pending, challenge, state, connectionId);
            return;
        }

        var code = _auth.BeginAuthorization(clientId, redirectUri, challenge, "S256", target.Id);
        if (code == null)
        {
            await SendJsonAsync(ctx, 400, "{\"error\":\"invalid_request\"}");
            return;
        }
        var sep2 = redirectUri.Contains('?') ? "&" : "?";
        var location = redirectUri + sep2 + "code=" + Uri.EscapeDataString(code)
                       + (state != null ? "&state=" + Uri.EscapeDataString(state) : "");
        await RedirectAsync(ctx, location);
    }

    /// <summary>
    /// GET /oauth/consent-status?pending={id} → {"state":"pending|approved|denied|expired"[,"redirect":".."]}.
    /// Поллит браузер клиента с интерстишела; решение принимает владелец локально.
    /// </summary>
    private async Task HandleConsentStatusAsync(HttpListenerContext ctx)
    {
        var pendingId = ctx.Request.QueryString["pending"] ?? "";
        var st = _auth.ConsentStatus(pendingId);
        if (st == null)
        {
            await SendJsonAsync(ctx, 404, "{\"error\":\"not_found\"}");
            return;
        }
        var json = "{\"state\":" + J(st.Value.State)
            + (st.Value.RedirectUrl != null ? ",\"redirect\":" + J(st.Value.RedirectUrl) : "")
            + "}";
        await SendJsonAsync(ctx, 200, json);
    }

    /// <summary>
    /// Интерстишел «ждём владельца»: JS-поллинг consent-status → redirect;
    /// без JS — meta-refresh на /authorize (дедуп вернёт готовый redirect).
    /// </summary>
    private Task SendConsentPageAsync(
        HttpListenerContext ctx, McpPendingClient p,
        string codeChallenge, string? state, string? connectionId)
    {
        var name = HtmlEncode(string.IsNullOrEmpty(p.ClientName) ? p.ClientId : p.ClientName!);
        // Полный повтор исходного запроса: дедуп по clientId+challenge вернёт
        // уже принятое решение (redirect), не создавая новый pending.
        var authRetry = "/authorize?response_type=code&client_id=" + Uri.EscapeDataString(p.ClientId)
            + "&redirect_uri=" + Uri.EscapeDataString(p.RedirectUri)
            + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
            + "&code_challenge_method=S256"
            + (state != null ? "&state=" + Uri.EscapeDataString(state) : "")
            + (connectionId != null ? "&connection_id=" + Uri.EscapeDataString(connectionId) : "");
        var html = "<!doctype html><html><head><meta charset=\"utf-8\">"
            + "<meta http-equiv=\"refresh\" content=\"6;url=" + HtmlEncode(authRetry) + "\">"
            + "<title>BrainstormBuddy — access request</title></head><body>"
            + "<h2>BrainstormBuddy MCP</h2>"
            + "<p>Client <b>" + name + "</b> is requesting access to BrainstormBuddy data.</p>"
            + "<p>The request is waiting for the owner's approval in the Buddy app "
            + "(Settings → MCP). Keep this page open — it will redirect automatically.</p>"
            + "<script>(function(){var id=" + System.Text.Json.JsonSerializer.Serialize(p.PendingId) + ";"
            + "function poll(){fetch('/oauth/consent-status?pending='+encodeURIComponent(id))"
            + ".then(function(r){return r.json()}).then(function(j){"
            + "if(j.state==='pending'){setTimeout(poll,2000);}"
            + "else if(j.redirect){window.location=j.redirect;}"
            + "else{document.body.innerHTML='<p>Request expired — restart the connection.</p>';}})"
            + ".catch(function(){setTimeout(poll,4000);});}poll();})();</script>"
            + "<noscript><p><a href=\"" + HtmlEncode(authRetry) + "\">Check again</a></p></noscript>"
            + "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var resp = ctx.Response;
        resp.StatusCode = 200;
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        return resp.OutputStream.WriteAsync(bytes).AsTask().ContinueWith(_ => resp.Close());
    }

    private static string HtmlEncode(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    /// <summary>connection_id параметр → единственное активное → первое не-suspended.</summary>
    private McpConnectionEntry? ResolveAuthorizationConnection(string? connectionId)
    {
        var conns = _connections();
        if (!string.IsNullOrEmpty(connectionId))
            return conns.FirstOrDefault(c => c.Id == connectionId && !c.Suspended);
        var active = conns.Where(c => !c.Suspended).ToList();
        return active.Count switch
        {
            0 => null,
            _ => active[0], // несколько — первый; клиент может уточнить connection_id
        };
    }

    // ── token ───────────────────────────────────────────────────────────────

    private async Task HandleTokenAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);
        var form = ParseForm(body);
        var grantType = form.GetValueOrDefault("grant_type") ?? "";

        (string Access, int Expires, string? Refresh)? tokens = null;
        switch (grantType)
        {
            case "authorization_code":
                tokens = _auth.ExchangeCode(
                    form.GetValueOrDefault("code") ?? "",
                    form.GetValueOrDefault("code_verifier") ?? "",
                    form.GetValueOrDefault("client_id") ?? "",
                    form.GetValueOrDefault("redirect_uri") ?? "");
                break;
            case "refresh_token":
                tokens = _auth.ExchangeRefresh(
                    form.GetValueOrDefault("refresh_token") ?? "",
                    form.GetValueOrDefault("client_id") ?? "");
                break;
            default:
                await SendJsonAsync(ctx, 400, "{\"error\":\"unsupported_grant_type\"}");
                return;
        }

        if (tokens == null)
        {
            await SendJsonAsync(ctx, 400,
                "{\"error\":\"invalid_grant\",\"error_description\":\"code/refresh invalid or PKCE mismatch\"}");
            return;
        }
        var json = "{"
            + $"\"access_token\":{J(tokens.Value.Access)},"
            + "\"token_type\":\"Bearer\","
            + $"\"expires_in\":{tokens.Value.Expires}"
            + (tokens.Value.Refresh != null ? $",\"refresh_token\":{J(tokens.Value.Refresh)}" : "")
            + ",\"scope\":\"mcp\""
            + "}";
        await SendJsonAsync(ctx, 200, json);
    }

    // ── /mcp JSON-RPC ───────────────────────────────────────────────────────

    private async Task HandleMcpAsync(HttpListenerContext ctx)
    {
        var conn = ResolveBearer(ctx.Request);
        if (conn == null)
        {
            ctx.Response.StatusCode = 401;
            // RFC 9728: resource_metadata указывает, где взять метаданные AS.
            ctx.Response.Headers["WWW-Authenticate"] =
                $"Bearer resource_metadata=\"{_issuerUrl}/.well-known/oauth-protected-resource\", realm=\"mcp\"";
            await SendJsonAsync(ctx, 401, "{\"error\":\"unauthorized\"}", writeBody: true,
                statusAlreadySet: true);
            return;
        }
        if (conn.Suspended)
        {
            await SendJsonAsync(ctx, 403, "{\"error\":\"forbidden\",\"error_description\":\"connection suspended\"}");
            return;
        }

        // Handshake-контакт по HTTP (не «Подключено» — только метка транспорта).
        _facade.UpdateConnectionLastSeen(conn.Id, "http");

        var body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);
        if (body.Length == 0)
        {
            await SendJsonAsync(ctx, 400, "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32600,\"message\":\"empty body\"}}");
            return;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            await SendJsonAsync(ctx, 200,
                "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"parse error\"}}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                // JSON-RPC batch: ответ массивом, notifications пропускаем.
                var responses = new List<string>();
                foreach (var item in root.EnumerateArray())
                {
                    var r = await DispatchSingleAsync(item, conn).ConfigureAwait(false);
                    if (r != null) responses.Add(r);
                }
                if (responses.Count == 0)
                {
                    ctx.Response.StatusCode = 202;
                    ctx.Response.Close();
                    return;
                }
                await SendJsonAsync(ctx, 200, "[" + string.Join(",", responses) + "]");
                return;
            }
            var single = await DispatchSingleAsync(root, conn).ConfigureAwait(false);
            if (single == null)
            {
                // notification: accept без тела (Streamable HTTP spec).
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }
            await SendJsonAsync(ctx, 200, single);
        }
    }

    /// <summary>Один JSON-RPC элемент → сериализованный ответ (null для notification).</summary>
    private async Task<string?> DispatchSingleAsync(
        JsonElement req, McpResolvedConnection conn)
    {
        if (req.ValueKind != JsonValueKind.Object ||
            !req.TryGetProperty("method", out var mEl) || mEl.ValueKind != JsonValueKind.String)
            return "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32600,\"message\":\"invalid request\"}}";

        var method = mEl.GetString()!;
        var idJson = req.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : null;
        var hasId = idJson != null;
        req.TryGetProperty("params", out var prms);

        string? resultJson;
        switch (method)
        {
            case "initialize":
            {
                var clientProto = prms.ValueKind == JsonValueKind.Object &&
                                  prms.TryGetProperty("protocolVersion", out var pv) &&
                                  pv.ValueKind == JsonValueKind.String
                    ? pv.GetString()! : McpProtocolVersion;
                resultJson = "{"
                    + $"\"protocolVersion\":{J(clientProto)},"
                    + "\"capabilities\":{\"tools\":{\"listChanged\":false}},"
                    + $"\"serverInfo\":{{\"name\":\"brainstorm-buddy\",\"version\":{J(ServerVersion)}}}"
                    + "}";
                // initialize — аутентичный контакт; «Подключено» ставит реальный вызов.
                _facade.UpdateConnectionLastSeen(conn.Id, "http");
                break;
            }
            case "notifications/initialized":
            case "notifications/cancelled":
            case "notifications/progress":
            case "notifications/roots/list_changed":
                return null;
            case "ping":
                resultJson = "{}";
                break;
            case "tools/list":
                resultJson = "{\"tools\":" + McpHttpToolBridge.ToolListJson() + "}";
                break;
            case "resources/list":
                resultJson = "{\"resources\":[]}";
                break;
            case "resources/templates/list":
                resultJson = "{\"resourceTemplates\":[]}";
                break;
            case "prompts/list":
                resultJson = "{\"prompts\":[]}";
                break;
            case "logging/setLevel":
                resultJson = "{}";
                break;
            case "tools/call":
            {
                var name = prms.ValueKind == JsonValueKind.Object &&
                           prms.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : "";
                var arguments = prms.ValueKind == JsonValueKind.Object &&
                                prms.TryGetProperty("arguments", out var a) ? a : default;
                if (!McpHttpToolBridge.TryMapCall(name, arguments, out var facadeMethod, out var facadeParams))
                    return RpcError(idJson, McpRpcError.MethodNotFound, $"unknown tool '{name}'");
                try
                {
                    var result = await _facade.InvokeFacadeAsync(facadeMethod, facadeParams, conn, _stopCts.Token)
                                          .ConfigureAwait(false);
                    resultJson = WrapToolResult(result.GetRawText());
                }
                catch (McpRpcException rex)
                {
                    // Как у stdio-хоста BuddyTools: ошибки фасада → isError-content, не RPC error.
                    resultJson = WrapToolResult(rex.Message, isError: true);
                }
                catch (Exception ex)
                {
                    _log?.Warn($"MCP HTTP tool {name} threw: {ex.Message}", "Mcp");
                    resultJson = WrapToolResult("internal error", isError: true);
                }
                break;
            }
            // Pipe-методы доступны и по HTTP — для симметрии и отладки.
            default:
            {
                if (method.StartsWith("facade.", StringComparison.Ordinal) || method == "auth.resolve")
                {
                    var resp = await _facade.TryDispatchHttpJsonRpcAsync(req.GetRawText(), conn, _stopCts.Token)
                                             .ConfigureAwait(false);
                    return resp ?? (hasId ? null : null); // notification → null
                }
                return RpcError(idJson, McpRpcError.MethodNotFound, $"unknown method '{method}'");
            }
        }
        if (!hasId) return null; // notification с известным методом — тихо
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{resultJson}}}";
    }

    /// <summary>MCP tool result envelope: {content:[{type:'text',text}],isError}.
    /// isError эмитим ВСЕГДА (и false на успехе): клиенты читают поле слепо,
    /// опциональность по спеке не оправдывает KeyNotFound у потребителей.</summary>
    private static string WrapToolResult(string payloadJsonOrText, bool isError = false)
        => "{\"content\":[{\"type\":\"text\",\"text\":" + J(payloadJsonOrText) + "}]"
           + ",\"isError\":" + (isError ? "true" : "false") + "}";

    private static string RpcError(string? idJson, int code, string message)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson ?? "null"},"
           + $"\"error\":{{\"code\":{code},\"message\":{J(message)}}}}}";

    private McpResolvedConnection? ResolveBearer(HttpListenerRequest req)
    {
        var header = req.Headers["Authorization"];
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = header["Bearer ".Length..].Trim();
        return _auth.ResolveAccessToken(token);
    }

    // ── IO helpers ──────────────────────────────────────────────────────────

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        var total = 0;
        int n;
        while ((n = await req.InputStream.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > MaxBodyBytes)
                throw McpRpcException.InvalidParams("request body too large");
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var k = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            var v = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            dict[k] = v;
        }
        return dict;
    }

    private async Task SendJsonAsync(
        HttpListenerContext ctx, int status, string json,
        bool writeBody = true, bool statusAlreadySet = false)
    {
        var resp = ctx.Response;
        if (!statusAlreadySet) resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-store";
        if (writeBody)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        resp.Close();
    }

    private Task SendMethodNotAllowed(HttpListenerContext ctx)
        => SendJsonAsync(ctx, 405, "{\"error\":\"method_not_allowed\"}");

    private async Task RedirectAsync(HttpListenerContext ctx, string location)
    {
        ctx.Response.StatusCode = 302;
        ctx.Response.RedirectLocation = location;
        ctx.Response.Close();
        await Task.CompletedTask;
    }

    private static string J(string s) => JsonSerializer.Serialize(s);
}
