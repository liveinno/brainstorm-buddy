using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>
/// Append-only журнал исходящих MCP-вызовов: {appData}/mcp/journal.jsonl, ротация —
/// записи старше 30 дней выкидываются при старте (файл переписывается один раз).
///
/// Легаси-Append пишет только ключи аргументов и размеры (без значений).
/// Расширенный AppendEvent пишет подробности: сервер, транспорт, URL без
/// секретных параметров, способ авторизации, протокол, список инструментов,
/// аргументы и результат (обрезанные). ВСЕ значения проходят через Redact:
/// Authorization, токены и содержимое защищённого хранилища в журнал не попадают.
/// Крупные результаты выгружаются в {appData}/mcp/diagnostics/*.txt, а в журнале
/// остаётся превью с отметкой Truncated и путём к файлу.
/// </summary>
public sealed class McpJournal
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>Предел длины текста, попадающего в одну запись журнала.</summary>
    private const int MaxInlineChars = 4000;

    private static readonly Regex BearerRx = new(
        @"\bBearer\s+[^\s"",;}\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonSecretRx = new(
        "\"(?<k>authorization|token|api[_-]?key|apikey|client[_-]?secret|secret|access[_-]?token|refresh[_-]?token|auth)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KeyValueSecretRx = new(
        @"\b(?<k>authorization|token|api[_-]?key|apikey|client[_-]?secret|secret|access[_-]?token|refresh[_-]?token|auth)\b(?<sep>\s*[:=]\s*)(?<v>""[^""]*""|'[^']*'|[^\s,;}\]]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OcKeyRx = new(
        @"\boc_sk_[A-Za-z0-9_\-]{6,}", RegexOptions.Compiled);
    private static readonly Regex JwtRx = new(
        @"\beyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{4,}(?:\.[A-Za-z0-9_\-]*)?", RegexOptions.Compiled);
    private static readonly Regex SkRx = new(
        @"\bsk-[A-Za-z0-9_\-]{12,}", RegexOptions.Compiled);

    private readonly string _filePath;
    private readonly string _diagDir;
    private readonly object _gate = new();
    private bool _rotated;
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _requestId = new();

    public McpJournal(string appDataDir)
    {
        var dir = Path.Combine(appDataDir, "mcp");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "journal.jsonl");
        _diagDir = Path.Combine(dir, "diagnostics");
    }

    /// <summary>Прямой конструктор на файл — для тестов и кастомных путей.</summary>
    public static McpJournal ForFile(string filePath) => new(filePath, forFile: true);

    private McpJournal(string filePath, bool forFile)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _diagDir = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "diagnostics");
    }

    /// <summary>Путь к файлу журнала ({appData}/mcp/journal.jsonl).</summary>
    public string FilePath => _filePath;

    /// <summary>Каталог диагностических выгрузок ({appData}/mcp/diagnostics).</summary>
    public string DiagnosticsDir => _diagDir;

    /// <summary>
    /// Сквозной идентификатор текущего запроса (ambient, AsyncLocal). Заполняется
    /// оркестратором на время запроса и автоматически подставляется в новые записи.
    /// </summary>
    public string? CurrentRequestId
    {
        get => _requestId.Value;
        set => _requestId.Value = value;
    }

    /// <summary>Зарегистрированные секреты (копия) — для маскирования в Redact.</summary>
    public IReadOnlyList<string> RegisteredSecrets
    {
        get { lock (_gate) return _secrets.ToArray(); }
    }

    /// <summary>
    /// Сериализованная запись журнала (jsonl-строка). Позиционные поля — легаси
    /// (тесты конструируют Entry напрямую), расширенные — init-свойства.
    /// </summary>
    public sealed record Entry(
        string Time,
        string Direction,
        string? JobId,

        string? Connection,
        string? Tool,
        string? State,
        long? DurationMs,
        string? ErrorCode,
        IReadOnlyList<string>? ArgKeys,
        int? ArgsBytes,
        int? ResultBytes)
    {
        /// <summary>Сквозной requestId запроса (пусто — вне запроса).</summary>
        public string? RequestId { get; init; }
        /// <summary>Фаза: connect|probe|initialize|tools/list|tools/call|approval|chat|…</summary>
        public string? Phase { get; init; }
        /// <summary>Имя сервера MCP.</summary>
        public string? Server { get; init; }
        /// <summary>stdio|http.</summary>
        public string? Transport { get; init; }
        /// <summary>URL без секретных query-параметров.</summary>
        public string? Url { get; init; }
        /// <summary>Способ авторизации: none|bearer|env.</summary>
        public string? AuthKind { get; init; }
        /// <summary>Есть ли секрет (сам секрет не пишется).</summary>
        public bool? HasSecret { get; init; }
        /// <summary>Согласованная версия протокола MCP.</summary>
        public string? ProtocolVersion { get; init; }
        /// <summary>Число инструментов (tools/list).</summary>
        public int? ToolCount { get; init; }
        /// <summary>Имена инструментов (tools/list, tools/offered).</summary>
        public IReadOnlyList<string>? Tools { get; init; }
        /// <summary>Аргументы вызова (после Redact, обрезанные).</summary>
        public string? ArgsJson { get; init; }
        /// <summary>Превью результата вызова (после Redact, обрезанное).</summary>
        public string? ResultPreview { get; init; }
        /// <summary>Текст в записи обрезан; полный результат — в ResultFile.</summary>
        public bool? Truncated { get; init; }
        /// <summary>Путь к файлу с полным результатом.</summary>
        public string? ResultFile { get; init; }
        /// <summary>HTTP-статус ответа сервера.</summary>
        public int? HttpStatus { get; init; }
        /// <summary>
        /// Классифицированная причина сбоя: Unauthorized|Forbidden|Timeout|
        /// Unreachable|Protocol|ToolError|Unknown. Позволяет отличить таймаут
        /// от отказа по авторизации без разбора текста исключения.
        /// </summary>
        public string? ErrorKind { get; init; }
        /// <summary>true — ошибка/отказ.</summary>
        public bool? IsError { get; init; }
        /// <summary>Детали (после Redact).</summary>
        public string? Detail { get; init; }
        /// <summary>Стек исключения (после Redact).</summary>
        public string? Stack { get; init; }
        /// <summary>Состояние подтверждения: requested|approved|denied|cancelled.</summary>
        public string? Approval { get; init; }
    }

    /// <summary>
    /// Запись события. argsJson → только список ключей верхнего уровня + размер;
    /// result — только размер. Никакой личной информации в журнал не попадает.
    /// </summary>
    public void Append(string direction, string? jobId, string? connection, string? tool,
        string? state, long? durationMs, string? errorCode,
        string? argsJson = null, int? resultBytes = null, string? detail = null)
    {
        var keys = ArgKeyList(argsJson);
        var entry = new Entry(
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            direction, jobId, connection, tool, state, durationMs, errorCode,
            keys, argsJson?.Length, resultBytes)
        {
            Phase = direction,
            RequestId = CurrentRequestId,
            Detail = detail == null ? null : Redact(detail, _secrets.ToArray()),
        };
        Write(entry);
    }

    /// <summary>
    /// Расширенная запись события (все поля опциональны). ArgsJson/ResultPreview/
    /// Detail/Stack прогоняются через Redact — секреты не пишутся НИКОГДА.
    /// Длинные тексты обрезаются, Truncated=true.
    /// </summary>
    public void AppendEvent(
        string phase,
        string? server = null,
        string? tool = null,
        string? transport = null,
        string? url = null,
        string? authKind = null,
        bool? hasSecret = null,
        string? protocolVersion = null,
        int? toolCount = null,
        IReadOnlyList<string>? tools = null,
        string? argsJson = null,
        string? resultPreview = null,
        bool? truncated = null,
        string? resultFile = null,
        int? httpStatus = null,
        string? errorKind = null,
        bool? isError = null,
        string? detail = null,
        string? stack = null,
        string? approval = null,
        long? durationMs = null,
        string? jobId = null,
        string? state = null,
        string? requestId = null)
    {
        var secrets = _secrets.ToArray();
        var args = argsJson == null ? null : Redact(argsJson, secrets);
        var preview = resultPreview == null ? null : Redact(resultPreview, secrets);
        var det = detail == null ? null : Redact(detail, secrets);
        var st = stack == null ? null : Redact(stack, secrets);

        var cut = truncated ?? false;
        if (args != null && args.Length > MaxInlineChars)
        {
            args = args[..MaxInlineChars];
            cut = true;
        }
        if (preview != null && preview.Length > MaxInlineChars)
        {
            preview = preview[..MaxInlineChars];
            cut = true;
        }

        var entry = new Entry(
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            phase, jobId, server, tool, state, durationMs, null,
            ArgKeyList(argsJson), argsJson?.Length, null)
        {
            RequestId = requestId ?? CurrentRequestId,
            Phase = phase,
            Server = server,
            Transport = transport,
            Url = url,
            AuthKind = authKind,
            HasSecret = hasSecret,
            ProtocolVersion = protocolVersion,
            ToolCount = toolCount,
            Tools = tools,
            ArgsJson = args,
            ResultPreview = preview,
            Truncated = cut ? true : truncated,
            ResultFile = resultFile,
            HttpStatus = httpStatus,
            ErrorKind = errorKind,
            IsError = isError,
            Detail = det,
            Stack = st,
            Approval = approval,
        };
        Write(entry);
    }

    /// <summary>
    /// Запомнить секрет для маскирования во всех последующих записях.
    /// Значение не пишется в журнал — только используется как образец для замены.
    /// </summary>
    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        var value = secret.Trim();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            value = value[7..].Trim();
        if (value.Length < 4) return;
        lock (_gate) { _secrets.Add(value); }
    }

    /// <summary>
    /// Маскирование секретов: точные значения (extraSecrets), «Bearer &lt;токен&gt;»,
    /// JSON-виды authorization/token/api_key/client_secret/secret, значения вида
    /// oc_sk_… и JWT (eyJ…). Возвращает очищенный текст.
    /// </summary>
    public static string Redact(string? text, IEnumerable<string?>? extraSecrets = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;
        if (extraSecrets != null)
        {
            foreach (var secret in extraSecrets)
            {
                if (string.IsNullOrWhiteSpace(secret)) continue;
                var value = secret.Trim();
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    value = value[7..].Trim();
                if (value.Length < 4) continue;
                s = s.Replace(value, "***", StringComparison.Ordinal);
            }
        }
        s = BearerRx.Replace(s, "Bearer ***");
        s = JsonSecretRx.Replace(s, m => $"\"{m.Groups["k"].Value}\":\"***\"");
        s = KeyValueSecretRx.Replace(s, MaskKeyValue);
        s = OcKeyRx.Replace(s, "oc_sk_***");
        s = JwtRx.Replace(s, "***");
        s = SkRx.Replace(s, "***");
        return s;
    }

    private static string MaskKeyValue(Match m)
    {
        var value = m.Groups["v"].Value;
        string masked;
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') masked = "\"***\"";
        else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') masked = "'***'";
        else masked = "***";
        return m.Groups["k"].Value + m.Groups["sep"].Value + masked;
    }

    /// <summary>
    /// Выгрузить полный результат в отдельный диагностический файл
    /// ({appData}/mcp/diagnostics/*.txt) и вернуть путь. Содержимое проходит Redact.
    /// </summary>
    public string? SpillResult(string serverName, string toolName, string content)
    {
        try
        {
            Directory.CreateDirectory(_diagDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var name = $"{stamp}_{Safe(serverName)}_{Safe(toolName)}_{Guid.NewGuid().ToString("N")[..6]}.txt";
            var path = Path.Combine(_diagDir, name);
            var text = Redact(content, _secrets.ToArray());
            File.WriteAllText(path, text);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Safe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var chars = raw.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray();
        var s = new string(chars);
        return s.Length > 40 ? s[..40] : s;
    }

    /// <summary>Ключи верхнего уровня args-объекта (значения НЕ логируются).</summary>
    public static IReadOnlyList<string>? ArgKeyList(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            var node = JsonNode.Parse(argsJson) as JsonObject;
            if (node == null) return null;
            var keys = new List<string>();
            foreach (var kv in node) keys.Add(kv.Key);
            return keys;
        }
        catch (JsonException) { return null; }
    }

    private void Write(Entry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOpts) + "\n";
        lock (_gate)
        {
            RotateOnce();
            File.AppendAllText(_filePath, line);
        }
    }

    /// <summary>Одноразовая (на процесс) чистка записей старше 30 дней.</summary>
    private void RotateOnce()
    {
        if (_rotated) return;
        _rotated = true;
        if (!File.Exists(_filePath)) return;
        try
        {
            var cutoff = DateTime.UtcNow - Retention;
            var keep = new List<string>();
            foreach (var line in File.ReadLines(_filePath))
            {
                var t = TimestampOf(line);
                if (t == null || t.Value >= cutoff) keep.Add(line);
            }
            var tmp = _filePath + ".tmp";
            File.WriteAllLines(tmp, keep);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (IOException) { /* журнал не должен валить приложение */ }
        catch (UnauthorizedAccessException) { }
    }

    private static DateTime? TimestampOf(string line)
    {
        try
        {
            var t = JsonNode.Parse(line)?["time"]?.GetValue<string>();
            return t != null && DateTime.TryParse(t, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var d) ? d : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Все строки (для тестов/диагностики).</summary>
    public IReadOnlyList<string> ReadAll()
        => File.Exists(_filePath) ? File.ReadAllLines(_filePath) : Array.Empty<string>();
}
