using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BrainstormBuddy.Ai;

/// <summary>
/// Креденшеалы подписки ChatGPT (Codex), прочитанные из <c>~/.codex/auth.json</c>.
/// Файл пишет Codex CLI; Buddy только читает его и никогда не переписывает.
/// </summary>
public sealed record CodexCredentials(
    string AccessToken,
    string RefreshToken,
    string AccountId,
    string PlanType,
    string Email,
    DateTimeOffset? ExpiresAt,
    string SourcePath);

/// <summary>
/// Работа с токенами подписки ChatGPT: чтение <c>~/.codex/auth.json</c>,
/// разбор JWT (аккаунт/план/срок) и обновление access_token по refresh_token.
/// Протокол и параметры - как в oh-my-pi (<c>registry/oauth/openai-codex.ts</c>):
/// POST https://auth.openai.com/oauth/token, grant_type=refresh_token.
/// </summary>
public static class CodexAuth
{
    /// <summary>Endpoint обмена токенов OAuth.</summary>
    public const string TokenUrl = "https://auth.openai.com/oauth/token";

    /// <summary>OAuth client_id официального Codex CLI.</summary>
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    /// <summary>Набор scope'ов официального клиента.</summary>
    public const string Scope = "openid profile email offline_access api.connectors.read api.connectors.invoke";

    private const string AuthClaimPath = "https://api.openai.com/auth";
    private const string ProfileClaimPath = "https://api.openai.com/profile";

    /// <summary>Путь к файлу авторизации Codex по умолчанию.</summary>
    public static string DefaultAuthPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    /// <summary>
    /// Читает креденшеалы из файла авторизации. Возвращает null, если файла нет
    /// или он не содержит access_token. Исключений не бросает.
    /// </summary>
    public static CodexCredentials? TryLoad(string? path = null)
    {
        var file = string.IsNullOrWhiteSpace(path) ? DefaultAuthPath : path!;
        try
        {
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return null;

            var access = Str(tokens, "access_token");
            if (string.IsNullOrWhiteSpace(access)) return null;

            var refresh = Str(tokens, "refresh_token");
            var account = Str(tokens, "account_id");
            var plan = PlanType(access) ?? "";
            var email = Email(access) ?? "";
            if (string.IsNullOrWhiteSpace(account)) account = AccountId(access) ?? "";

            return new CodexCredentials(access, refresh, account, plan, email, ExpiresAt(access), file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Токен просрочен (с запасом на часовую стрелку и сетевые задержки)?</summary>
    public static bool IsExpired(CodexCredentials credentials, TimeSpan? skew = null)
    {
        if (credentials.ExpiresAt == null) return false;
        return credentials.ExpiresAt.Value <= DateTimeOffset.UtcNow + (skew ?? TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Обновляет access_token по refresh_token. Возвращает обновлённые креденшеалы
    /// либо null, если refresh не удался (нет refresh_token или ответ с ошибкой).
    /// Файл <c>~/.codex/auth.json</c> не меняется - новые токены живут в конфиге Buddy.
    /// </summary>
    public static async Task<CodexCredentials?> RefreshAsync(
        CodexCredentials current,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken)) return null;

        var owns = http == null;
        var client = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("refresh_token", current.RefreshToken),
                new KeyValuePair<string, string>("scope", Scope),
            });
            using var resp = await client.PostAsync(TokenUrl, form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var access = Str(root, "access_token");
            if (string.IsNullOrWhiteSpace(access)) return null;

            var refresh = Str(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(refresh)) refresh = current.RefreshToken;
            var account = AccountId(access) ?? current.AccountId;
            var plan = PlanType(access);
            if (string.IsNullOrWhiteSpace(plan)) plan = current.PlanType;
            var email = Email(access);
            if (string.IsNullOrWhiteSpace(email)) email = current.Email;

            return new CodexCredentials(access, refresh, account, plan, email, ExpiresAt(access), current.SourcePath);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (owns) client.Dispose();
        }
    }

    /// <summary>Срок действия access_token (из клейма exp), null - если токен не JWT.</summary>
    public static DateTimeOffset? ExpiresAt(string accessToken)
    {
        var exp = LongClaim(accessToken, "exp");
        return exp == null ? null : DateTimeOffset.FromUnixTimeSeconds(exp.Value);
    }

    /// <summary>Идентификатор ChatGPT-аккаунта из клейма access_token.</summary>
    public static string? AccountId(string accessToken) =>
        ObjectClaim(accessToken, AuthClaimPath, "chatgpt_account_id");

    /// <summary>План подписки (plus/pro/team/...) из клейма access_token.</summary>
    public static string? PlanType(string accessToken) =>
        ObjectClaim(accessToken, AuthClaimPath, "chatgpt_plan_type");

    /// <summary>Email владельца подписки из клейма профиля.</summary>
    public static string? Email(string accessToken) =>
        ObjectClaim(accessToken, ProfileClaimPath, "email");

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Читает JWT-пакет и достаёт поле верхнего уровня.</summary>
    private static long? LongClaim(string token, string claim)
    {
        var payload = DecodePayload(token);
        if (payload == null) return null;
        return payload.Value.TryGetProperty(claim, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : null;
    }

    /// <summary>Читает JWT-пакет и достаёт поле из вложенного объёма по URL-ключу.</summary>
    private static string? ObjectClaim(string token, string objectKey, string field)
    {
        var payload = DecodePayload(token);
        if (payload == null) return null;
        if (!payload.Value.TryGetProperty(objectKey, out var nested) || nested.ValueKind != JsonValueKind.Object)
            return null;
        if (!nested.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static JsonElement? DecodePayload(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
