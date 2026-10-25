namespace BrainstormBuddy.Core.Mcp;

/// <summary>Коды ошибок входящего MCP JSON-RPC канала (named pipe).</summary>
public static class McpRpcError
{
    public const int Unauthorized = -32001; // токен неизвестен / отозван / подключение приостановлено
    public const int ScopeDenied = -32002;  // запрос выходит за scope/права подключения
    public const int Paused = -32003;       // глобальная пауза MCP/LLM
    public const int InvalidParams = -32602;
    public const int MethodNotFound = -32601;
    public const int ParseError = -32700;
    public const int InternalError = -32603;
}

/// <summary>
/// Ошибка с JSON-RPC кодом. McpIncomingHost ловит её и отдаёт клиенту код+message,
/// LocalMcpFacade бросает для scope-проверок вместо проглатывания данных.
/// </summary>
public sealed class McpRpcException : Exception
{
    public McpRpcException(int code, string message) : base(message) => Code = code;
    public int Code { get; }

    public static McpRpcException Unauthorized(string msg = "unauthorized") => new(McpRpcError.Unauthorized, msg);
    public static McpRpcException ScopeDenied(string msg = "scope denied") => new(McpRpcError.ScopeDenied, msg);
    public static McpRpcException Paused(string msg = "paused") => new(McpRpcError.Paused, msg);
    public static McpRpcException InvalidParams(string msg) => new(McpRpcError.InvalidParams, msg);
}
