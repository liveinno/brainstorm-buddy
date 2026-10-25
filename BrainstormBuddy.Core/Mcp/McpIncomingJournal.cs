using System.Text;
using System.Text.Json;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Журнал входящего MCP-канала (named pipe + HTTP): {logs}/mcp.log.
/// Журнал вызовов на стороне McpHost — отдельный файл {logs}/mcphost.log
/// (два процесса не пишут в один файл, чтобы не спорить за ротацию).
///
/// Что попадает в журнал: запуск/остановка канала, подключение клиента, авторизация,
/// метод + параметры + ответ + длительность, отмена, таймаут, разрыв и переподключение.
/// Для исключений пишутся тип, сообщение, inner exception и stack trace.
///
/// Чего в журнале НЕТ: токенов и ключей — чувствительные значения маскируются
/// (<c>***</c>), содержимое документов/встреч не логируется (только размеры ответа).
///
/// Ротация: при превышении лимита текущий файл уходит в .1, .2… (до maxFiles),
/// самый старый удаляется. Запись потокобезопасна (один lock на файл).
/// </summary>
public sealed class McpIncomingJournal
{
    public const string DefaultFileName = "mcp.log";
    public const int DefaultMaxFiles = 5;
    public const long DefaultMaxSizeBytes = 2L * 1024 * 1024;

    /// <summary>Имена параметров, значение которых никогда не пишется в журнал.</summary>
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "connectionToken", "token", "apiKey", "api_key", "secret", "clientSecret", "client_secret",
        "password", "passwd", "authorization", "accessToken", "access_token", "refreshToken",
        "refresh_token", "cookie", "apiKeyEncrypted", "bearer",
    };

    private readonly string _logDir;
    private readonly string _filePath;
    private readonly int _maxFiles;
    private readonly long _maxSizeBytes;
    private readonly LoggingService? _mirror;
    private readonly object _gate = new();

    public McpIncomingJournal(
        string logDir,
        string fileName = DefaultFileName,
        int maxFiles = DefaultMaxFiles,
        long maxSizeBytes = DefaultMaxSizeBytes,
        LoggingService? mirror = null)
    {
        _logDir = logDir;
        Directory.CreateDirectory(logDir);
        _filePath = Path.Combine(logDir, fileName);
        _maxFiles = Math.Max(1, maxFiles);
        _maxSizeBytes = Math.Max(64 * 1024L, maxSizeBytes);
        _mirror = mirror;
    }

    /// <summary>Каталог журналов (обычно {appData}/logs).</summary>
    public string LogDirectory => _logDir;

    /// <summary>Путь к текущему файлу журнала.</summary>
    public string FilePath => _filePath;

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Debug(string message) => Write("DEBUG", message, null);

    /// <summary>Ошибка: тип, сообщение, inner exception и stack trace — в одной записи.</summary>
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>
    /// Каталог данных приложения по пути к файлу токена
    /// ({appData}/mcp/tokens/{id}.token → {appData}). null — если путь не похож.
    /// </summary>
    public static string? ResolveDataDirFromTokenFile(string tokenFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(tokenFilePath));
            if (dir == null) return null;
            var mcp = Path.GetDirectoryName(dir);            // .../mcp
            var root = Path.GetDirectoryName(mcp ?? "");     // .../{appData}
            if (root == null) return null;
            // Проверка: путь должен заканчиваться на mcp/tokens
            if (!string.Equals(Path.GetFileName(dir), "tokens", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!string.Equals(Path.GetFileName(mcp ?? ""), "mcp", StringComparison.OrdinalIgnoreCase))
                return null;
            return root;
        }
        catch { return null; }
    }

    // ── запись ────────────────────────────────────────────────────────────

    private void Write(string level, string message, Exception? ex)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] {message}";
        if (ex != null) line += " | " + DescribeException(ex);
        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* журнал не должен ронять MCP-канал */ }
        }
        try
        {
            if (level == "ERROR") _mirror?.Error(message, ex, "Mcp");
            else if (level == "WARN") _mirror?.Warn(message, "Mcp");
            else if (level == "INFO") _mirror?.Info(message, "Mcp");
            else _mirror?.Debug(message, "Mcp");
        }
        catch { /* зеркало (LogWindow) — best effort */ }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_filePath);
            if (!fi.Exists || fi.Length < _maxSizeBytes) return;
            // Сдвиг: .1 → .2 → … → .N (старый .N удаляется)
            var oldest = _filePath + "." + _maxFiles;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = _maxFiles - 1; i >= 1; i--)
            {
                var from = _filePath + "." + i;
                var to = _filePath + "." + (i + 1);
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }
            File.Move(_filePath, _filePath + ".1", overwrite: true);
        }
        catch { /* сбой ротации не должен ронять запись */ }
    }

    // ── маскирование и представление ─────────────────────────────────────

    /// <summary>Маскировать секрет: видны только первые 4 символа.</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        if (secret.Length <= 4) return "***";
        return secret[..4] + "***(" + secret.Length + ")";
    }

    /// <summary>Параметры запроса в компактном виде; чувствительные значения — ***.</summary>
    public static string DescribeParams(JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object) return prms.ValueKind.ToString();
        var sb = new StringBuilder();
        foreach (var p in prms.EnumerateObject())
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p.Name).Append('=');
            if (SensitiveKeys.Contains(p.Name)) sb.Append("***");
            else sb.Append(DescribeValue(p.Value));
        }
        return sb.ToString();
    }

    /// <summary>Значение параметра в компактном виде (без содержимого объектов).</summary>
    public static string DescribeValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => "\"" + Truncate(v.GetString() ?? "") + "\"",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => $"[{v.GetArrayLength()} items]",
        JsonValueKind.Object => "{object}",
        _ => v.ValueKind.ToString(),
    };

    private static string Truncate(string s, int max = 160)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>Тип + сообщение + inner exception + stack trace — одной строкой.</summary>
    public static string DescribeException(Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
        if (ex.InnerException != null)
            sb.Append(" | inner: ").Append(ex.InnerException.GetType().FullName)
              .Append(": ").Append(ex.InnerException.Message);
        var stack = ex.StackTrace;
        if (!string.IsNullOrEmpty(stack))
            sb.Append(" | stack: ").Append(Truncate(stack, 1200));
        return sb.ToString();
    }
}
