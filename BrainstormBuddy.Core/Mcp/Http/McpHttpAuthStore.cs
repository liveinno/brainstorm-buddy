using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Core.Mcp.Http;

/// <summary>
/// Хранилище OAuth-клиентов и bearer-токенов HTTP-ingress:
/// %AppData%\BrainstormBuddy\mcp\http-clients.json.
/// Dynamic Client Registration (RFC 7591) без пароля клиента
/// (token_endpoint_auth_method=none, публичный клиент MCP), PKCE S256.
/// В файле — только SHA-256 хэши access/refresh токенов; auth-коды живут
/// в памяти (TTL 5 мин; рестарт между authorize и token инвалидирует код —
/// клиент просто повторяет flow).
/// Потокобезопасно: запись под lock + AtomicFileWriter.
/// </summary>
public sealed class McpHttpAuthStore : IPendingClientSource
{
    private const int TokenBytes = 32;
    private const int AccessTtlSec = 3600;      // 1 час
    private const int RefreshTtlSec = 30 * 24 * 3600; // 30 дней
    private static readonly TimeSpan AuthCodeTtl = TimeSpan.FromMinutes(5);
    /// <summary>Кап на dynamic registration: без него публичный Funnel → мусорная накачка файла.</summary>
    public const int MaxClients = 64;
    /// <summary>Первый контакт клиента ждёт решения владельца не дольше TTL.</summary>
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);
    private const int MaxPending = 32;

    private sealed class HttpClientRec
    {
        [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
        [JsonPropertyName("redirect_uris")] public List<string> RedirectUris { get; set; } = new();
        [JsonPropertyName("client_name")] public string ClientName { get; set; } = "";
        [JsonPropertyName("created_utc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class AccessTokenRec
    {
        [JsonPropertyName("token_hash")] public string TokenHash { get; set; } = "";
        [JsonPropertyName("connection_id")] public string ConnectionId { get; set; } = "";
        [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
        [JsonPropertyName("expires_utc")] public DateTime ExpiresUtc { get; set; }
        [JsonPropertyName("issued_utc")] public DateTime IssuedUtc { get; set; }
        /// <summary>Хэш refresh-токена или null. Refresh переживает рестарт.</summary>
        [JsonPropertyName("refresh_hash")] public string? RefreshHash { get; set; }
        [JsonPropertyName("refresh_expires_utc")] public DateTime? RefreshExpiresUtc { get; set; }
    }

    private sealed class StoreFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("clients")] public List<HttpClientRec> Clients { get; set; } = new();
        [JsonPropertyName("access_tokens")] public List<AccessTokenRec> AccessTokens { get; set; } = new();
        [JsonPropertyName("approved_clients")] public List<string> ApprovedClients { get; set; } = new();
    }


    private sealed class AuthCode
    {
        public required string ClientId { get; init; }
        public required string ConnectionId { get; init; }
        public required string RedirectUri { get; init; }
        public required string CodeChallenge { get; init; }
        public required string Method { get; init; } // S256|plain
        public DateTime ExpiresUtc { get; } = DateTime.UtcNow + AuthCodeTtl;
    }
    /// <summary>Первый контакт клиента: ждёт решения владельца (consent gate).</summary>
    private sealed class PendingAuth
    {
        public required string PendingId { get; init; }
        public required string ClientId { get; init; }
        public required string ConnectionId { get; init; }
        public required string RedirectUri { get; init; }
        public required string CodeChallenge { get; init; }
        public string? State { get; init; }
        public string? ClientName { get; init; }
        public string? ScopeLabel { get; init; }
        public DateTime RequestedUtc { get; } = DateTime.UtcNow;
        /// <summary>pending|approved|denied.</summary>
        public string Status { get; set; } = "pending";
        /// <summary>Redirect URL для браузера клиента после решения владельца.</summary>
        public string? ResultRedirect { get; set; }
    }

    /// <summary>Результат попытки первого контакта: либо ждём, либо уже решён.</summary>
    public readonly record struct PendingDecision(
        McpPendingClient Pending, bool Resolved, string? RedirectUrl);

    /// <summary>Результат регистрации клиента (RFC 7591).</summary>
    public sealed record ClientRegistration(string ClientId, IReadOnlyList<string> RedirectUris);

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly McpPermissionStore _permissions;
    private readonly Func<IReadOnlyList<McpConnectionEntry>> _connections;
    private StoreFile _file;
    private readonly ConcurrentDictionary<string, AuthCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingAuth> _pending = new(StringComparer.Ordinal);

    public McpHttpAuthStore(
        string appDataDir,
        McpPermissionStore permissions,
        Func<IReadOnlyList<McpConnectionEntry>> connections)
    {
        _filePath = Path.Combine(appDataDir, "mcp", "http-clients.json");
        _permissions = permissions;
        _connections = connections;
        _file = Load(_filePath);
    }

    public string FilePath => _filePath;

    // ── RFC 7591 dynamic client registration ────────────────────────────────

    /// <summary>
    /// Публичный клиент: секрет не выдаём (token_endpoint_auth_method=none).
    /// Кап MaxClients: при переполнении вытесняем самого старого клиента без
    /// токенов и без consent-аппрува; approved/used не вытесняются → null (429).
    /// </summary>
    public ClientRegistration? RegisterClient(IEnumerable<string> redirectUris, string clientName)
    {
        var rec = new HttpClientRec
        {
            ClientId = "mcph_" + McpPermissionStore.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            RedirectUris = redirectUris.Where(u => !string.IsNullOrWhiteSpace(u))
                                     .Select(u => u.Trim())
                                     .Distinct(StringComparer.Ordinal)
                                     .ToList(),
            ClientName = string.IsNullOrWhiteSpace(clientName) ? "mcp-client" : clientName.Trim(),
        };
        lock (_gate)
        {
            if (_file.Clients.Count >= MaxClients && !TryEvictStaleClientLocked())
                return null;
            _file.Clients.Add(rec);
            SaveLocked();
        }
        return new ClientRegistration(rec.ClientId, rec.RedirectUris);
    }

    /// <summary>Вытеснение старейшего «мёртвого» клиента (не approved, токенов нет).</summary>
    private bool TryEvictStaleClientLocked()
    {
        var victim = _file.Clients
            .Where(c => !_file.ApprovedClients.Contains(c.ClientId)
                        && !_file.AccessTokens.Any(t => t.ClientId == c.ClientId))
            .OrderBy(c => c.CreatedUtc)
            .FirstOrDefault();
        if (victim == null) return false;
        _file.Clients.Remove(victim);
        return true;
    }

    public bool RedirectUriAllowed(string clientId, string redirectUri)
    {
        lock (_gate)
        {
            var c = _file.Clients.FirstOrDefault(x => x.ClientId == clientId);
            return c?.RedirectUris.Any(u => string.Equals(u, redirectUri, StringComparison.Ordinal)) == true;
        }
    }

    /// <summary>Имя клиента для показа владельцу (из регистрации).</summary>
    public string? ClientNameOf(string clientId)
    {
        lock (_gate)
            return _file.Clients.FirstOrDefault(c => c.ClientId == clientId)?.ClientName;
    }

    // ── first-contact consent (публичный Funnel: анонимный minting закрыт) ───

    /// <summary>Клиент уже одобрен владельцем ранее — повторное согласие не требуется.</summary>
    public bool IsApproved(string clientId)
    {
        lock (_gate) return _file.ApprovedClients.Contains(clientId);
    }

    /// <summary>Сигнал UI: новый pending-консент (владелец должен решить).</summary>
    public event Action<McpPendingClient>? PendingAdded;

    /// <summary>
    /// Первый контакт: регистрирует pending-консент и возвращает его (дедуп по
    /// clientId+code_challenge — повторный /authorize того же запроса не спамит
    /// владельца). Если по дедуп-ключу решение уже принято — вернёт готовый
    /// redirect (meta-refresh с интерстишела уйдёт сразу, без нового pending).
    /// null — достигнут MaxPending (429 наружу).
    /// </summary>
    public PendingDecision? EnsurePending(
        string clientId, string redirectUri, string codeChallenge,
        string? state, string connectionId, string? scopeLabel)
    {
        var dedupKey = clientId + "|" + codeChallenge;
        var now = DateTime.UtcNow;
        // Мусорим решённые/просроченные старше часа — иначе кап MaxPending
        // засчитывал бы историю (поток регистраций в тестах/рестарт клиента).
        var cutoff = now - TimeSpan.FromHours(1);
        foreach (var kv in _pending)
        {
            var p0 = kv.Value;
            if ((p0.Status != "pending" && p0.RequestedUtc < cutoff)
                || p0.RequestedUtc + PendingTtl < now - TimeSpan.FromHours(1))
                _pending.TryRemove(kv.Key, out _);
        }
        foreach (var p in _pending.Values)
        {
            if (!string.Equals(p.ClientId + "|" + p.CodeChallenge, dedupKey, StringComparison.Ordinal))
                continue;
            if (p.RequestedUtc + PendingTtl < now) continue;
            var resolved = p.Status != "pending";
            return new PendingDecision(ToPublic(p), resolved, p.ResultRedirect);
        }

        // Кап только по живым неразрешённым: легитимный поток решённых
        // консентов не должен приводить к 429.
        if (_pending.Values.Count(p => p.Status == "pending"
                && p.RequestedUtc + PendingTtl >= now) >= MaxPending)
            return null;
        var pending = new PendingAuth
        {
            PendingId = "pend_" + McpPermissionStore.Base64UrlEncode(RandomNumberGenerator.GetBytes(12)),
            ClientId = clientId,
            ConnectionId = connectionId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            State = state,
            ClientName = ClientNameOf(clientId),
            ScopeLabel = scopeLabel,
        };
        _pending[pending.PendingId] = pending;
        var pub = ToPublic(pending);
        try { PendingAdded?.Invoke(pub); } catch { /* UI-сигнал не должен ломать flow */ }
        return new PendingDecision(pub, Resolved: false, RedirectUrl: null);
    }

    /// <summary>Поллинг с интерстишела: state + redirect (approved/denied).</summary>
    public (string State, string? RedirectUrl)? ConsentStatus(string pendingId)
    {
        if (!_pending.TryGetValue(pendingId, out var p)) return null;
        var expired = p.Status == "pending" && p.RequestedUtc + PendingTtl < DateTime.UtcNow;
        var state = expired ? "expired" : p.Status;
        return (state, state == "pending" ? null : p.ResultRedirect);
    }

    /// <summary>Для карточки владельца: только живые непросроченные pending.</summary>
    public IReadOnlyList<McpPendingClient> ListPending()
    {
        var now = DateTime.UtcNow;
        return _pending.Values
            .Where(p => p.Status == "pending" && p.RequestedUtc + PendingTtl >= now)
            .OrderBy(p => p.RequestedUtc)
            .Select(ToPublic)
            .ToList();
    }

    /// <summary>
    /// Решение владельца. approve: клиент → approvedClients (persisted) + мятится
    /// auth-code, ResultRedirect = redirect_uri?code=..&state=...  deny: redirect
    /// с error=access_denied. false — неизвестный/просроченный/уже решённый id.
    /// </summary>
    public bool ResolvePending(string pendingId, bool approve)
    {
        if (!_pending.TryGetValue(pendingId, out var p)) return false;
        if (p.Status != "pending" || p.RequestedUtc + PendingTtl < DateTime.UtcNow) return false;

        if (approve)
        {
            var code = BeginAuthorization(p.ClientId, p.RedirectUri, p.CodeChallenge, "S256", p.ConnectionId);
            if (code == null)
            {
                p.Status = "denied";
                p.ResultRedirect = BuildRedirect(p.RedirectUri, "error=server_error", p.State);
                return true;
            }
            lock (_gate)
            {
                if (!_file.ApprovedClients.Contains(p.ClientId))
                {
                    _file.ApprovedClients.Add(p.ClientId);
                    SaveLocked();
                }
            }
            p.Status = "approved";
            p.ResultRedirect = BuildRedirect(p.RedirectUri, "code=" + Uri.EscapeDataString(code), p.State);
        }
        else
        {
            p.Status = "denied";
            p.ResultRedirect = BuildRedirect(p.RedirectUri, "error=access_denied", p.State);
        }
        return true;
    }

    private static string BuildRedirect(string redirectUri, string query, string? state)
    {
        var sep = redirectUri.Contains('?') ? "&" : "?";
        return redirectUri + sep + query
            + (state != null ? "&state=" + Uri.EscapeDataString(state) : "");
    }

    private static McpPendingClient ToPublic(PendingAuth p) => new(
        p.PendingId, p.ClientId, p.ClientName, p.RedirectUri, p.ScopeLabel ?? "", p.RequestedUtc);
    // ── authorization code (in-memory, PKCE) ────────────────────────────────

    /// <summary>
    /// Согласие уже выражено локально в permission-модели (scope выдан мастером
    /// один раз): code выписывается сразу на connectionId, без интерактивного UI.
    /// </summary>
    public string? BeginAuthorization(
        string clientId, string redirectUri, string codeChallenge, string method, string connectionId)
    {
        if (!RedirectUriAllowed(clientId, redirectUri)) return null;
        if (string.IsNullOrEmpty(connectionId)) return null;
        var code = McpPermissionStore.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        _codes[code] = new AuthCode
        {
            ClientId = clientId,
            ConnectionId = connectionId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            Method = string.IsNullOrEmpty(method) ? "S256" : method,
        };
        return code;
    }

    /// <summary>
    /// code+verifier+client+redirect → access/refresh токены. null — код невалиден
    /// (просрочен/использован/PKCE не сошёлся/redirect другой).
    /// </summary>
    public (string AccessToken, int ExpiresIn, string? RefreshToken)? ExchangeCode(
        string code, string codeVerifier, string clientId, string redirectUri)
    {
        if (!_codes.TryRemove(code, out var ac)) return null;
        if (ac.ExpiresUtc < DateTime.UtcNow) return null;
        if (!string.Equals(ac.ClientId, clientId, StringComparison.Ordinal)) return null;
        if (!string.Equals(ac.RedirectUri, redirectUri, StringComparison.Ordinal)) return null;
        if (!VerifyPkce(ac, codeVerifier)) return null;
        if (!GrantAlive(ac.ConnectionId)) return null;
        return IssueTokens(ac.ConnectionId, clientId);
    }

    /// <summary>PKCE только S256: plain вычёркиваем на /authorize, здесь — догоняющий guard.</summary>
    private static bool VerifyPkce(AuthCode ac, string verifier)
    {
        if (string.IsNullOrEmpty(verifier)) return false;
        if (!ac.Method.Equals("S256", StringComparison.OrdinalIgnoreCase)) return false;
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        var computed = McpPermissionStore.Base64UrlEncode(hash);
        return string.Equals(ac.CodeChallenge, computed, StringComparison.Ordinal);
    }
    /// <summary>refresh_token → новый access-токен (rotation: старый refresh инвалидируется).</summary>
    public (string AccessToken, int ExpiresIn, string? RefreshToken)? ExchangeRefresh(
        string refreshToken, string clientId)
    {
        var hash = McpPermissionStore.HashTokenString(refreshToken);
        string connectionId;
        lock (_gate)
        {
            var rec = _file.AccessTokens.FirstOrDefault(t =>
                t.RefreshHash != null &&
                string.Equals(t.RefreshHash, hash, StringComparison.Ordinal) &&
                string.Equals(t.ClientId, clientId, StringComparison.Ordinal));
            if (rec == null || rec.RefreshExpiresUtc < DateTime.UtcNow) return null;
            connectionId = rec.ConnectionId;
            _file.AccessTokens.Remove(rec);
            SaveLocked();
        }
        if (!GrantAlive(connectionId)) return null;
        return IssueTokens(connectionId, clientId);
    }

    private (string, int, string?) IssueTokens(string connectionId, string clientId)
    {
        var access = "bbm_" + McpPermissionStore.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        var refresh = "bbr_" + McpPermissionStore.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        var rec = new AccessTokenRec
        {
            TokenHash = McpPermissionStore.HashTokenString(access),
            ConnectionId = connectionId,
            ClientId = clientId,
            IssuedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow + TimeSpan.FromSeconds(AccessTtlSec),
            RefreshHash = McpPermissionStore.HashTokenString(refresh),
            RefreshExpiresUtc = DateTime.UtcNow + TimeSpan.FromSeconds(RefreshTtlSec),
        };
        lock (_gate)
        {
            _file.AccessTokens.RemoveAll(t => t.ConnectionId == connectionId && t.ClientId == clientId);
            _file.AccessTokens.Add(rec);
            SaveLocked();
        }
        return (access, AccessTtlSec, refresh);
    }

    // ── bearer → resolved connection ────────────────────────────────────────

    /// <summary>
    /// Bearer → resolved connection. Отозванный permission (Revoke) инвалидирует
    /// access-токены немедленно — проверка гранта выполняется на каждый вызов.
    /// </summary>
    public McpResolvedConnection? ResolveAccessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = McpPermissionStore.HashTokenString(token);
        AccessTokenRec? rec;
        lock (_gate)
            rec = _file.AccessTokens.FirstOrDefault(t =>
                string.Equals(t.TokenHash, hash, StringComparison.Ordinal));
        if (rec == null || rec.ExpiresUtc < DateTime.UtcNow) return null;
        if (!GrantAlive(rec.ConnectionId)) return null;
        var entry = _connections().FirstOrDefault(c => c.Id == rec.ConnectionId);
        if (entry == null || entry.Suspended) return null;
        return McpConnectionMapping.ToResolved(entry);
    }

    /// <summary>Грант подключения жив и не удалён из конфига.</summary>
    private bool GrantAlive(string connectionId)
    {
        if (!_permissions.HasGrant(connectionId)) return false;
        return _connections().Any(c => c.Id == connectionId);
    }

    /// <summary>Удалить access-токены подключения (ревокация/перевыпуск).</summary>
    public int RevokeConnection(string connectionId)
    {
        lock (_gate)
        {
            var n = _file.AccessTokens.RemoveAll(t => t.ConnectionId == connectionId);
            if (n > 0) SaveLocked();
            return n;
        }
    }

    // ── persistence ─────────────────────────────────────────────────────────

    private static StoreFile Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var f = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path));
                if (f?.Clients != null && f.AccessTokens != null)
                {
                    // Протухшие токены вычищаем на старте — файла маленький, раз в запуск.
                    f.AccessTokens.RemoveAll(t =>
                        t.ExpiresUtc < DateTime.UtcNow &&
                        (t.RefreshExpiresUtc == null || t.RefreshExpiresUtc < DateTime.UtcNow));
                    return f;
                }
            }
        }
        catch { /* битый файл — пустое хранилище */ }
        return new StoreFile();
    }

    private void SaveLocked()
        => AtomicFileWriter.WriteText(_filePath,
            JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));
}
