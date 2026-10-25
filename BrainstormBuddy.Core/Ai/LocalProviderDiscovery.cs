using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BrainstormBuddy.Ai;

/// <summary>Состояние одного источника обнаружения локальной подписки.</summary>
public enum LocalDiscoveryState
{
    /// <summary>Ещё не проверяли.</summary>
    Pending,
    /// <summary>Проверяем прямо сейчас.</summary>
    Checking,
    /// <summary>Источник найден.</summary>
    Found,
    /// <summary>Источник не найден (файла/переменной/сервера нет).</summary>
    Missing,
    /// <summary>Источник найден, но Buddy не умеет говорить с ним по своей версии протокола.</summary>
    Unsupported,
    /// <summary>Проверка сорвалась (нечитаемый файл, сетевая ошибка).</summary>
    Failed,
}

/// <summary>
/// Результат проверки одного источника. <paramref name="Profile"/> и
/// <paramref name="ApiKey"/> заполнены только для найденных источников;
/// <paramref name="CanApply"/> = true, если найденный источник можно сразу
/// подключить в Buddy (профиль поддерживается текущим клиентом).
/// </summary>
public sealed record LocalDiscoveryStep(
    string SourceId,
    string DisplayName,
    LocalDiscoveryState State,
    string Summary,
    string Detail,
    LlmProviderProfile? Profile = null,
    string? ApiKey = null,
    bool CanApply = false,
    /// <summary>refresh_token (только для подписки ChatGPT/Codex) - нужен для обновления токена.</summary>
    string? RefreshToken = null)
{
    /// <summary>Короткая метка состояния для UI.</summary>
    public string StateLabel => State switch
    {
        LocalDiscoveryState.Found => "найдено",
        LocalDiscoveryState.Missing => "не найдено",
        LocalDiscoveryState.Unsupported => "не поддерживается",
        LocalDiscoveryState.Failed => "ошибка",
        LocalDiscoveryState.Checking => "проверка…",
        _ => "ожидает",
    };

    /// <summary>
    /// Строковое представление без секретов: шаг обнаружения уходит в лог и
    /// в отчёт, а access_token/refresh_token там быть не должны.
    /// </summary>
    public override string ToString()
        => $"{SourceId} [{StateLabel}] {Summary}";
}

/// <summary>Итог сканирования локальных подписок.</summary>
public sealed record LocalDiscoveryReport(
    int Total,
    int Found,
    TimeSpan Elapsed,
    IReadOnlyList<LocalDiscoveryStep> Steps);

/// <summary>
/// Параметры сканирования. Вынесены отдельно, чтобы юнит-тесты подставляли
/// временный «домашний» каталог и набор переменных окружения, а приложение
/// работало с реальным окружением пользователя.
/// </summary>
public sealed class LocalDiscoveryOptions
{
    /// <summary>Домашний каталог пользователя (где ищем ~/.codex, ~/.config/opencode и т.п.).</summary>
    public string HomeDirectory { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Чтение переменной окружения (в тестах — подставной словарь).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; set; } =
        Environment.GetEnvironmentVariable;

    /// <summary>Проверять локальные HTTP-серверы (LM Studio). В юнит-тестах выключается.</summary>
    public bool ProbeLocalServers { get; set; } = true;

    /// <summary>Таймаут локального HTTP-запроса, секунды.</summary>
    public int HttpTimeoutSeconds { get; set; } = 4;

    /// <summary>Путь к файлу с ключами opencode (по умолчанию ~/.local/share/opencode/auth.json).</summary>
    public string? OpenCodeAuthPathOverride { get; set; }
}

/// <summary>Один проверяемый источник локальной подписки.</summary>
public interface ILocalProviderProbe
{
    /// <summary>Стабильный id источника (для UI и логов).</summary>
    string Id { get; }

    /// <summary>Человеческое имя источника.</summary>
    string DisplayName { get; }

    /// <summary>Что именно ищем — показывается в строке «не найдено».</summary>
    string Expectation { get; }

    /// <summary>Проверка источника. Не бросает: ошибка возвращается как Failed.</summary>
    Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct);
}

/// <summary>
/// Сканер локально установленных LLM-подписок. Только чтение: файлы авторизации
/// читаются, но нигде не изменяются; найденные ключи попадают в конфиг только
/// по явному действию пользователя.
/// </summary>
public static class LocalProviderDiscovery
{
    /// <summary>Источники проверяются в этом порядке (сначала самые вероятные).</summary>
    public static IReadOnlyList<ILocalProviderProbe> DefaultProbes { get; } = new ILocalProviderProbe[]
    {
        new CodexChatGptProbe(),
        new OpenCodeGoProbe(),
        new OpenAiKeyProbe(),
        new GeminiApiKeyProbe(),
        new GeminiCliProbe(),
        new GroqKeyProbe(),
        new OpenRouterKeyProbe(),
        new NvidiaNimKeyProbe(),
        new LmStudioProbe(),
        new DevinCliProbe(),
        new CursorProbe(),
        new FactoryDroidProbe(),
        new AntigravityProbe(),
    };

    /// <summary>
    /// Последовательно проверяет все источники. Каждый результат отдаётся в
    /// <paramref name="progress"/> сразу после проверки — UI показывает шаги по
    /// мере готовности, а не пачкой в конце.
    /// </summary>
    public static async Task<LocalDiscoveryReport> ScanAsync(
        LocalDiscoveryOptions options,
        IProgress<LocalDiscoveryStep>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<LocalDiscoveryStep>(DefaultProbes.Count);
        foreach (var probe in DefaultProbes)
        {
            ct.ThrowIfCancellationRequested();
            LocalDiscoveryStep step;
            try
            {
                step = await probe.ProbeAsync(options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                step = new LocalDiscoveryStep(probe.Id, probe.DisplayName, LocalDiscoveryState.Failed,
                    "Проверка не удалась", ex.Message);
            }
            steps.Add(step);
            progress?.Report(step);
        }
        sw.Stop();
        return new LocalDiscoveryReport(steps.Count, steps.Count(s => s.State == LocalDiscoveryState.Found), sw.Elapsed, steps);
    }

    // ---------- общие помощники ----------

    /// <summary>Путь внутри домашнего каталога (пустой каталог → пустой путь).</summary>
    internal static string HomePath(LocalDiscoveryOptions options, params string[] parts)
    {
        var home = options.HomeDirectory ?? "";
        if (string.IsNullOrWhiteSpace(home)) return "";
        var segments = new List<string> { home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) };
        segments.AddRange(parts);
        return Path.Combine(segments.ToArray());
    }

    /// <summary>Читает JSON-файл; при ошибке возвращает null (исключение наружу не идёт).</summary>
    internal static async Task<(bool Exists, JsonDocument? Doc, string? Error)> TryReadJsonAsync(
        string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (false, null, null);
        try
        {
            await using var stream = File.OpenRead(path);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return (true, doc, null);
        }
        catch (Exception ex)
        {
            return (true, null, ex.Message);
        }
    }

    /// <summary>Значение строкового поля верхнего уровня (пусто, если нет).</summary>
    internal static string Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    /// <summary>
    /// Модель по умолчанию из конфига Codex (~/.codex/config.toml): верхнеуровневая
    /// строка model = "…". Пусто — конфига нет или модель в нём не задана.
    /// </summary>
    internal static string ReadCodexConfiguredModel(string home)
    {
        if (string.IsNullOrWhiteSpace(home)) return "";
        try
        {
            var path = Path.Combine(home, ".codex", "config.toml");
            if (!File.Exists(path)) return "";
            foreach (var raw in File.ReadLines(path))
            {
                // Ключи верхнего уровня в TOML не с отступом (в секциях — с отступом).
                if (raw.Length == 0 || char.IsWhiteSpace(raw[0])) continue;
                var line = raw.Trim();
                if (!line.StartsWith("model", StringComparison.Ordinal)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var name = line[..eq].Trim();
                if (!string.Equals(name, "model", StringComparison.Ordinal)) continue;
                var value = line[(eq + 1)..].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch
        {
            // Конфиг не читается — модель по умолчанию возьмём из списка бэкенда.
        }
        return "";
    }

    /// <summary>Значение строкового поля вложенного объекта (пусто, если нет).</summary>
    internal static string NestedStr(JsonElement obj, string parent, string name)
        => obj.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, name) : "";

    /// <summary>Пишет claim из JWT payload (base64url) — без проверки подписи.</summary>
    internal static string JwtClaim(string token, string claim)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var parts = token.Split('.');
        if (parts.Length < 2) return "";
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty(claim, out var direct))
                return direct.ValueKind == JsonValueKind.String ? (direct.GetString() ?? "") : "";
            // claim-ключи с URL внутри (например "https://api.openai.com/auth")
            foreach (var prop in root.EnumerateObject())
            {
                if (!prop.Name.EndsWith(claim, StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("value", out var inner))
                    return inner.ValueKind == JsonValueKind.String ? (inner.GetString() ?? "") : "";
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var sub in prop.Value.EnumerateObject())
                    {
                        if (sub.Name.EndsWith(claim, StringComparison.Ordinal) && sub.Value.ValueKind == JsonValueKind.String)
                            return sub.Value.GetString() ?? "";
                    }
                }
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Значение claim-а, у которого ключ — URL (claim-объект).</summary>
    internal static string JwtObjectClaim(string token, string claimUrl, string field)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var parts = token.Split('.');
        if (parts.Length < 2) return "";
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(claimUrl, out var obj) && obj.ValueKind == JsonValueKind.Object)
                return Str(obj, field);
            return "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Прячет середину e-mail (a***@b***.com) — ключи/почта не показываются целиком.</summary>
    internal static string MaskEmail(string email)
    {
        var e = (email ?? "").Trim();
        if (e.Length == 0) return "";
        var at = e.IndexOf('@');
        if (at <= 0) return e.Length <= 2 ? e : e[..1] + "…";
        var name = e[..at];
        var domain = e[(at + 1)..];
        var head = name.Length <= 2 ? name[..1] : name[..2];
        var dot = domain.LastIndexOf('.');
        var dom = dot > 0 ? domain[..dot] : domain;
        var tail = dot > 0 ? domain[dot..] : "";
        return $"{head}…@{dom[..Math.Min(2, dom.Length)]}…{tail}";
    }
}

/// <summary>
/// Codex / ChatGPT-подписка: читаем ~/.codex/auth.json (его пишет Codex CLI после
/// входа). Формат: {"auth_mode":"chatgpt","tokens":{"access_token":"JWT",
/// "account_id":"…","refresh_token":"…"}}. План и почта берутся из claim-ов JWT.
/// </summary>
internal sealed class CodexChatGptProbe : ILocalProviderProbe
{
    public string Id => "codex";
    public string DisplayName => "Codex (подписка ChatGPT)";
    public string Expectation => "файл ~/.codex/auth.json";

    // Клиентская версия, закреплённая в oh-my-pi: бэкенд отсекает модели по ней.
    // 0.163.0 — последний стабильный релиз @openai/codex (npm): при нём доступны
    // и gpt-6.1-sol (модель из конфига Codex на этой машине), и gpt-5.6-sol.
    private const string ClientVersion = "0.163.0";

    public async Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var path = LocalProviderDiscovery.HomePath(options, ".codex", "auth.json");
        var (exists, doc, error) = await LocalProviderDiscovery.TryReadJsonAsync(path, ct);
        if (!exists)
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Файл авторизации не найден", $"Ожидался: {path}");
        if (doc == null)
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Failed,
                "Файл не читается", $"{path}: {error}");

        using (doc)
        {
            var root = doc.RootElement;
            var tokens = root.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
            string accessToken = tokens.ValueKind == JsonValueKind.Object ? LocalProviderDiscovery.Str(tokens, "access_token") : "";
            if (string.IsNullOrWhiteSpace(accessToken))
                return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                    "Токена доступа нет", $"Файл найден ({path}), но tokens.access_token пуст");

            var accountId = tokens.ValueKind == JsonValueKind.Object
                ? LocalProviderDiscovery.Str(tokens, "account_id") : "";
            if (string.IsNullOrWhiteSpace(accountId))
                accountId = LocalProviderDiscovery.JwtObjectClaim(accessToken, "https://api.openai.com/auth", "chatgpt_account_id");
            var plan = LocalProviderDiscovery.JwtObjectClaim(accessToken, "https://api.openai.com/auth", "chatgpt_plan_type");
            var email = LocalProviderDiscovery.JwtObjectClaim(accessToken, "https://api.openai.com/profile", "email");

            var planLabel = string.IsNullOrWhiteSpace(plan) ? "ChatGPT" : $"ChatGPT {plan}";
            var summary = string.IsNullOrWhiteSpace(email)
                ? $"{planLabel} · аккаунт {accountId[..Math.Min(8, accountId.Length)]}…"
                : $"{planLabel} · {LocalProviderDiscovery.MaskEmail(email)}";

            var profile = new LlmProviderProfile
            {
                Id = "codex-chatgpt",
                Name = "Codex (ChatGPT)",
                Protocol = "openai-responses",
                BaseUrl = "https://chatgpt.com/backend-api",
                AuthKind = "bearer",
                ChatPath = "/codex/responses",
                ModelsPath = $"/codex/models?client_version={ClientVersion}",
                PrivacyNote = "Запросы идут через бэкенд подписки ChatGPT — промпты видны оператору OpenAI.",
                // Бэкенд подписки отвечает ТОЛЬКО потоком: stream=false → 400
                // «Stream must be set to true».
                RequiresStreaming = true,
            };
            // Модель по умолчанию — та, что выбрана в конфиге Codex на этой машине
            // (~/.codex/config.toml, model = "…"); список моделей берётся динамически
            // у бэкенда, жёстко зашитых ID здесь нет.
            var configuredModel = LocalProviderDiscovery.ReadCodexConfiguredModel(options.HomeDirectory ?? "");
            if (!string.IsNullOrWhiteSpace(configuredModel))
                profile.ModelPresets.Add(configuredModel);
            if (!string.IsNullOrWhiteSpace(accountId))
                profile.ExtraHeaders.Add(new KeyValuePair<string, string>("chatgpt-account-id", accountId));
            profile.ExtraHeaders.Add(new KeyValuePair<string, string>("OpenAI-Beta", "responses=experimental"));
            // originator/version/session-заголовки - как у клиента, которым подписка
            // пользуется на этой машине (oh-my-pi, packages/ai/src/providers/openai-codex-responses.ts).
            profile.ExtraHeaders.Add(new KeyValuePair<string, string>("originator", "omp"));
            profile.ExtraHeaders.Add(new KeyValuePair<string, string>("version", ClientVersion));
            profile.ExtraHeaders.Add(new KeyValuePair<string, string>("session_id", "{session}"));
            profile.ExtraHeaders.Add(new KeyValuePair<string, string>("conversation_id", "{session}"));
            profile.ExtraHeaders.Add(new KeyValuePair<string, string>("x-client-request-id", "{session}"));
            profile.ExtraHeaders.Add(new KeyValuePair<string, string>("x-codex-routing-hint", "model={model}"));

            var refreshToken = tokens.ValueKind == JsonValueKind.Object
                ? LocalProviderDiscovery.Str(tokens, "refresh_token") : "";

            var detail = $"Файл: {path} · endpoint: {profile.BaseUrl}/codex/responses";
            var canApply = LlmProtocolSupport.IsSupported(profile.Protocol);
            var step = new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Found, summary, detail,
                profile, accessToken, canApply, refreshToken);
            return canApply
                ? step
                : step with
                {
                    Summary = summary,
                    Detail = detail + " · протокол Responses пока не поддержан клиентом Buddy — ключ сохранён, подключение вручную",
                };
        }
    }
}

/// <summary>OpenCode Go: ключ из ~/.local/share/opencode/auth.json (его пишет opencode).</summary>
internal sealed class OpenCodeGoProbe : ILocalProviderProbe
{
    public string Id => "opencode-go";
    public string DisplayName => "OpenCode Go";
    public string Expectation => "файл ~/.local/share/opencode/auth.json (или переменная OPENCODE_API_KEY)";

    public async Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var envKey = options.GetEnvironmentVariable("OPENCODE_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return Found("Ключ в переменной окружения OPENCODE_API_KEY", "Переменная окружения OPENCODE_API_KEY", envKey);

        var path = options.OpenCodeAuthPathOverride
                   ?? LocalProviderDiscovery.HomePath(options, ".local", "share", "opencode", "auth.json");
        var (exists, doc, error) = await LocalProviderDiscovery.TryReadJsonAsync(path, ct);
        if (!exists)
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Файл авторизации не найден", $"Ожидался: {path}");
        if (doc == null)
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Failed,
                "Файл не читается", $"{path}: {error}");

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("opencode-go", out var entry) && entry.ValueKind == JsonValueKind.Object)
            {
                var key = LocalProviderDiscovery.Str(entry, "key");
                if (!string.IsNullOrWhiteSpace(key))
                    return Found("Ключ подписки opencode-go найден", $"Файл: {path}", key);
            }
            // Файл есть, но записи opencode-go нет — покажем, какие провайдеры там есть.
            var ids = new List<string>();
            foreach (var prop in root.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Object && LocalProviderDiscovery.Str(prop.Value, "key").Length > 0)
                    ids.Add(prop.Name);
            var detail = ids.Count > 0
                ? $"Файл: {path} · в нём ключи: {string.Join(", ", ids)}"
                : $"Файл: {path} · записи opencode-go нет";
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Ключ opencode-go не найден", detail);
        }
    }

    private LocalDiscoveryStep Found(string summary, string detail, string key)
    {
        var profile = LlmProviderRegistry.BuiltIn.FirstOrDefault(p => p.Id == "opencode-go");
        return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Found, summary, detail,
            profile?.Clone(), key, CanApply: true);
    }
}

/// <summary>Ключ OpenAI из переменной окружения или ~/.codex/auth.json.</summary>
internal sealed class OpenAiKeyProbe : ILocalProviderProbe
{
    public string Id => "openai";
    public string DisplayName => "OpenAI API";
    public string Expectation => "переменная окружения OPENAI_API_KEY";

    public async Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var envKey = options.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return Found("Ключ в переменной окружения OPENAI_API_KEY", "Переменная окружения OPENAI_API_KEY", envKey);

        var path = LocalProviderDiscovery.HomePath(options, ".codex", "auth.json");
        var (exists, doc, error) = await LocalProviderDiscovery.TryReadJsonAsync(path, ct);
        if (!exists || doc == null)
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Ключ не найден", $"Ожидалась переменная OPENAI_API_KEY или запись в {path}{(error == null ? "" : $" ({error})")}");

        using (doc)
        {
            var key = LocalProviderDiscovery.Str(doc.RootElement, "OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(key))
                return Found("Ключ API найден в ~/.codex/auth.json", $"Файл: {path}", key);
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Ключ API не найден", $"В {path} запись OPENAI_API_KEY пуста");
        }
    }

    private static LocalDiscoveryStep Found(string summary, string detail, string key)
    {
        var profile = LlmProviderRegistry.BuiltIn.FirstOrDefault(p => p.Id == "openai");
        return new LocalDiscoveryStep("openai", "OpenAI API", LocalDiscoveryState.Found, summary, detail,
            profile?.Clone(), key, CanApply: true);
    }
}

/// <summary>Ключ Gemini API (Google AI Studio) — есть OpenAI-совместимый эндпоинт.</summary>
internal sealed class GeminiApiKeyProbe : ILocalProviderProbe
{
    public string Id => "gemini";
    public string DisplayName => "Google Gemini API";
    public string Expectation => "переменные окружения GEMINI_API_KEY / GOOGLE_API_KEY";

    public Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var key = options.GetEnvironmentVariable("GEMINI_API_KEY");
        var varName = "GEMINI_API_KEY";
        if (string.IsNullOrWhiteSpace(key))
        {
            key = options.GetEnvironmentVariable("GOOGLE_API_KEY");
            varName = "GOOGLE_API_KEY";
        }
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Ключ не найден", "Ожидались переменные окружения GEMINI_API_KEY или GOOGLE_API_KEY"));

        var profile = new LlmProviderProfile
        {
            Id = "gemini-api",
            Name = "Google Gemini API",
            Protocol = "openai-chat",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
            AuthKind = "bearer",
            PrivacyNote = "Запросы уходят в Google AI Studio — промпты видны оператору Google.",
            ModelPresets = { "gemini-2.5-pro", "gemini-2.5-flash" },
        };
        return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Found,
            "Ключ найден", $"Переменная окружения {varName}", profile, key, CanApply: true));
    }
}

/// <summary>Gemini CLI (google-gemini-cli): свой OAuth-файл, но API — Cloud Code
/// (v1internal), а не OpenAI-совместимый эндпоинт.</summary>
internal sealed class GeminiCliProbe : ILocalProviderProbe
{
    public string Id => "gemini-cli";
    public string DisplayName => "Gemini CLI";
    public string Expectation => "файл ~/.gemini/oauth_creds.json";

    public async Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var path = LocalProviderDiscovery.HomePath(options, ".gemini", "oauth_creds.json");
        var (exists, doc, error) = await LocalProviderDiscovery.TryReadJsonAsync(path, ct);
        if (!exists)
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Не найден", $"Ожидался файл: {path}{(error == null ? "" : $" ({error})")}");
        if (doc == null)
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Failed,
                "Файл не читается", $"{path}: {error}");

        using (doc)
        {
            var root = doc.RootElement;
            var token = LocalProviderDiscovery.Str(root, "access_token");
            var email = LocalProviderDiscovery.Str(root, "email");
            var who = string.IsNullOrWhiteSpace(email) ? "" : $" · {LocalProviderDiscovery.MaskEmail(email)}";
            var tokenMark = string.IsNullOrWhiteSpace(token)
                ? "без access_token"
                : $"токен {token[..Math.Min(8, token.Length)]}…";
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Unsupported,
                "Установлен, но протокол не поддерживается",
                $"{path} · {tokenMark}{who} · внутренний API Cloud Code (v1internal) недоступен для OpenAI-совместимого клиента Buddy");
        }
    }
}

/// <summary>Ключ Groq из переменной окружения.</summary>
internal sealed class GroqKeyProbe : ILocalProviderProbe
{
    public string Id => "groq";
    public string DisplayName => "Groq";
    public string Expectation => "переменная окружения GROQ_API_KEY";

    public Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var key = options.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Ключ не найден", "Ожидалась переменная окружения GROQ_API_KEY"));
        var profile = LlmProviderRegistry.BuiltIn.FirstOrDefault(p => p.Id == "groq");
        return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Found,
            "Ключ найден", "Переменная окружения GROQ_API_KEY", profile?.Clone(), key, CanApply: true));
    }
}

/// <summary>Ключ OpenRouter из переменной окружения.</summary>
internal sealed class OpenRouterKeyProbe : ILocalProviderProbe
{
    public string Id => "openrouter";
    public string DisplayName => "OpenRouter";
    public string Expectation => "переменная окружения OPENROUTER_API_KEY";

    public Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var key = options.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Ключ не найден", "Ожидалась переменная окружения OPENROUTER_API_KEY"));
        var profile = LlmProviderRegistry.BuiltIn.FirstOrDefault(p => p.Id == "openrouter");
        return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Found,
            "Ключ найден", "Переменная окружения OPENROUTER_API_KEY", profile?.Clone(), key, CanApply: true));
    }
}

/// <summary>Ключ NVIDIA NIM из переменной окружения.</summary>
internal sealed class NvidiaNimKeyProbe : ILocalProviderProbe
{
    public string Id => "nvidia-nim";
    public string DisplayName => "NVIDIA NIM";
    public string Expectation => "переменные окружения NVIDIA_API_KEY / NVIDIA_NIM_API_KEY";

    public Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var key = options.GetEnvironmentVariable("NVIDIA_API_KEY");
        var varName = "NVIDIA_API_KEY";
        if (string.IsNullOrWhiteSpace(key))
        {
            key = options.GetEnvironmentVariable("NVIDIA_NIM_API_KEY");
            varName = "NVIDIA_NIM_API_KEY";
        }
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Ключ не найден", "Ожидались переменные окружения NVIDIA_API_KEY или NVIDIA_NIM_API_KEY"));
        var profile = LlmProviderRegistry.BuiltIn.FirstOrDefault(p => p.Id == "nvidia-nim");
        return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Found,
            "Ключ найден", $"Переменная окружения {varName}", profile?.Clone(), key, CanApply: true));
    }
}

/// <summary>Локальный сервер LM Studio (http://localhost:1234/v1).</summary>
internal sealed class LmStudioProbe : ILocalProviderProbe
{
    public string Id => "lmstudio";
    public string DisplayName => "LM Studio (локально)";
    public string Expectation => "сервер http://localhost:1234/v1";

    public async Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        if (!options.ProbeLocalServers)
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Проверка локальных серверов отключена", "Локальные серверы не опрашиваются");

        var profile = LlmProviderRegistry.BuiltIn.FirstOrDefault(p => p.Id == "lmstudio");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, options.HttpTimeoutSeconds)) };
            using var resp = await http.GetAsync("http://localhost:1234/v1/models", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Found,
                    "Сервер отвечает", "http://localhost:1234/v1 · модели доступны локально",
                    profile?.Clone(), null, CanApply: true);
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                $"Сервер ответил {(int)resp.StatusCode}", "http://localhost:1234/v1/models");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Сервер не отвечает", "Ожидался: http://localhost:1234/v1");
        }
    }
}

/// <summary>Devin CLI установлен, но его API — Connect/protobuf, Buddy его не понимает.</summary>
internal sealed class DevinCliProbe : ILocalProviderProbe
{
    public string Id => "devin";
    public string DisplayName => "Devin CLI";
    public string Expectation => "каталог ~/.devin";

    public Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var path = LocalProviderDiscovery.HomePath(options, ".devin");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Не установлен", $"Ожидался каталог: {path}"));
        return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Unsupported,
            "Установлен, но протокол не поддерживается",
            $"{path} · Devin CLI говорит по Connect/protobuf, Buddy работает с OpenAI-совместимыми API"));
    }
}

/// <summary>Cursor установлен, но его API — protobuf, Buddy его не понимает.</summary>
internal sealed class CursorProbe : ILocalProviderProbe
{
    public string Id => "cursor";
    public string DisplayName => "Cursor";
    public string Expectation => "каталог ~/.cursor";

    public Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var path = LocalProviderDiscovery.HomePath(options, ".cursor");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Не установлен", $"Ожидался каталог: {path}"));
        return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Unsupported,
            "Установлен, но протокол не поддерживается",
            $"{path} · Cursor использует своё protobuf-приложение, Buddy работает с OpenAI-совместимыми API"));
    }
}

/// <summary>Factory Droid установлен, но его прокси-протокол Buddy не понимает.</summary>
internal sealed class FactoryDroidProbe : ILocalProviderProbe
{
    public string Id => "factory";
    public string DisplayName => "Factory Droid";
    public string Expectation => "каталог ~/.factory";

    public Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var path = LocalProviderDiscovery.HomePath(options, ".factory");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Не установлен", $"Ожидался каталог: {path}"));
        return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Unsupported,
            "Установлен, но протокол не поддерживается",
            $"{path} · Factory Droid использует собственный прокси-протокол, Buddy работает с OpenAI-совместимыми API"));
    }
}

/// <summary>Antigravity/Gemini CLI: внутренний диалект Google, Buddy его не понимает.</summary>
internal sealed class AntigravityProbe : ILocalProviderProbe
{
    public string Id => "antigravity";
    public string DisplayName => "Antigravity / Gemini CLI";
    public string Expectation => "каталог ~/.gemini/antigravity-cli";

    public Task<LocalDiscoveryStep> ProbeAsync(LocalDiscoveryOptions options, CancellationToken ct)
    {
        var path = LocalProviderDiscovery.HomePath(options, ".gemini", "antigravity-cli");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Missing,
                "Не установлен", $"Ожидался каталог: {path}"));
        return Task.FromResult(new LocalDiscoveryStep(Id, DisplayName, LocalDiscoveryState.Unsupported,
            "Установлен, но протокол не поддерживается",
            $"{path} · внутренний API Cloud Code (v1internal) недоступен для OpenAI-совместимого клиента Buddy"));
    }
}

/// <summary>
/// Какие протоколы умеет текущий клиент Buddy. Используется сканером: найденный
/// источник помечается «можно подключить» только если Buddy умеет говорить с ним.
/// </summary>
public static class LlmProtocolSupport
{
    /// <summary>Протоколы, которые реально реализованы в OpenAiClient.</summary>
    public static readonly IReadOnlyList<string> Supported = new[] { "openai-chat", "openai-responses" };

    /// <summary>Умеет ли клиент говорить по указанному протоколу.</summary>
    public static bool IsSupported(string protocol)
        => Supported.Contains(protocol ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>Человеческое объяснение, почему протокол не работает.</summary>
    public static string UnsupportedReason(string protocol) => protocol switch
    {
        "openai-responses" => "Buddy говорит с бэкендом по протоколу Responses API (/responses)",
        "anthropic" => "Buddy отправляет запросы по /chat/completions и /responses; у Anthropic свой формат /messages",
        _ => $"протокол «{protocol}» клиентом не поддерживается",
    };
}
