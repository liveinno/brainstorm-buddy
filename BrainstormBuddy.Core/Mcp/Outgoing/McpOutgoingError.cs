using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>
/// Класс ошибки исходящего MCP-подключения (авторизация, доступность, таймаут,
/// протокол). Нужен UI и журналу, чтобы показывать владельцу точную причину
/// вместо «подключение не удалось».
/// </summary>
public enum McpOutgoingErrorKind
{
    /// <summary>Причина не определена.</summary>
    Unknown,
    /// <summary>HTTP 401 — токен отклонён сервером.</summary>
    Unauthorized,
    /// <summary>HTTP 403 — доступ запрещён.</summary>
    Forbidden,
    /// <summary>Сервер не ответил за отведённое время (или запрос отменён по таймауту).</summary>
    Timeout,
    /// <summary>Сервер недоступен: нет соединения, отказ, DNS/сокет-ошибка.</summary>
    Unreachable,
    /// <summary>Ответ сервера получен, но это не ожидаемый MCP-протокол/статус.</summary>
    Protocol,
    /// <summary>Ошибка конкретного вызова инструмента (tools/call вернул isError).</summary>
    ToolError,
}

/// <summary>
/// Разобранная причина ошибки исходящего MCP: вид, короткий заголовок для UI,
/// детали (сообщение исключения без секретов) и HTTP-статус, если он был.
/// </summary>
public sealed record McpOutgoingErrorInfo(
    McpOutgoingErrorKind Kind,
    string Title,
    string Detail,
    int? HttpStatus)
{
    /// <summary>Проблема авторизации (401/403) — UI предлагает проверить токен.</summary>
    public bool IsAuthProblem => Kind is McpOutgoingErrorKind.Unauthorized or McpOutgoingErrorKind.Forbidden;

    /// <summary>
    /// Определить причину по исключению: обход цепочки ex + InnerException.
    /// HttpRequestException со статусом 401 → Unauthorized, 403 → Forbidden,
    /// прочие статусы → Protocol; TaskCanceledException/OperationCanceledException →
    /// Timeout; HttpRequestException без статуса и SocketException → Unreachable;
    /// всё остальное → Unknown.
    /// </summary>
    /// <param name="ex">Исключение (может быть null — тогда Unknown).</param>
    /// <param name="timeoutSeconds">Таймаут подключения/вызова, для текста ошибки.</param>
    public static McpOutgoingErrorInfo Classify(Exception? ex, int timeoutSeconds = 15)
    {
        if (ex == null)
            return new McpOutgoingErrorInfo(McpOutgoingErrorKind.Unknown, "Неизвестная ошибка", "", null);

        var chain = Chain(ex);

        // 1) Ответ сервера со статусом — самая точная причина.
        foreach (var e in chain)
        {
            if (e is not HttpRequestException hre || !hre.StatusCode.HasValue) continue;
            var code = (int)hre.StatusCode.Value;
            return code switch
            {
                401 => new McpOutgoingErrorInfo(
                    McpOutgoingErrorKind.Unauthorized, "Токен отклонён сервером (401)", Sanitize(ex.Message), code),
                403 => new McpOutgoingErrorInfo(
                    McpOutgoingErrorKind.Forbidden, "Доступ запрещён (403)", Sanitize(ex.Message), code),
                _ => new McpOutgoingErrorInfo(
                    McpOutgoingErrorKind.Protocol, $"Ошибка протокола MCP (HTTP {code})", Sanitize(ex.Message), code),
            };
        }

        // 2) Таймаут/отмена.
        foreach (var e in chain)
        {
            if (e is not (TaskCanceledException or OperationCanceledException)) continue;
            var detail = Sanitize(ex.Message);
            if (string.IsNullOrEmpty(detail))
                detail = $"Превышен таймаут {timeoutSeconds} с";
            return new McpOutgoingErrorInfo(
                McpOutgoingErrorKind.Timeout, "Сервер не ответил вовремя", detail, null);
        }

        // 3) Сеть: соединение не установлено.
        foreach (var e in chain)
        {
            if (e is not (HttpRequestException or SocketException)) continue;
            return new McpOutgoingErrorInfo(
                McpOutgoingErrorKind.Unreachable, "Сервер недоступен", Sanitize(ex.Message), null);
        }

        return new McpOutgoingErrorInfo(
            McpOutgoingErrorKind.Unknown, "Неизвестная ошибка", Sanitize(ex.Message), null);
    }

    /// <summary>Отклонил ли сервер токен (HTTP 401) — true только при Unauthorized.</summary>
    public static bool IsUnauthorized(Exception? ex) => Classify(ex).Kind == McpOutgoingErrorKind.Unauthorized;

    /// <summary>Цепочка исключения и всех вложенных (ограничена по длине).</summary>
    private static List<Exception> Chain(Exception ex)
    {
        var list = new List<Exception>();
        var current = ex;
        while (current != null && list.Count < 16)
        {
            list.Add(current);
            current = current.InnerException;
        }
        return list;
    }

    /// <summary>
    /// Текст исключения для показа владельцу: без переносов строк, без значений
    /// заголовков (Authorization/Bearer) и с обрезкой по длине.
    /// </summary>
    private static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var s = message.Replace('\r', ' ').Replace('\n', ' ');
        // Порядок важен: сначала «Bearer <значение>», затем «Authorization: <значение>»
        // (иначе маскирование заголовка оставляло бы сам токен в тексте).
        s = Regex.Replace(s, @"(?i)(bearer\s+)\S+", "$1***");
        s = Regex.Replace(s, @"(?i)(authorization\s*[:=]\s*)\S+", "$1***");
        s = Regex.Replace(
            s,
            @"(?i)((?:access_token|refresh_token|api[_-]?key|client_secret|password|secret|token)\s*[:=]\s*)(\S+)",
            "$1***");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        if (s.Length > 400) s = s[..400] + "…";
        return s;
    }
}

/// <summary>
/// Фасад классификации ошибок исходящего MCP: короткая точка входа
/// <c>McpOutgoingError.Classify(ex)</c> для UI и зонда подключения.
/// Вся логика — в <see cref="McpOutgoingErrorInfo"/>.
/// </summary>
public static class McpOutgoingError
{
    /// <summary>Разобрать причину ошибки подключения/вызова (см. McpOutgoingErrorInfo.Classify).</summary>
    public static McpOutgoingErrorInfo Classify(Exception? ex, int timeoutSeconds = 15)
        => McpOutgoingErrorInfo.Classify(ex, timeoutSeconds);

    /// <summary>Отклонил ли сервер токен (HTTP 401).</summary>
    public static bool IsUnauthorized(Exception? ex) => McpOutgoingErrorInfo.IsUnauthorized(ex);
}
