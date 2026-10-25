using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Images;

/// <summary>
/// Определение поддержки изображений моделью (ТЗ §8). Источники:
/// каталог провайдера (OpenRouter input_modalities / Ollama /api/show capabilities),
/// явный пробой синтетической картинкой, зафиксированный вердикт боевого запроса.
/// Ничего не ходит в сеть из GetCached; каталог — с рейт-лимитом раз в 24ч на
/// scope «provider|baseUrl». Сетевые ошибки любого шага → Unknown, без исключений.
/// </summary>
public sealed class ImageCapabilityService : IImageCapabilityService
{
    private static readonly TimeSpan CatalogMinInterval = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly IImageCapabilityCache _cache;
    private readonly Func<ApiConfig> _config;
    private readonly LoggingService? _log;
    // Per-client session-id для "{session}"-плейсхолдеров ExtraHeaders — как в OpenAiClient.
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    public ImageCapabilityService(HttpClient http, IImageCapabilityCache cache,
        Func<ApiConfig> config, LoggingService? logger = null)
    {
        _http = http;
        _cache = cache;
        _config = config;
        _log = logger;
    }

    /// <summary>Ключ возможностей из текущих настроек (для UI/транспорта):
    /// провайдер/endpoint/модель/протокол + версия авторизации — смена ключа
    /// или набора extra-заголовков делает чужой вердикт неприменимым.</summary>
    public static ImageCapabilityKey KeyFor(ApiConfig api)
    {
        var profile = LlmProviderRegistry.Resolve(api);
        var model = NormalizeModelId(api.ChatModel);
        var auth = profile.AuthKind
            + (string.IsNullOrEmpty(api.ApiKey) ? ":none" : ":key")
            + (profile.ExtraHeaders.Count > 0 ? ":hdr" + profile.ExtraHeaders.Count : "");
        return new ImageCapabilityKey(profile.Id, api.BaseUrl ?? "", model, profile.Protocol, auth);
    }

    public ImageSupportInfo GetCached(ImageCapabilityKey key)
        => _cache.Get(key.CacheKey()) ?? new ImageSupportInfo(ImageSupportState.Unknown, ImageSupportSource.None, DateTime.MinValue, null);

    public async Task<ImageSupportInfo> RefreshCatalogAsync(ImageCapabilityKey key, bool force, CancellationToken ct)
    {
        var scope = key.ProviderId + "|" + key.BaseUrlNormalized;
        if (!force && _cache.CatalogRefreshedUtc(scope) is DateTime last &&
            DateTime.UtcNow - last < CatalogMinInterval)
            return GetCached(key); // каталог не дёргаем чаще раза в сутки
        _cache.MarkCatalogRefreshed(scope);

        var info = new ImageSupportInfo(ImageSupportState.Unknown, ImageSupportSource.ProviderCatalog, DateTime.UtcNow, null);
        try
        {
            ApplyHeaders();
            info = key.ProviderId switch
            {
                "openrouter" => await OpenRouterCatalogAsync(key, ct).ConfigureAwait(false),
                "ollama" => await OllamaShowAsync(key, ct).ConfigureAwait(false),
                _ => info, // у прочих openai-chat провайдеров каталога модальностей нет
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Info($"ImageCapability: catalog refresh failed ({key.ProviderId}): {ex.Message}", "Ai");
        }
        _cache.Set(key.CacheKey(), info, KeyJson(key));
        return info;
    }

    public async Task<ImageSupportInfo> ProbeAsync(ImageCapabilityKey key, CancellationToken ct)
    {
        ImageSupportInfo info;
        try
        {
            info = await ProbeCoreAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            info = ProbeUnknown($"probe error: {ex.Message}");
        }
        _cache.Set(key.CacheKey(), info, KeyJson(key));
        return info;
    }

    /// <summary>Проба боевым запросом с синтетической картинкой (ТЗ §8): явный
    /// отказ на изображение → Unsupported; правильный ответ «2 и 1» → Supported;
    /// таймаут/сеть/пустой или неверный текст → Unknown «не удалось подтвердить».</summary>
    private async Task<ImageSupportInfo> ProbeCoreAsync(ImageCapabilityKey key, CancellationToken ct)
    {
        var api = _config();
        var profile = LlmProviderRegistry.Resolve(api);
        if (profile.Protocol != "openai-chat" || string.IsNullOrEmpty(profile.ChatPath))
            return ProbeUnknown($"протокол {profile.Protocol}: чат-запроса с картинкой нет");

        ApplyHeaders();
        var url = EndpointUrl(api, profile, profile.ChatPath);
        object content = ChatImageTransport.BuildUserContent(
            "Сколько прямоугольников и кругов на картинке? Ответь двумя числами.",
            new[] { new ChatImagePayload("probe", "image/png", ProbePng, 160, 120) });
        var req = new Dictionary<string, object>
        {
            ["model"] = NormalizeModelId(api.ChatModel),
            ["messages"] = new object[]
            {
                new { role = "system", content = "Ты точно считаешь фигуры на изображении." },
                new { role = "user", content },
            },
            ["temperature"] = 0.0,
            ["max_tokens"] = 32,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        var rct = cts.Token;

        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.PostAsJsonAsync(url, req, JsonOpts, rct).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(rct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ProbeUnknown(ex is OperationCanceledException ? "таймаут 45с" : $"сеть: {ex.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var (code, type, msg) = ParseApiError(body);
                var verdict = ImageApiErrorClassifier.Classify((int)response.StatusCode, code, type, msg);
                return verdict == ImageApiVerdict.ExplicitImageRejection
                    ? new ImageSupportInfo(ImageSupportState.Unsupported, ImageSupportSource.ExplicitProbe, DateTime.UtcNow,
                        $"API отказал на изображение: {code ?? type ?? ((int)response.StatusCode).ToString()}")
                    : ProbeUnknown($"HTTP {(int)response.StatusCode}: без явного отказа на изображение");
            }
            var answer = ExtractChatText(body, profile);
            if (answer != null && AnswerMatches(answer))
                return new ImageSupportInfo(ImageSupportState.Supported, ImageSupportSource.ExplicitProbe, DateTime.UtcNow, null);
            return ProbeUnknown(answer == null ? "пустой ответ" : $"ответ не похож на «2 прямоугольника, 1 круг»: «{Truncate(answer, 80)}»");
        }
    }

    /// <summary>OpenRouter: GET {base}/models → точный modelId →
    /// architecture.input_modalities содержит "image". Модели нет/поля нет → Unknown.</summary>
    private async Task<ImageSupportInfo> OpenRouterCatalogAsync(ImageCapabilityKey key, CancellationToken ct)
    {
        var api = _config();
        var profile = LlmProviderRegistry.Resolve(api);
        var url = EndpointUrl(api, profile, profile.ModelsPath);
        using var r = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode)
            return new ImageSupportInfo(ImageSupportState.Unknown, ImageSupportSource.ProviderCatalog,
                DateTime.UtcNow, $"models ← HTTP {(int)r.StatusCode}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return CatalogUnknown("в ответе /models нет массива data");
        foreach (var m in data.EnumerateArray())
        {
            if (!m.TryGetProperty("id", out var id) || id.GetString() != key.ModelId)
                continue;
            // Модель есть: смотрим заявленные входные модальности.
            if (!m.TryGetProperty("architecture", out var arch) ||
                !arch.TryGetProperty("input_modalities", out var mods) ||
                mods.ValueKind != JsonValueKind.Array)
                return CatalogUnknown("у модели нет architecture.input_modalities");
            var hasImage = mods.EnumerateArray().Any(x =>
                string.Equals(x.GetString(), "image", StringComparison.OrdinalIgnoreCase));
            return new ImageSupportInfo(
                hasImage ? ImageSupportState.Supported : ImageSupportState.Unsupported,
                ImageSupportSource.ProviderCatalog, DateTime.UtcNow,
                hasImage ? null : "в input_modalities нет \"image\"");
        }
        return CatalogUnknown($"модель «{key.ModelId}» не найдена в каталоге");
    }

    /// <summary>Ollama: POST {base-без-/v1}/api/show {"model": id} →
    /// capabilities[] содержит "vision". Поля нет → Unknown.</summary>
    private async Task<ImageSupportInfo> OllamaShowAsync(ImageCapabilityKey key, CancellationToken ct)
    {
        var api = _config();
        var profile = LlmProviderRegistry.Resolve(api);
        var baseUrl = EndpointUrl(api, profile, "").TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3];
        var url = baseUrl + "/api/show";
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = key.ModelId }),
                Encoding.UTF8, "application/json")
        };
        using var r = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode)
            return new ImageSupportInfo(ImageSupportState.Unknown, ImageSupportSource.ProviderCatalog,
                DateTime.UtcNow, $"api/show ← HTTP {(int)r.StatusCode}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("capabilities", out var caps) ||
            caps.ValueKind != JsonValueKind.Array)
            return CatalogUnknown("в api/show нет capabilities[]");
        var hasVision = caps.EnumerateArray().Any(x =>
            string.Equals(x.GetString(), "vision", StringComparison.OrdinalIgnoreCase));
        return new ImageSupportInfo(
            hasVision ? ImageSupportState.Supported : ImageSupportState.Unsupported,
            ImageSupportSource.ProviderCatalog, DateTime.UtcNow,
            hasVision ? null : "в capabilities нет \"vision\"");
    }

    public void RecordApiResult(ImageCapabilityKey key, ImageApiVerdict verdict, string? note)
    {
        switch (verdict)
        {
            case ImageApiVerdict.ExplicitImageRejection:
                _cache.Set(key.CacheKey(),
                    new ImageSupportInfo(ImageSupportState.Unsupported, ImageSupportSource.ApiRefusal,
                        DateTime.UtcNow, note), KeyJson(key));
                break;
            case ImageApiVerdict.Accepted:
                // Принятый API запрос с картинкой — подтверждение транспорта (ТЗ §8).
                _cache.Set(key.CacheKey(),
                    new ImageSupportInfo(ImageSupportState.Supported, ImageSupportSource.ApiRefusal,
                        DateTime.UtcNow, note ?? "accepted-request"), KeyJson(key));
                break;
            // Inconclusive — ничего не записываем.
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void ApplyHeaders() => LlmRequestHeaders.Apply(_http, _config(), _sessionId);

    private static ImageSupportInfo CatalogUnknown(string note)
        => new(ImageSupportState.Unknown, ImageSupportSource.ProviderCatalog, DateTime.UtcNow, note);

    private static ImageSupportInfo ProbeUnknown(string note)
        => new(ImageSupportState.Unknown, ImageSupportSource.ExplicitProbe, DateTime.UtcNow,
            "Поддержку не удалось подтвердить" + (string.IsNullOrEmpty(note) ? "" : $": {note}"));

    private static string KeyJson(ImageCapabilityKey key) => JsonSerializer.Serialize(key, JsonOpts);

    /// <summary>Тот же URL-контракт, что у OpenAiClient: конфиг BaseUrl (или
    /// профильный) + путь; пустой path → база без слеша.</summary>
    private static string EndpointUrl(ApiConfig api, LlmProviderProfile profile, string path)
    {
        var baseUrl = string.IsNullOrWhiteSpace(api.BaseUrl) ? profile.BaseUrl : api.BaseUrl;
        var b = baseUrl.TrimEnd('/');
        return string.IsNullOrEmpty(path) ? b : b + "/" + path.TrimStart('/');
    }

    private static string NormalizeModelId(string model)
        => string.Concat((model ?? "").Trim().Where(c =>
            c != '​' && c != '‎' && c != '‏' && c != (char)0xFEFF && c != ' '));

    /// <summary>Ответ на пробу «сколько прямоугольников и кругов»: должны быть
    /// числа 2 и 1 (картинка: 2 прямоугольника + 1 круг).</summary>
    private static bool AnswerMatches(string answer)
    {
        var hasTwo = false;
        var hasOne = false;
        foreach (var ch in answer)
        {
            if (ch == '2') hasTwo = true;
            else if (ch == '1') hasOne = true;
        }
        return hasTwo && hasOne;
    }

    /// <summary>Текст ответа чат-запроса по ContentPath профиля (как OpenAiClient).</summary>
    private static string? ExtractChatText(string body, LlmProviderProfile profile)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var env) && env.ValueKind == JsonValueKind.Object)
                root = env;
            var el = root;
            foreach (var seg in (profile.ContentPath ?? "choices[0].message.content").Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = seg;
                var bracket = seg.IndexOf('[');
                int? index = null;
                if (bracket >= 0)
                {
                    name = seg[..bracket];
                    if (int.TryParse(seg[(bracket + 1)..seg.IndexOf(']', bracket)], out var ix)) index = ix;
                }
                var node = el;
                if (name.Length > 0)
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node))
                        return null;
                }
                el = index.HasValue
                    ? (node.ValueKind == JsonValueKind.Array && index.Value < node.GetArrayLength()
                        ? node[index.Value] : default)
                    : node;
                if (index.HasValue && el.ValueKind == JsonValueKind.Undefined) return null;
            }
            return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>(code, type, message) из тела ошибки API: {error:{code,type,message}}
    /// (openai) / {error:"строка"} / плоские поля.</summary>
    internal static (string? Code, string? Type, string? Message) ParseApiError(string? body)
    {
        if (string.IsNullOrEmpty(body)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String)
                    return (null, null, err.GetString());
                if (err.ValueKind == JsonValueKind.Object)
                    return (TryStr(err, "code") ?? TryStr(err, "param"),
                            TryStr(err, "type"), TryStr(err, "message"));
            }
            if (root.ValueKind == JsonValueKind.Object)
            {
                var msg = TryStr(root, "message") ?? TryStr(root, "error_description");
                if (msg != null) return (null, TryStr(root, "type"), msg);
            }
        }
        catch (JsonException) { }
        return (null, null, null);
    }

    private static string? TryStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Синтетическая PNG 160x120: белый фон, 2 цветных прямоугольника,
    /// 1 круг (генерится кодом — см. историю фичи; WIC из Core недоступен).</summary>
    private static readonly byte[] ProbePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAKAAAAB4CAIAAAD6wG44AAACXklEQVR4nO3cMW5aQRhF4cFK5UWkYQm09gpSeQdZRBq3btx4L66ygtCyBG/jb0kXuYhA8uMx/z2cU2MJ6dOdh4SZzfF4HMbtbvYbsHUTGJ7A8ASGJzA8geEJDE9geALDExiewPAEhicwPIHhCQxPYHgCwxMYnsDwvi3544/dbgS2PRzGzeSC4QkMT2B4AsMTGJ7A8ASGJzA8geEJDE9geALDExiewPAEhicwPIHhCQxPYHibJfdkRfxP1s+X76df8OfH++DGBD6LejvYNOAv01KZIcAXcUVKEz5kraQ7xnj8/TTCy17werSYKQcv+Gq6I3nKqcDX1I02jgS+vm6ucR7wLN1Q4zDgubqJxknAHXTjjGOA++hmGWcAd9MNMs4ANjJwz/mmjLg7cGfdCOPuwLYwgeG1Bu5/Pvc/pRfdk2X/2j1/jHkdXrerfB98s7P4b7V/G/2AWx/RtjyB4QkMrylw3AN4jHH/8Gv0qymwXSqB4QkMT2B4AsMTGJ7A8ASGJzA8geE1BU78uWbN+7owD9gulcDwBIbXFzjrMVwtH8CtgY0PnDLi6jrf7sC2PIHhdQfuf0pX4/M5ALi5cfXWzQA2PnDPEVf7+cYANzSuBN0k4FbGFaIbBtzEuHJ084CnG1eUbiTwRONK000FnmJcgbrBwFc2rkzd1lc4NPmtaSXQwq9wWG/KlaDLX/DF11xprpG37ExhrjTas8DMe7I+H9qP57A/v3i3n3nd1RoxF2yoD1l2IoHhCQxPYHgCwxMYnsDwBIYnMDyB4QkMT2B4AsMTGJ7A8ASGJzA8gQe7vxP3q6dD7XoqAAAAAElFTkSuQmCC");
}
