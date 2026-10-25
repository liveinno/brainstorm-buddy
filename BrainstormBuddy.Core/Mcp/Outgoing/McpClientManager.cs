using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>
/// Менеджер исходящих MCP-подключений: stdio (процесс пользователя) и HTTP
/// (Streamable HTTP / SSE, авто-детект в SDK 2.2). Кеширует McpClient + список
/// инструментов на сервер, переподключается при сбое вызова. Секрет из DPAPI-сторе
/// (SecretRef) пробрасывается как Bearer-заголовок (http) или MCP_AUTH_TOKEN (stdio).
///
/// Расширенный журнал (McpJournal): connect → probe → initialize → tools/list →
/// tools/call → reconnect, плюс ошибки со стеком. Секреты в журнал не попадают.
/// </summary>
public sealed class McpClientManager : IMcpToolTransport, IAsyncDisposable
{
    /// <summary>Таймаут пред-флайт-зонда initialize, сек.</summary>
    private const int ProbeTimeoutSeconds = 15;

    private sealed class Entry
    {
        public McpClient? Client;
        public IReadOnlyList<McpToolSpec>? Tools;
        public McpServerState State = new(McpServerConnectionStatus.Disconnected, null, 0, null);
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _secretOverrides = new(StringComparer.Ordinal);
    private readonly Func<McpOutgoingServer, string?> _resolveSecret;
    private readonly LoggingService? _logger;
    private readonly Func<LoggingService?> _loggerFactory;
    private readonly McpJournal? _journal;

    public McpClientManager(Func<McpOutgoingServer, string?>? resolveSecret = null,
        LoggingService? logger = null, McpJournal? journal = null)
    {
        _resolveSecret = resolveSecret ?? (_ => null);
        _logger = logger;
        _loggerFactory = () => _logger;
        _journal = journal;
    }

    /// <summary>Текущий статус подключения (для UI/диагностики).</summary>
    public McpServerState GetStatus(McpOutgoingServer server)
        => _entries.TryGetValue(server.Id, out var e) ? e.State
            : new McpServerState(McpServerConnectionStatus.Disconnected, null, 0, null);

    /// <summary>
    /// Временно подменить секрет сервера (кнопка «Проверить»): значение ещё не
    /// сохранено в защищённом хранилище. Приоритетнее _resolveSecret.
    /// Кеш соединения сбрасывается — следующий вызов поднимет клиент с новым токеном.
    /// </summary>
    public void SetSecretOverride(string serverId, string? secret)
    {
        if (string.IsNullOrEmpty(serverId)) return;
        if (string.IsNullOrWhiteSpace(secret))
        {
            _secretOverrides.TryRemove(serverId, out _);
            DropCached(serverId);
            return;
        }
        _secretOverrides[serverId] = secret;
        _journal?.RegisterSecret(secret);
        DropCached(serverId);
    }

    /// <summary>Снять временный секрет сервера (кеш соединения тоже сбрасывается).</summary>
    public void ClearSecretOverride(string serverId)
    {
        if (string.IsNullOrEmpty(serverId)) return;
        _secretOverrides.TryRemove(serverId, out _);
        DropCached(serverId);
    }

    /// <summary>Сбросить закешированного клиента сервера (смена секрета/настроек).</summary>
    private void DropCached(string serverId)
    {
        if (!_entries.TryRemove(serverId, out var e) || e.Client == null) return;
        _ = Task.Run(async () =>
        {
            try { await e.Client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _loggerFactory()?.Debug($"MCP→ dispose «{serverId}»: {ex.Message}", "Mcp"); }
        });
    }

    /// <summary>Резолв секрета: временный override ?? хранилище (SecretRef).</summary>
    public string? ResolveSecret(McpOutgoingServer server)
    {
        if (server == null) return null;
        if (_secretOverrides.TryGetValue(server.Id, out var ov)) return ov;
        return _resolveSecret(server);
    }

    /// <summary>
    /// Подключение + tools/list. Повторные вызовы дёшевы (кеш). При ошибке —
    /// статус Failed и исключение наверх (caller переводит job в WaitingConnection).
    /// </summary>
    public async Task<IReadOnlyList<McpToolSpec>> ListToolsAsync(McpOutgoingServer server, CancellationToken ct)
    {
        var entry = await EnsureConnectedAsync(server, ct).ConfigureAwait(false);
        return entry.Tools!;
    }

    /// <summary>Вызов инструмента. При обрыве соединения сбрасывает кеш клиента (реконнект на след. вызове).</summary>
    public async Task<McpToolCallResult> CallToolAsync(
        McpOutgoingServer server, McpToolSpec tool, string argsJson,
        IProgress<McpToolProgress>? progress, CancellationToken ct)
    {
        var entry = await EnsureConnectedAsync(server, ct).ConfigureAwait(false);
        var client = entry.Client!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Вызов (begin/result) пишет tool-loop: там же видны аргументы, длительность
        // и превью результата. Здесь дублировать нельзя — иначе один вызов даёт
        // две записи «tools/call» и журнал нельзя считать (одна задача = один вызов).
        try
        {
            IReadOnlyDictionary<string, object?>? args = null;
            if (!string.IsNullOrWhiteSpace(argsJson))
                args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
            IProgress<ModelContextProtocol.ProgressNotificationValue>? sdkProgress = null;
            if (progress != null)
                sdkProgress = new Progress<ModelContextProtocol.ProgressNotificationValue>(v =>
                    progress.Report(new McpToolProgress(
                        v.Progress, v.Total, v.Message)));
            var result = await client.CallToolAsync(tool.OriginalName, args, sdkProgress, null, ct)
                .ConfigureAwait(false);
            sw.Stop();
            var sb = new StringBuilder();
            foreach (var block in result.Content)
            {
                if (block is TextContentBlock t) sb.Append(t.Text);
                else if (block is EmbeddedResourceBlock) sb.Append("[embedded resource]");
                else if (block is ImageContentBlock) sb.Append("[image]");
                else if (block is AudioContentBlock) sb.Append("[audio]");
                else sb.Append($"[{block.Type}]");
            }
            return new McpToolCallResult(sb.ToString(), result.IsError == true, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Единственная запись об ошибке вызова: транспорт знает HTTP-статус.
            _journal?.AppendEvent("error", server: server.Name, tool: tool.AdapterName,
                detail: ex.Message, stack: ex.StackTrace, isError: true,
                httpStatus: HttpStatusOf(ex),
                errorKind: McpOutgoingError.Classify(ex).Kind.ToString(), state: "tools/call");
            // Обрыв транспорта/протокольная ошибка → выкидываем закешированного клиента,
            // следующий EnsureConnected переподключится.
            await DropAsync(server, "reconnect").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Учёт аннотаций протокола в оценке риска для gate'а подтверждений.</summary>
    private static McpActionRisk ClassifyRisk(McpClientTool tool)
    {
        var a = tool.ProtocolTool?.Annotations;
        if (a?.DestructiveHint == true) return McpActionRisk.Destructive;
        if (a?.ReadOnlyHint == true) return McpActionRisk.ReadOnly;
        return McpActionRisk.Mutating;
    }

    private async Task<Entry> EnsureConnectedAsync(McpOutgoingServer server, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = _entries.GetOrAdd(server.Id, _ => new Entry());
            if (entry.Client != null && entry.Tools != null) return entry;

            entry.State = new McpServerState(McpServerConnectionStatus.Connecting, null, 0, null);
            var secret = ResolveSecret(server);
            _journal?.RegisterSecret(secret);
            var isHttp = string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase);
            var authKind = string.IsNullOrEmpty(secret) ? "none" : (isHttp ? "bearer" : "env");
            var safeUrl = SanitizeUrl(server.Url);
            _journal?.AppendEvent("connect", server: server.Name, transport: server.Transport,
                url: safeUrl, authKind: authKind, hasSecret: !string.IsNullOrEmpty(secret),
                state: "begin");
            _loggerFactory()?.Info($"MCP→ подключение к «{server.Name}» ({server.Transport})", "Mcp");

            // Пред-флайт: лёгкий POST initialize — HTTP-статус и версия протокола.
            // Ошибка зонда не ломает подключение: SDK-клиент может подняться.
            McpHttpProbeResult? probe = null;
            if (isHttp && !string.IsNullOrWhiteSpace(server.Url))
            {
                try
                {
                    probe = await McpHttpProbe
                        .InitializeAsync(server.Url, secret, ProbeTimeoutSeconds, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    probe = new McpHttpProbeResult(false, null, null, null, null,
                        ex.Message, McpOutgoingErrorKind.Unknown, 0);
                }
                _journal?.AppendEvent("probe", server: server.Name, transport: server.Transport,
                    url: safeUrl, protocolVersion: probe.ProtocolVersion,
                    httpStatus: probe.HttpStatus, isError: probe.Ok ? null : true,
                    detail: probe.ErrorText, durationMs: probe.DurationMs, state: "probe");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var transport = CreateTransport(server, secret);
            var client = await McpClient.CreateAsync(transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "BrainstormBuddy", Version = "1.0" },
                },
                loggerFactory: null, ct).ConfigureAwait(false);
            sw.Stop();

            var sdkProtocol = client.NegotiatedProtocolVersion;
            var serverInfo = client.ServerInfo;
            _journal?.AppendEvent("initialize", server: server.Name, transport: server.Transport,
                url: safeUrl, authKind: authKind, hasSecret: !string.IsNullOrEmpty(secret),
                protocolVersion: sdkProtocol ?? probe?.ProtocolVersion,
                httpStatus: probe?.HttpStatus,
                detail: serverInfo == null ? null : $"{serverInfo.Name}/{serverInfo.Version}",
                durationMs: sw.ElapsedMilliseconds, state: "initialize");

            var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var specs = new List<McpToolSpec>(tools.Count);
            foreach (var t in tools)
            {
                var adapter = McpToolNaming.MakeAdapterName(server.Name, t.Name, used);
                specs.Add(new McpToolSpec(
                    adapter, server.Id, server.Name, t.Name,
                    t.Description, t.JsonSchema, ClassifyRisk(t)));
            }
            entry.Client = client;
            entry.Tools = specs;
            entry.State = new McpServerState(McpServerConnectionStatus.Connected, null, specs.Count, null);
            _journal?.AppendEvent("tools/list", server: server.Name, transport: server.Transport,
                url: safeUrl, protocolVersion: sdkProtocol ?? probe?.ProtocolVersion,
                toolCount: specs.Count, tools: specs.Select(s => s.OriginalName).ToList(),
                state: "tools/list");
            _loggerFactory()?.Info($"MCP→ «{server.Name}»: {specs.Count} инструментов", "Mcp");
            return entry;
        }
        catch (Exception ex)
        {
            var entry = _entries.GetOrAdd(server.Id, _ => new Entry());
            entry.State = new McpServerState(McpServerConnectionStatus.Failed, ex.Message, 0, DateTime.UtcNow);
            _journal?.AppendEvent("error", server: server.Name, transport: server.Transport,
                url: SanitizeUrl(server.Url), detail: ex.Message, stack: ex.StackTrace,
                isError: true, httpStatus: HttpStatusOf(ex),
                errorKind: McpOutgoingError.Classify(ex).Kind.ToString(), state: "connect");
            _loggerFactory()?.Warn($"MCP→ «{server.Name}»: сбой подключения: {ex.Message}", "Mcp");
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private IClientTransport CreateTransport(McpOutgoingServer server, string? secret)
    {
        if (string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            var opts = new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url, UriKind.Absolute),
                TransportMode = HttpTransportMode.AutoDetect,
                Name = server.Name,
            };
            if (!string.IsNullOrEmpty(secret))
                opts.AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = secret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? secret : $"Bearer {secret}",
                };
            return new HttpClientTransport(opts);
        }

        var stdio = new StdioClientTransportOptions
        {
            Command = server.Command,
            Arguments = SplitArgs(server.Args).ToList(),
            Name = server.Name,
            // Полный env наследуем: пользователь запускает свой сервер, ему может
            // понадобиться PATH/HOME/корпоративные переменные. Секрет — только явно.
            InheritEnvironmentVariables = true,
            StandardErrorLines = line =>
                _loggerFactory()?.Debug($"MCP→[{server.Name}] stderr: {line}", "Mcp"),
        };
        if (!string.IsNullOrEmpty(secret))
            stdio.EnvironmentVariables = new Dictionary<string, string?> { ["MCP_AUTH_TOKEN"] = secret };
        return new StdioClientTransport(stdio);
    }

    /// <summary>URL без userinfo и секретных query-параметров (для журнала).</summary>
    internal static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return McpJournal.Redact(url);
        var sb = new System.Text.StringBuilder();
        sb.Append(uri.GetLeftPart(UriPartial.Authority));
        if (uri.AbsolutePath.Length > 0) sb.Append(uri.AbsolutePath);
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var parts = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries);
            var safe = new List<string>();
            foreach (var part in parts)
            {
                var name = part.Split('=', 2)[0];
                safe.Add(IsSecretParam(name) ? name + "=***" : part);
            }
            if (safe.Count > 0) sb.Append('?').Append(string.Join('&', safe));
        }
        return sb.ToString();
    }

    private static bool IsSecretParam(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = name.Trim().ToLowerInvariant();
        return n.Contains("token") || n.Contains("secret") || n.Contains("password")
            || n.Contains("key") || n.Contains("auth") || n.Contains("apikey");
    }

    /// <summary>HTTP-статус из исключения (если есть).</summary>
    private static int? HttpStatusOf(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException hre && hre.StatusCode.HasValue)
                return (int)hre.StatusCode.Value;
        }
        return null;
    }

    /// <summary>Разбор строки аргументов с кавычками: `a "b c" d` → [a, "b c", d].</summary>
    internal static IReadOnlyList<string> SplitArgs(string? args)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return list;
        var cur = new StringBuilder();
        var inQuote = false;
        var hasToken = false;
        foreach (var c in args)
        {
            if (c == '"') { inQuote = !inQuote; hasToken = true; continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (hasToken) { list.Add(cur.ToString()); cur.Clear(); hasToken = false; }
                continue;
            }
            cur.Append(c);
            hasToken = true;
        }
        if (hasToken) list.Add(cur.ToString());
        return list;
    }

    private async Task DropAsync(McpOutgoingServer server, string reason = "disconnect")
    {
        var dropped = false;
        if (_entries.TryRemove(server.Id, out var e) && e.Client != null)
        {
            dropped = true;
            try { await e.Client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _loggerFactory()?.Debug($"MCP→ dispose «{server.Name}»: {ex.Message}", "Mcp"); }
        }
        if (dropped)
            _journal?.AppendEvent("reconnect", server: server.Name, transport: server.Transport,
                url: SanitizeUrl(server.Url),
                detail: string.Equals(reason, "reconnect", StringComparison.Ordinal)
                    ? "соединение сброшено, будет переподключение"
                    : "отключено по запросу",
                state: "reconnect");
    }

    /// <summary>Отключить конкретный сервер (смена настроек/выключение).</summary>
    public Task DisconnectAsync(McpOutgoingServer server) => DropAsync(server, "disconnect");

    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _entries)
        {
            if (kv.Value.Client == null) continue;
            try { await kv.Value.Client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _loggerFactory()?.Debug($"MCP→ dispose «{kv.Key}»: {ex.Message}", "Mcp"); }
        }
        _entries.Clear();
    }
}
