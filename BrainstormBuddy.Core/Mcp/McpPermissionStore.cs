using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Хранилище разрешений входящих MCP-подключений: %AppData%\BrainstormBuddy\mcp\permissions.json.
/// Токен подключения — 32 случайных байта (base64url); в файле лежат только
/// SHA-256 хэш токена (для Resolve) и DPAPI-блоб токена (для повторного экспорта
/// визардом без перевыпуска). Отдельных файлов секретов нет — DPAPI CurrentUser
/// привязывает блоб к учётке, а файл живёт в пользовательском профиле.
/// Секция "secrets" хранит секреты исходящих MCP-подключений (Bearer-токен и т.п.)
/// в виде DPAPI-блобов: в config.json лежит только ссылка (SecretRef), само
/// значение — исключительно здесь, под DPAPI CurrentUser.
/// Потокобезопасно: все операции под lock, запись — атомарно через AtomicFileWriter.
/// </summary>
public sealed class McpPermissionStore
{
    private const int TokenBytes = 32;

    /// <summary>Грант входящего подключения: хэш токена + DPAPI-блоб для повторного экспорта.</summary>
    public sealed class TokenGrant
    {
        [JsonPropertyName("connectionId")] public string ConnectionId { get; set; } = "";
        [JsonPropertyName("tokenHash")] public string TokenHash { get; set; } = "";
        [JsonPropertyName("protectedToken")] public string ProtectedToken { get; set; } = "";
        [JsonPropertyName("issuedUtc")] public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Секрет в защищённом виде: значение хранится только как DPAPI-блоб
    /// (base64), открытый текст в permissions.json не попадает никогда.
    /// </summary>
    private sealed class SecretRecord
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("protectedValue")] public string ProtectedValue { get; set; } = "";
        [JsonPropertyName("updatedUtc")] public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class StoreFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("grants")] public List<TokenGrant> Grants { get; set; } = new();
        /// <summary>Секреты исходящих подключений (DPAPI-блобы). В старых файлах раздела нет — это норма.</summary>
        [JsonPropertyName("secrets")] public List<SecretRecord> Secrets { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _mcpDir;
    private StoreFile _file;
    /// <summary>
    /// Последнее прочитанное содержимое permissions.json — по нему решаем,
    /// что файл изменился. Отпечаток (mtime+размер) не годится: две быстрые
    /// перезаписи дают одну и ту же пару (83% замеров на этой машине), а
    /// размер файла при перевыпуске токена не меняется — грант остался бы
    /// невидимым до перезапуска MCP.
    /// </summary>
    private string _raw = "";

    /// <summary>
    /// Открыть хранилище прав и секретов; существующий permissions.json читается
    /// сразу, файл не создаётся до первой записи.
    /// </summary>
    /// <param name="appDataDir">%AppData%\BrainstormBuddy (корень данных приложения).</param>
    public McpPermissionStore(string appDataDir)
    {
        _mcpDir = Path.Combine(appDataDir, "mcp");
        _filePath = Path.Combine(_mcpDir, "permissions.json");
        _raw = ReadRaw(_filePath) ?? "";
        _file = Parse(_raw) ?? new StoreFile();
    }

    /// <summary>Путь к файлу прав и секретов (mcp/permissions.json).</summary>
    public string FilePath => _filePath;

    /// <summary>Каталог MCP (%AppData%\BrainstormBuddy\mcp).</summary>
    public string McpDirectory => _mcpDir;

    /// <summary>
    /// Выпустить новый токен для подключения: случайные 32 байта, base64url.
    /// Старый grant того же connectionId заменяется (перевыпуск = ревокация
    /// предыдущего токена). Токен пишется в mcp/tokens/{connectionId}.token —
    /// путь передаётся McpHost.exe как argv[0].
    /// </summary>
    public string Issue(McpConnectionEntry entry)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(TokenBytes);
        var token = Base64UrlEncode(tokenBytes);
        var grant = new TokenGrant
        {
            ConnectionId = entry.Id,
            TokenHash = HashToken(token),
            ProtectedToken = Protect(tokenBytes),
            IssuedUtc = DateTime.UtcNow,
        };
        lock (_gate)
        {
            ReloadLocked();
            _file.Grants.RemoveAll(g => g.ConnectionId == entry.Id);
            _file.Grants.Add(grant);
            SaveLocked();
        }
        WriteTokenFile(entry.Id, token);
        return token;
    }

    /// <summary>
    /// Токен → resolved connection или null (неизвестен/отозван).
    /// Suspended соединение возвращается как есть — решение -32001 на стороне
    /// сервера (нужно для auth.resolve, чтобы отличать «нет такого» от «приостановлено»).
    /// </summary>
    public McpResolvedConnection? Resolve(string token, IReadOnlyList<McpConnectionEntry> connections)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token);
        TokenGrant? grant;
        lock (_gate)
        {
            ReloadLocked();
            grant = _file.Grants.FirstOrDefault(g =>
                string.Equals(g.TokenHash, hash, StringComparison.Ordinal));
        }
        if (grant == null) return null;
        var entry = connections.FirstOrDefault(c => c.Id == grant.ConnectionId);
        return entry == null ? null : McpConnectionMapping.ToResolved(entry);
    }

    /// <summary>Отозвать токен подключения (запись подключения в конфиге при этом остаётся).</summary>
    public bool Revoke(string connectionId)
    {
        lock (_gate)
        {
            ReloadLocked();
            var removed = _file.Grants.RemoveAll(g => g.ConnectionId == connectionId);
            if (removed > 0) SaveLocked();
            TryDeleteTokenFile(connectionId);
            return removed > 0;
        }
    }

    /// <summary>
    /// Расшифровать ранее выпущенный токен (визард показывает конфиг снова,
    /// не заставляя пользователя перевыпускать). null — гранта нет или DPAPI
    /// не смог расшифровать (чужой профиль/перенос файла).
    /// </summary>
    public string? TryRevealToken(string connectionId)
    {
        lock (_gate)
        {
            ReloadLocked();
            var grant = _file.Grants.FirstOrDefault(g => g.ConnectionId == connectionId);
            if (grant == null) return null;
            try
            {
                var bytes = Unprotect(grant.ProtectedToken);
                return bytes == null ? null : Base64UrlEncode(bytes);
            }
            catch { return null; }
        }
    }

    /// <summary>Путь к файлу токена, который McpHost читает при старте (argv[0]).</summary>
    public string TokenFilePath(string connectionId)
        => Path.Combine(_mcpDir, "tokens", SanitizeFileName(connectionId) + ".token");

    /// <summary>Синхронизация с конфигом: ревокация грантов для удалённых из конфига подключений.</summary>
    public int Prune(IReadOnlyList<McpConnectionEntry> connections)
    {
        var alive = new HashSet<string>(connections.Select(c => c.Id), StringComparer.Ordinal);
        lock (_gate)
        {
            ReloadLocked();
            var stale = _file.Grants.Where(g => !alive.Contains(g.ConnectionId))
                                    .Select(g => g.ConnectionId).ToList();
            if (stale.Count == 0) return 0;
            _file.Grants.RemoveAll(g => stale.Contains(g.ConnectionId));
            SaveLocked();
            foreach (var id in stale) TryDeleteTokenFile(id);
            return stale.Count;
        }
    }

    /// <summary>Жив ли грант для подключения (для статуса «Доступ отозван» без знания токена).</summary>
    public bool HasGrant(string connectionId)
    {
        lock (_gate)
        {
            ReloadLocked();
            return _file.Grants.Any(g => string.Equals(g.ConnectionId, connectionId, StringComparison.Ordinal));
        }
    }

    /// <summary>SHA-256 токена (hex) — формат сравнения общий для pipe- и HTTP-токенов.</summary>
    public static string HashTokenString(string token) => HashToken(token);

    /// <summary>Число грантов входящих подключений (без раскрытия токенов).</summary>
    public int GrantCount { get { lock (_gate) { ReloadLocked(); return _file.Grants.Count; } } }

    // ── секреты исходящих подключений (DPAPI) ──────────────────────────────

    /// <summary>
    /// Сохранить секрет (Bearer-токен внешнего MCP-сервера и т.п.) под
    /// идентификатором <paramref name="id"/> — обычно это SecretRef из config.json.
    /// В permissions.json попадает только DPAPI-блоб (base64), открытый текст
    /// секрета в файле не сохраняется. Повторный вызов с тем же id заменяет
    /// значение. Пустое значение секрета — no-op: прежний секрет остаётся
    /// (удаление — отдельным вызовом <see cref="RemoveSecret"/>).
    /// </summary>
    public void StoreSecret(string id, string secret)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (string.IsNullOrEmpty(secret)) return; // пустое значение — прежний секрет сохраняется
        var blob = Protect(Encoding.UTF8.GetBytes(secret));
        lock (_gate)
        {
            ReloadLocked();
            _file.Secrets.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            _file.Secrets.Add(new SecretRecord
            {
                Id = id,
                ProtectedValue = blob,
                UpdatedUtc = DateTime.UtcNow,
            });
            SaveLocked();
        }
    }

    /// <summary>
    /// Расшифровать сохранённый секрет. null — секрета нет, id пустой или
    /// DPAPI не смог расшифровать блоб (чужой профиль/перенос файла).
    /// </summary>
    public string? TryRevealSecret(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            ReloadLocked();
            var record = _file.Secrets.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (record == null) return null;
            try
            {
                var bytes = Unprotect(record.ProtectedValue);
                return bytes == null ? null : Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }

    /// <summary>Есть ли сохранённый секрет под указанным id (значение не раскрывается).</summary>
    public bool HasSecret(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            ReloadLocked();
            return _file.Secrets.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>Удалить сохранённый секрет (значение больше недоступно).</summary>
    public void RemoveSecret(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            ReloadLocked();
            if (_file.Secrets.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal)) > 0)
                SaveLocked();
        }
    }

    /// <summary>
    /// Синхронизация секретов с конфигом: удалить секреты, на которые больше нет
    /// ссылки в списке живых id. Возвращает число удалённых записей.
    /// </summary>
    public int PruneSecrets(IReadOnlyCollection<string> aliveIds)
    {
        var alive = aliveIds == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(aliveIds, StringComparer.Ordinal);
        lock (_gate)
        {
            ReloadLocked();
            var stale = _file.Secrets.Where(s => !alive.Contains(s.Id)).Select(s => s.Id).ToList();
            if (stale.Count == 0) return 0;
            _file.Secrets.RemoveAll(s => stale.Contains(s.Id));
            SaveLocked();
            return stale.Count;
        }
    }

    // ── внутренности ────────────────────────────────────────────────────────

    private static StoreFile? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new StoreFile();
        try
        {
            var f = JsonSerializer.Deserialize<StoreFile>(text);
            if (f == null || f.Grants == null) return new StoreFile();
            f.Secrets ??= new List<SecretRecord>(); // "secrets": null в старом/правленом вручную файле
            return f;
        }
        catch { return null; } // битый JSON — состояние не меняем
    }

    /// <summary>Прочитать файл прав; null — читать не удалось, "" — файла нет.</summary>
    private static string? ReadRaw(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    private void SaveLocked()
    {
        var text = JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true });
        AtomicFileWriter.WriteText(_filePath, text);
        _raw = text;
    }

    /// <summary>
    /// Перечитать permissions.json, если файл изменился с прошлой загрузки.
    /// Нужно, потому что экземпляров стора в процессе несколько (приложение,
    /// pipe-хост, мастер подключения): выданный в одном экземпляре токен
    /// обязан стать виден остальным без перезапуска MCP.
    /// Сравниваем содержимое, а не (mtime, размер): быстрые перезаписи файла
    /// не меняют mtime, а перевыпуск токена не меняет размер — по отпечатку
    /// новый грант остался бы невидимым.
    /// Вызывается под _gate.
    /// </summary>
    private void ReloadLocked()
    {
        var text = ReadRaw(_filePath);
        if (text == null) return; // файл занят/недоступен — держим последнее удачное состояние
        if (string.Equals(text, _raw, StringComparison.Ordinal)) return; // не изменилось
        var parsed = Parse(text);
        if (parsed == null) return; // битый файл — держим последнее удачное состояние
        _file = parsed;
        _raw = text;
    }

    private void WriteTokenFile(string connectionId, string token)
    {
        var path = TokenFilePath(connectionId);
        AtomicFileWriter.WriteText(path, token + Environment.NewLine);
    }

    private void TryDeleteTokenFile(string connectionId)
    {
        try { File.Delete(TokenFilePath(connectionId)); } catch { /* нет файла — ок */ }
    }

    internal static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>DPAPI CurrentUser. Платформа — Windows-only (продукт Windows-only).</summary>
    internal static string Protect(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MCP token store requires Windows DPAPI");
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    internal static byte[]? Unprotect(string base64)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return ProtectedData.Unprotect(Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    internal static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
