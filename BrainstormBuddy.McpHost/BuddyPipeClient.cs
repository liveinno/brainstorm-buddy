using System.Diagnostics;
using System.Text.Json;
using BrainstormBuddy.Core.Mcp;

namespace BrainstormBuddy.McpHost;

/// <summary>
/// Тонкая обёртка над McpPipeClient для DI в tool-классе: знает файл токена
/// (argv[0]) и умеет читать токен лениво и обновлять его на лету.
/// Ошибки pipe/JSON-RPC всплывают как BuddyBridgeException — Program превращает их
/// в текстовый tool-ответ с понятной причиной и request ID.
///
/// Каждый вызов получает свой request ID (mcph-xxxxxxxxxxxx), который:
///  • уходит в params и пишется сервером в журнал Buddy (связь записей двух сторон);
///  • попадает в собственный журнал хоста {appData}/logs/mcphost.log;
///  • возвращается в тексте ошибки, если вызов не удался.
/// </summary>
public sealed class BuddyPipeClient : IAsyncDisposable
{
    public sealed class BuddyBridgeException : Exception
    {
        public BuddyBridgeException(string message, string? requestId = null, Exception? inner = null)
            : base(message, inner)
        {
            RequestId = requestId;
        }

        /// <summary>ID запроса (для корреляции с журналом Buddy).</summary>
        public string? RequestId { get; }
    }

    private readonly string _tokenFilePath;
    private readonly string _pipeName;
    private readonly McpIncomingJournal _journal;
    private McpPipeClient? _client;

    public BuddyPipeClient(
        string tokenFilePath, string? pipeName = null, McpIncomingJournal? journal = null)
    {
        _tokenFilePath = tokenFilePath;
        _pipeName = pipeName ?? McpPipeNaming.GetDefaultPipeName();
        if (journal != null)
        {
            // Общий экземпляр с Program: один файл — одна ротация.
            _journal = journal;
            return;
        }
        var dataDir = McpIncomingJournal.ResolveDataDirFromTokenFile(tokenFilePath)
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                          "BrainstormBuddy");
        _journal = new McpIncomingJournal(
            Path.Combine(dataDir, "logs"), "mcphost.log",
            mirror: null);
    }

    /// <summary>Журнал вызовов хоста (тот же, что у Program).</summary>
    internal McpIncomingJournal Journal => _journal;

    private McpPipeClient Client()
    {
        if (_client != null) return _client;
        if (!File.Exists(_tokenFilePath))
            throw new BuddyBridgeException(
                $"token file '{_tokenFilePath}' not found — issue a connection in Buddy settings first");
        try
        {
            _client = McpPipeClient.FromTokenFile(_tokenFilePath, _pipeName);
            return _client;
        }
        catch (Exception ex)
        {
            throw new BuddyBridgeException($"cannot read token file '{_tokenFilePath}': {ex.Message}");
        }
    }

    /// <summary>Вызов facade.* с подставленным токеном; ошибки → BuddyBridgeException.</summary>
    public async Task<JsonElement> CallAsync(
        string method, Dictionary<string, object?>? prms, CancellationToken ct)
    {
        var requestId = "mcph-" + Guid.NewGuid().ToString("N")[..12];
        var args = new Dictionary<string, object?>(prms ?? []);
        args["requestId"] = requestId;
        var sw = Stopwatch.StartNew();
        _journal.Info($"call start req={requestId} method={method} params={DescribeArgs(args)}");

        try
        {
            var result = await Client().CallFacadeAsync(method, args, ct).ConfigureAwait(false);
            _journal.Info(
                $"call ok req={requestId} method={method} ms={sw.ElapsedMilliseconds} result={result.GetRawText().Length} bytes");
            return result;
        }
        catch (McpPipeClient.RpcException rex)
        {
            _journal.Error(
                $"call failed req={requestId} method={method} ms={sw.ElapsedMilliseconds} code={rex.Code} message={rex.Message}");
            throw new BuddyBridgeException($"server error {rex.Code}: {rex.Message}", requestId);
        }
        catch (IOException ex)
        {
            _journal.Error(
                $"call failed req={requestId} method={method} ms={sw.ElapsedMilliseconds} io={ex.Message}");
            DropClient();
            throw new BuddyBridgeException(
                $"cannot reach BrainstormBuddy (pipe '{_pipeName}'): is the app running? {ex.Message}",
                requestId, ex);
        }
        catch (TimeoutException ex)
        {
            _journal.Error(
                $"call failed req={requestId} method={method} ms={sw.ElapsedMilliseconds} timeout={ex.Message}");
            DropClient();
            throw new BuddyBridgeException(
                $"BrainstormBuddy is not listening on pipe '{_pipeName}' — start the app and enable MCP",
                requestId, ex);
        }
        catch (OperationCanceledException)
        {
            _journal.Warn($"call canceled req={requestId} method={method} ms={sw.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            _journal.Error(
                $"call failed req={requestId} method={method} ms={sw.ElapsedMilliseconds} {ex.GetType().Name}: {ex.Message}",
                ex);
            throw new BuddyBridgeException($"unexpected error: {ex.Message}", requestId, ex);
        }
    }

    /// <summary>Сбросить кэш клиента — следующий вызов подключится заново.</summary>
    private void DropClient()
    {
        var c = _client;
        _client = null;
        if (c != null) _ = c.DisposeAsync().AsTask();
    }

    /// <summary>Параметры вызова для журнала: значения масскируются.</summary>
    private static string DescribeArgs(Dictionary<string, object?> args)
    {
        if (args.Count == 0) return "{}";
        var parts = new List<string>(args.Count);
        foreach (var (key, value) in args)
        {
            if (key.Equals("connectionToken", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{key}=***");
                continue;
            }
            parts.Add($"{key}={value}");
        }
        return string.Join(' ', parts);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
            await _client.DisposeAsync().ConfigureAwait(false);
        _journal.Info("host bridge disposed");
    }
}
