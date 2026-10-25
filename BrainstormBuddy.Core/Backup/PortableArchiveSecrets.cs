using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BrainstormBuddy.Core.Backup;

/// <summary>
/// Перенос DPAPI-секретов профиля внутри зашифрованного архива.
///
/// Источник — mcp/permissions.json: grants[].protectedToken (DPAPI
/// CurrentUser-блоб исходной учётной записи). При создании архива блобы
/// расшифровываются текущим пользователем и складываются в отдельную запись
/// ZIP <c>mcp-secrets.json</c> — она зашифрована вместе со всем payload и не
/// фигурирует в манифесте (имена внешних файлов не утекают, секреты на диск
/// не падают). При восстановлении токены перешифровываются DPAPI новой
/// учётной записи: protectedToken заменяется, tokenHash сохраняется (те же
/// байты), файл mcp/tokens/&lt;id&gt;.token перезаписывается base64url-представлением —
/// после restore входящие MCP-подключения работают сразу, а визард может
/// показать токен повторно.
///
/// Секреты никогда не попадают в логи/сообщения: здесь только количества
/// и connectionId.
/// </summary>
internal static class PortableArchiveSecrets
{
    /// <summary>Имя записи с секретами внутри зашифрованного ZIP.</summary>
    internal const string SecretsEntryName = "mcp-secrets.json";

    /// <summary>Одна перенесённая связка (токен — сырые байты в base64).</summary>
    internal sealed record SecretRecord(
        [property: JsonPropertyName("connectionId")] string ConnectionId,
        [property: JsonPropertyName("token")] string Token);

    internal sealed record SecretsFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("secrets")] List<SecretRecord> Secrets);

    /// <summary>
    /// Извлекает DPAPI-расшифровываемые токены из mcp/permissions.json
    /// профиля. Возвращает список (возможно пустой) перенесённых связок —
    /// вызывающий сравнивает connectionId с грантами и решает, какие
    /// соединения останутся RequiresLogin.
    /// </summary>
    internal static List<SecretRecord> Export(string profileDir)
    {
        var records = new List<SecretRecord>();
        var path = Path.Combine(profileDir, "mcp", "permissions.json");
        if (!File.Exists(path)) return records;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("grants", out var grants) ||
                grants.ValueKind != JsonValueKind.Array)
                return records;
            foreach (var g in grants.EnumerateArray())
            {
                var connId = g.TryGetProperty("connectionId", out var c)
                    && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? ""
                        : "";
                if (connId.Length == 0 ||
                    !g.TryGetProperty("protectedToken", out var pt) ||
                    pt.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(pt.GetString()))
                    continue;
                var raw = UnprotectCurrentUser(pt.GetString()!);
                if (raw is { Length: > 0 })
                    records.Add(new SecretRecord(connId, Convert.ToBase64String(raw)));
            }
        }
        catch { /* файл ещё пишется/битый — секреты не экспортируем, данные архивируются как есть */ }
        return records;
    }

    /// <summary>Сериализация набора секретов для записи в ZIP-поток.</summary>
    internal static SecretsFile Pack(List<SecretRecord> records)
        => new(1, records);

    /// <summary>Разбор записи mcp-secrets.json (null при отсутствии/повреждении).</summary>
    internal static List<SecretRecord>? Unpack(Stream s, CancellationToken ct)
    {
        try
        {
            var f = JsonSerializer.Deserialize<SecretsFile>(s,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return f?.Secrets;
        }
        catch { return null; }
    }

    /// <summary>
    /// Перепривязывает токены к текущей учётной записи в staging-каталоге:
    /// перезаписывает protectedToken в mcp/permissions.json DPAPI-блобом этого
    /// пользователя и обновляет mcp/tokens/&lt;id&gt;.token. Возвращает число
    /// привязанных токенов; ошибки → warnings (нефатально).
    /// Путь permissions.json и каталог токенов берутся из
    /// <paramref name="effective"/> — PathRemapper мог перенести их, тогда
    /// правится фактическое расположение, а не фиксированное имя.
    /// </summary>
    internal static int Rebind(
        string stagingRoot, IReadOnlyList<SecretRecord> records,
        List<string> warnings, IReadOnlyDictionary<string, string>? effective = null)
    {
        if (records.Count == 0) return 0;
        var permPath = ResolveEffective(stagingRoot, "mcp/permissions.json", effective);
        // Каталог токенов: если PathRemapper перенёс существующие файлы
        // mcp/tokens/*, пишем рядом с ними; иначе дефолтное место.
        var tokensDir = Path.Combine(stagingRoot, "mcp", "tokens");
        if (effective != null)
        {
            foreach (var kv in effective)
            {
                if (!kv.Key.StartsWith("mcp/tokens/", StringComparison.Ordinal)) continue;
                var dir = Path.GetDirectoryName(kv.Value);
                var resolved = dir != null
                    ? PortableArchiveScanner.ResolveUnderRoot(stagingRoot, dir)
                    : null;
                if (resolved != null) { tokensDir = resolved; break; }
            }
        }
        var bound = 0;

        JsonNode? root = null;
        JsonArray? grants = null;
        if (File.Exists(permPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(permPath));
                grants = root?["grants"] as JsonArray;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                warnings.Add($"Не удалось перечитать permissions.json: {ex.Message}");
            }
        }

        foreach (var rec in records)
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(rec.Token); }
            catch { continue; }

            // Перешифровка блоба под новую учётную запись ДО записи файла:
            // plaintext-токен без перепривязанного блоба — полуфабрикат
            // (платформа без DPAPI получила бы рабочий файл токена и
            // одновременно RequiresLogin на гранте).
            var reProtected = ProtectCurrentUser(raw);
            if (reProtected == null) continue;

            JsonNode? grant = grants?.FirstOrDefault(g =>
                g is JsonObject go &&
                go.TryGetPropertyValue("connectionId", out var cn) &&
                cn is JsonValue jv &&
                jv.TryGetValue<string>(out var cid) &&
                string.Equals(cid, rec.ConnectionId, StringComparison.Ordinal));
            // В восстановленном permissions.json гранта нет — секрет сирота,
            // файл токена без гранта не нужен.
            if (grant == null) continue;

            try
            {
                // Файл plaintext-токена, который McpHost читает при старте.
                Directory.CreateDirectory(tokensDir);
                var tokenFile = Path.Combine(tokensDir,
                    SanitizeFileName(rec.ConnectionId) + ".token");
                File.WriteAllText(tokenFile,
                    Base64Url(raw) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Не удалось записать токен-файл для {rec.ConnectionId}: {ex.Message}");
                continue;
            }

            grant["protectedToken"] = reProtected;
            bound++;
        }

        if (bound > 0 && root != null)
        {
            try
            {
                var tmp = permPath + ".reloc";
                File.WriteAllText(tmp, root.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, permPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                warnings.Add($"Не удалось переписать permissions.json: {ex.Message}");
            }
        }
        return bound;
    }

    /// <summary>
    /// Гранты permissions.json, чей protectedToken не расшифровывается текущей
    /// учётной записью (старый блоб без переноса). Для RequiresLogin-деталей.
    /// <paramref name="effective"/> — фактический путь файла после PathRemapper.
    /// </summary>
    internal static List<string> StaleConnectionIds(
        string profileDir, IReadOnlyDictionary<string, string>? effective = null)
    {
        var stale = new List<string>();
        var path = ResolveEffective(profileDir, "mcp/permissions.json", effective);
        if (!File.Exists(path)) return stale;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("grants", out var grants) ||
                grants.ValueKind != JsonValueKind.Array)
                return stale;
            foreach (var g in grants.EnumerateArray())
            {
                if (!g.TryGetProperty("protectedToken", out var pt) ||
                    pt.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(pt.GetString()))
                    continue;
                var connId = g.TryGetProperty("connectionId", out var c)
                    && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? "" : "";
                if (UnprotectCurrentUser(pt.GetString()!) == null && connId.Length > 0)
                    stale.Add(connId);
            }
        }
        catch { }
        return stale;
    }

    /// <summary>Фактический путь записи внутри корня (с учётом PathRemapper).</summary>
    private static string ResolveEffective(
        string root, string archivePath, IReadOnlyDictionary<string, string>? effective)
    {
        if (effective != null && effective.TryGetValue(archivePath, out var rel))
        {
            var resolved = PortableArchiveScanner.ResolveUnderRoot(root, rel);
            if (resolved != null) return resolved;
        }
        return Path.Combine(root, archivePath.Replace('/', Path.DirectorySeparatorChar));
    }


    // DPAPI CurrentUser — Windows-only; на прочих платформах секрет просто
    // не переносится (grant остаётся RequiresLogin).
    private static string? ProtectCurrentUser(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Convert.ToBase64String(
                ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }

    private static byte[]? UnprotectCurrentUser(string base64)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ProtectedData.Unprotect(
                Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
