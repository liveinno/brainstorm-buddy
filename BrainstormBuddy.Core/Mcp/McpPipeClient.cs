using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Клиент named-pipe JSON-RPC канала Buddy. Используется BrainstormBuddy.McpHost.exe
/// (stdio→pipe бридж) и юнит-тестами (loopback к McpIncomingHost).
/// Один вызов = одна строка запроса → одна строка ответа; вызовы сериализуются
/// семафором (pipe не мультиплексирует concurrent requests — id всё равно 1..N
/// по порядку и достаточно).
/// </summary>
public sealed class McpPipeClient : IAsyncDisposable
{
    public sealed class RpcException : Exception
    {
        public RpcException(int code, string message, string? requestId = null)
            : base(requestId == null ? message : $"{message} (requestId={requestId})")
        {
            Code = code;
            RequestId = requestId;
        }
        public int Code { get; }

        /// <summary>ID запроса из error.data — ключ для поиска в журнале MCP.</summary>
        public string? RequestId { get; }
    }

    private readonly string _pipeName;
    private readonly string? _tokenFilePath;
    private string _token;
    private readonly TimeSpan _connectTimeout;
    private readonly SemaphoreSlim _io = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _nextId;

    public McpPipeClient(
        string pipeName, string token, TimeSpan? connectTimeout = null, string? tokenFilePath = null)
    {
        _pipeName = pipeName;
        _token = token;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
        _tokenFilePath = tokenFilePath;
    }

    /// <summary>
    /// Клиент, читающий токен из файла. Токен перечитывается при каждом вызове:
    /// мастер выдаёт новый токен (переподключение после перезапуска приложения)
    /// без пересоздания клиента.
    /// </summary>
    public static McpPipeClient FromTokenFile(string tokenFilePath, string? pipeName = null)
    {
        var token = File.ReadAllText(tokenFilePath).Trim();
        return new McpPipeClient(
            pipeName ?? McpPipeNaming.GetDefaultPipeName(), token, tokenFilePath: tokenFilePath);
    }

    /// <summary>Текущий токен (актуален после <see cref="RefreshToken"/>).</summary>
    public string Token => _token;

    /// <summary>Перечитать токен из файла, если он изменился. true — токен обновлён.</summary>
    public bool RefreshToken()
    {
        if (_tokenFilePath == null) return false;
        try
        {
            // Сверяем содержимое, а не (mtime, размер): быстрые перезаписи файла
            // не меняют mtime, а длина токена постоянна — по отпечатку новый
            // токен после перевыпуска остался бы незамеченным.
            var text = File.ReadAllText(_tokenFilePath).Trim();
            if (text.Length == 0) return false;
            if (text == _token) return false;
            _token = text;
            return true;
        }
        catch { return false; }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true }) return;
        DisposeTransport();
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_connectTimeout);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Сервер не слушает (или не успел подняться) — это не отмена пользователем.
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"MCP pipe '{_pipeName}' не отвечает (таймаут подключения {_connectTimeout.TotalSeconds:0.#} с)");
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true)
            { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>Вызов facade-метода с подстановкой connectionToken в params.</summary>
    public async Task<JsonElement> CallFacadeAsync(
        string method, Dictionary<string, object?>? extraParams, CancellationToken ct)
    {
        var prms = new Dictionary<string, object?>(extraParams ?? []);
        // Токен из файла мог быть перевыдан мастером — обновляем перед вызовом.
        RefreshToken();
        // Явно переданный токен перекрывает файловый (multi-connection сценарий).
        prms.TryAdd("connectionToken", _token);
        return await CallAsync("facade." + method, prms, ct).ConfigureAwait(false);
    }

    /// <summary>Низкоуровневый JSON-RPC вызов. RpcException с серверным кодом на error-ответ.</summary>
    public async Task<JsonElement> CallAsync(
        string method, Dictionary<string, object?>? prms, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(ct).ConfigureAwait(false);
                    var id = Interlocked.Increment(ref _nextId);
                    var req = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id,
                        method,
                        @params = prms ?? new Dictionary<string, object?>(),
                    });
                    await _writer!.WriteLineAsync(req.AsMemory(), ct).ConfigureAwait(false);
                    var line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null) throw new IOException("pipe closed by server");
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                    {
                        var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : McpRpcError.InternalError;
                        var msg = err.TryGetProperty("message", out var m) ? m.GetString() ?? "rpc error" : "rpc error";
                        // request ID из error.data — по нему запись находится в журнале MCP.
                        string? rid = null;
                        if (err.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                            && data.TryGetProperty("requestId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String)
                            rid = ridEl.GetString();
                        throw new RpcException(code, msg, rid);
                    }
                    if (root.TryGetProperty("result", out var result))
                        return result.Clone();
                    throw new IOException("malformed rpc response");
                }
                catch (IOException) when (attempt == 0)
                {
                    // Канал оборвался (сервер перезапустился) — переподключаемся
                    // и повторяем вызов ровно один раз.
                    DisposeTransport();
                    RefreshToken();
                    continue;
                }
                catch (IOException)
                {
                    DisposeTransport(); // следующий вызов переподключится
                    throw;
                }
            }
        }
        finally
        {
            _io.Release();
        }
    }

    private void DisposeTransport()
    {
        // Канал мог быть оборван сервером: закрытие writer'а шлёт flush в
        // мёртвый pipe. Это не должно ронять клиента (особенно при Dispose
        // после перезапуска сервера) — ошибки закрытия игнорируем.
        try { _reader?.Dispose(); } catch (Exception) { /* канал уже мёртв */ }
        try { _writer?.Dispose(); } catch (Exception) { /* pipe broken */ }
        try { _pipe?.Dispose(); } catch (Exception) { /* pipe broken */ }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public ValueTask DisposeAsync()
    {
        DisposeTransport();
        _io.Dispose();
        return ValueTask.CompletedTask;
    }
}
