using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Services;
using BrainstormBuddy.Transcription;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Входящий MCP-канал: NamedPipeServerStream "buddy-mcp-{UserSid}" + line-delimited
/// JSON-RPC 2.0 → IMcpFacade. Клиенты — экземпляры BrainstormBuddy.McpHost.exe
/// (stdio-бридж для Claude Desktop и т.п.). Конкурентные подключения допустимы
/// (по инстансу пайпа на клиента); ACL = текущий пользователь
/// (PipeOptions.CurrentUserOnly).
///
/// Методы протокола (params всегда содержат connectionToken):
///   auth.resolve          → McpResolvedConnection | null
///   facade.status         → McpServerStatus (без auth — только версия/время)
///   facade.listProjects   → McpProjectInfo[]
///   facade.listMeetings   → McpMeetingInfo[] {projectId?, limit?, cursor?}
///   facade.search         → McpSearchResult {query, types?, projectIds?, limit?}
///   facade.readSource     → McpSourceContent {reference}
///   facade.enqueueJob     → McpJob {kind, argsJson?, idempotencyKey?}
///   facade.getJob         → McpJob|null {jobId}
///   facade.cancelJob      → bool {jobId}
/// </summary>
public sealed class McpIncomingHost : IAsyncDisposable
{
    public const int MaxConcurrentClients = 8;
    private const int MaxLineBytes = 4 * 1024 * 1024;

    private readonly McpJsonRpcDispatch _dispatch;
    private readonly LoggingService? _log;
    private readonly McpIncomingJournal? _journal;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly List<Task> _clientTasks = new();
    private readonly object _tasksGate = new();
    private Task? _acceptLoop;
    private volatile bool _stopping;

    public McpIncomingHost(
        IMcpFacade facade,
        McpPermissionStore permissions,
        Func<IReadOnlyList<McpConnectionEntry>> connections,
        LoggingService? log = null,
        string? pipeName = null,
        McpIncomingJournal? journal = null)
    {
        // Маршрутизация методов — в общем dispatch (его же использует HTTP-ingress).
        _dispatch = new McpJsonRpcDispatch(
            facade, permissions, connections, log, transport: "pipe", journal: journal);
        _log = log;
        _journal = journal;
        _pipeName = pipeName ?? McpPipeNaming.GetDefaultPipeName();
    }

    public string PipeName => _pipeName;
    /// <summary>Фасад этого хоста — нужен composition root для HTTP-ingress на тех же данных.</summary>
    public IMcpFacade Facade => _dispatch.Facade;
    /// <summary>Общий dispatch (pipe=transport «pipe»); для тестов и HTTP-моста.</summary>
    public McpJsonRpcDispatch Dispatch => _dispatch;
    public bool IsRunning => _acceptLoop != null && !_stopping;

    /// <summary>Запуск accept-цикла. Идемпотентен.</summary>
    public void Start()
    {
        if (_acceptLoop != null) return;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log?.Info($"MCP incoming host listening on pipe {_pipeName}", "Mcp");
        _journal?.Info($"host started pipe={_pipeName} maxClients={MaxConcurrentClients}");
    }

    /// <summary>
    /// Всё-в-одном для composition root: при выключенном MCP/stdio-хосте вернёт null.
    /// Не бросает — входящий канал не должен валить приложение.
    /// </summary>
    public static McpIncomingHost? TryStart(
        McpConfig config, string appDataDir,
        MeetingTranscriptStore transcripts, IMemoryStore? memory,
        AttachmentStore? attachments, LoggingService? log = null,
        McpIncomingJournal? journal = null,
        Func<IMemoryStore?>? memoryProvider = null)
    {
        if (!config.Enabled || !config.StdioHostEnabled) return null;
        try
        {
            var jobs = new McpJobStore(appDataDir);
            var permissions = new McpPermissionStore(appDataDir);
            journal ??= new McpIncomingJournal(Path.Combine(appDataDir, "logs"), mirror: log);
            var facade = new LocalMcpFacade(
                transcripts, memory, attachments, jobs, config, log, permissions, journal: journal,
                memoryProvider: memoryProvider);
            var host = new McpIncomingHost(
                facade, permissions, () => config.Connections, log, journal: journal);
            host.Start();
            return host;
        }
        catch (Exception ex)
        {
            log?.Error("MCP incoming host failed to start", ex, "Mcp");
            journal?.Error("MCP incoming host failed to start", ex);
            return null;
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        try { _stopCts.Cancel(); }
        catch (ObjectDisposedException) { /* хост уже освобождён через DisposeAsync */ }
        try
        {
            if (_acceptLoop != null)
                await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Warn($"MCP accept loop ended with error: {ex.Message}", "Mcp"); }
        Task[] clients;
        lock (_tasksGate) clients = _clientTasks.ToArray();
        try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { /* клиенты не успели — их таски отменены через CTS */ }
        (_dispatch.Facade as IDisposable)?.Dispose();
        _log?.Info("MCP incoming host stopped", "Mcp");
        _journal?.Info("host stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }

    // ── accept/connection ───────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        while (!_stopCts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, MaxConcurrentClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stopCts.Token).ConfigureAwait(false);
                var p = pipe;
                pipe = null; // владение перешло хэндлеру
                _journal?.Info($"client connected pipe={_pipeName}");
                var t = Task.Run(() => HandleClientAsync(p));
                lock (_tasksGate) _clientTasks.Add(t);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log?.Warn($"MCP pipe accept failed: {ex.Message}", "Mcp");
                // Пайпа могла умереть транзиентно — не выходим, пауза против busy-loop.
                try { await Task.Delay(500, _stopCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false),
                    bufferSize: 8192, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (!_stopCts.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await ReadLineLimitedAsync(reader, _stopCts.Token).ConfigureAwait(false);
                    if (line == null) break;       // EOF — клиент ушёл
                    if (line.Length == 0) continue;
                    var response = await DispatchLineAsync(line, _stopCts.Token).ConfigureAwait(false);
                    if (response == null) continue; // notification без id — не отвечаем
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
                _journal?.Info("client disconnected (eof)");
            }
        }
        catch (IOException ex) { /* клиент оборвал пайп */
            _journal?.Info($"client disconnected (io): {ex.Message}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Warn($"MCP pipe client error: {ex.Message}", "Mcp");
            _journal?.Error("client handler failed", ex);
        }
    }

    /// <summary>Чтение одной строки с потолком длины (защита от раздувания памяти).</summary>
    private static async Task<string?> ReadLineLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
            if (read == 0) return sb.Length == 0 ? null : sb.ToString();
            var nl = Array.IndexOf(buf, '\n', 0, read); // Span в async запрещён
            if (nl >= 0)
            {
                sb.Append(buf, 0, nl);
                var s = sb.ToString();
                return s.EndsWith('\r') ? s[..^1] : s;
            }
            sb.Append(buf, 0, read);
            if (sb.Length > MaxLineBytes)
                throw McpRpcException.InvalidParams("request line too long");
        }
    }

    // ── JSON-RPC dispatch ───────────────────────────────────────────────────

    /// <summary>Ответ на строку запроса (null — notification, отвечать не надо).</summary>
    internal Task<string?> DispatchLineAsync(string line, CancellationToken ct)
        => _dispatch.DispatchLineAsync(line, preResolved: null, ct);

}
