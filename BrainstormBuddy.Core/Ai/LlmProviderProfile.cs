using System.Net.Http;
using System.Net.Http.Headers;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Ai;

/// <summary>
/// Профиль LLM-провайдера: откуда слать запросы, как авторизоваться и где в ответе
/// искать текст. Встроенные профили зашиты в <see cref="LlmProviderRegistry"/>,
/// пользовательские хранятся в ApiConfig.CustomProviders (config.json).
/// </summary>
public sealed class LlmProviderProfile
{
    public string Id { get; set; } = "";            // "opencode-go", "ollama", "custom-…"
    public string Name { get; set; } = "";          // отображаемое
    public string Protocol { get; set; } = "openai-chat"; // "openai-chat" | "openai-responses" | "anthropic"
    public string BaseUrl { get; set; } = "";       // до /v1
    public string AuthKind { get; set; } = "bearer";    // "bearer" | "x-api-key" | "none"
    // Значение "{session}" → стабильный per-client Guid (кэш в клиенте, _sessionId).
    public List<KeyValuePair<string, string>> ExtraHeaders { get; set; } = new();
    public string ModelsPath { get; set; } = "/models";
    public string ChatPath { get; set; } = "/chat/completions";
    public string ContentPath { get; set; } = "choices[0].message.content";
    public string UsagePath { get; set; } = "usage";
    public List<string> ModelPresets { get; set; } = new();
    public string PrivacyNote { get; set; } = "";
    public string ReasoningEffort { get; set; } = "low"; // "" = не слать поле reasoning
    public bool IsBuiltIn { get; set; }

    public LlmProviderProfile Clone() => new()
    {
        Id = Id, Name = Name, Protocol = Protocol, BaseUrl = BaseUrl, AuthKind = AuthKind,
        ExtraHeaders = new List<KeyValuePair<string, string>>(ExtraHeaders),
        ModelsPath = ModelsPath, ChatPath = ChatPath, ContentPath = ContentPath,
        UsagePath = UsagePath, ModelPresets = new List<string>(ModelPresets),
        PrivacyNote = PrivacyNote, ReasoningEffort = ReasoningEffort, IsBuiltIn = IsBuiltIn
    };

    // DisplayMemberPath в комбо не всегда прокидывается в UIA-имя item'а (FlaUI видит тип).
    // ToString => Name делает профиль читаемым и в UIA, и в любых списках без DisplayMemberPath.
    public override string ToString() => Name;
}

public static class LlmProviderRegistry
{
    private static readonly LlmProviderProfile[] _builtIn =
    {
        new()
        {
            Id = "opencode-go", Name = "OpenCode Go", Protocol = "openai-chat",
            BaseUrl = "https://opencode.ai/zen/go/v1", AuthKind = "bearer",
            ExtraHeaders = { new KeyValuePair<string, string>("x-opencode-session", "{session}") },
            ModelPresets = { "space-bunny-free", "MiMo-V2.6-Flash", "deepseek-v4.1-flash" },
            PrivacyNote = "Запросы идут через шлюз opencode.ai — промпты и ответы видны оператору шлюза.",
            IsBuiltIn = true
        },
        new()
        {
            Id = "ollama", Name = "Ollama (local)", Protocol = "openai-chat",
            BaseUrl = "http://127.0.0.1:11434/v1", AuthKind = "none",
            ModelPresets = { "qwen2.5vl:7b", "qwen2.5vl:3b" },
            PrivacyNote = "Локальная модель — данные не покидают машину.",
            IsBuiltIn = true
        },
        new()
        {
            Id = "lmstudio", Name = "LM Studio (local)", Protocol = "openai-chat",
            BaseUrl = "http://localhost:1234/v1", AuthKind = "none",
            PrivacyNote = "Локальная модель — данные не покидают машину.",
            IsBuiltIn = true
        },
        new()
        {
            Id = "openrouter", Name = "OpenRouter", Protocol = "openai-chat",
            BaseUrl = "https://openrouter.ai/api/v1", AuthKind = "bearer",
            ModelPresets = { "nvidia/nemotron-3.5-lightning:free", "google/gemma-4-26b-a4b-it:free", "openrouter/free" },
            PrivacyNote = "Запросы идут через OpenRouter к выбранному провайдеру модели.",
            IsBuiltIn = true
        },
        new()
        {
            Id = "groq", Name = "Groq", Protocol = "openai-chat",
            BaseUrl = "https://api.groq.com/openai/v1", AuthKind = "bearer",
            ModelPresets = { "llama-3.3-70b-versatile", "llama3-8b-8192" },
            PrivacyNote = "Запросы уходят в облако Groq.",
            IsBuiltIn = true
        },
        new()
        {
            Id = "openai", Name = "OpenAI", Protocol = "openai-chat",
            BaseUrl = "https://api.openai.com/v1", AuthKind = "bearer",
            ModelPresets = { "gpt-4o-mini", "gpt-4o" },
            PrivacyNote = "Запросы уходят в облако OpenAI.",
            IsBuiltIn = true
        },
        new()
        {
            Id = "nvidia-nim", Name = "NVIDIA NIM", Protocol = "openai-chat",
            BaseUrl = "https://integrate.api.nvidia.com/v1", AuthKind = "bearer",
            ModelPresets = { "meta/llama-3.1-70b-instruct" },
            PrivacyNote = "Запросы уходят в облако NVIDIA.",
            IsBuiltIn = true
        },
        new()
        {
            Id = "localai", Name = "LocalAI (local)", Protocol = "openai-chat",
            BaseUrl = "http://127.0.0.1:8080/v1", AuthKind = "none",
            PrivacyNote = "Локальная модель — данные не покидают машину.",
            IsBuiltIn = true
        },
        // Псевдо-профиль «свой сервер»: выбор в комбо открывает редактор провайдера
        // (IsBuiltIn=false → UI показывает LlmProviderEditPanel). Запись в CustomProviders
        // с тем же Id ("custom") затеняет эту заглушку — см. All()/FindById.
        new()
        {
            Id = "custom", Name = "Custom (свой сервер)", Protocol = "openai-chat",
            BaseUrl = "", AuthKind = "bearer",
            ModelPresets = { },
            PrivacyNote = "Запросы идут на указанный вами сервер.",
            IsBuiltIn = false
        }
    };

    public static IReadOnlyList<LlmProviderProfile> BuiltIn => _builtIn;

    // Сначала пользовательские (могут переопределять), потом встроенные.
    public static LlmProviderProfile? FindById(string id, IEnumerable<LlmProviderProfile>? custom = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return (custom ?? Enumerable.Empty<LlmProviderProfile>())
            .Concat(_builtIn)
            .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // Встроенные + пользовательские; дедуп по Id: запись из CustomProviders с тем же Id
    // затеняет встроенную (занимает её позицию), как в FindById.
    public static IReadOnlyList<LlmProviderProfile> All(IEnumerable<LlmProviderProfile>? custom)
    {
        var customs = custom?.ToList() ?? new List<LlmProviderProfile>();
        var list = new List<LlmProviderProfile>(_builtIn.Length + customs.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in _builtIn)
        {
            var shadow = customs.FirstOrDefault(p => string.Equals(p.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            var p = shadow ?? b;
            if (seen.Add(p.Id)) list.Add(p);
        }
        foreach (var c in customs)
            if (seen.Add(c.Id)) list.Add(c);
        return list;
    }

    /// <summary>Профиль для конфига: по ProviderId (включая custom-профили юзера),
    /// а если не нашли — синтетический профиль из BaseUrl (старое поведение:
    /// Bearer + стандартные /models и /chat/completions). Псевдо-профиль реестра
    /// "custom" имеет пустой BaseUrl — подставляем BaseUrl из конфига, иначе запросы
    /// ушли бы на пустой адрес (раньше FindById("custom") возвращал null → FromBaseUrl).</summary>
    public static LlmProviderProfile Resolve(ApiConfig api)
    {
        var p = FindById(api.ProviderId, api.CustomProviders)
                ?? FindById(InferProviderId(api.BaseUrl));
        if (p != null)
        {
            if (string.IsNullOrWhiteSpace(p.BaseUrl) && !string.IsNullOrWhiteSpace(api.BaseUrl))
            {
                p = p.Clone();
                p.BaseUrl = api.BaseUrl;
            }
            return p;
        }
        return FromBaseUrl(api.BaseUrl);
    }

    public static LlmProviderProfile FromBaseUrl(string baseUrl) => new()
    {
        Id = "custom",
        Name = "Custom",
        Protocol = "openai-chat",
        BaseUrl = baseUrl,
        AuthKind = "bearer"
    };

    /// <summary>Эвристика ProviderId по BaseUrl — для бэкфилла при миграции старых конфигов.</summary>
    public static string InferProviderId(string baseUrl)
    {
        var u = (baseUrl ?? "").ToLowerInvariant();
        if (u.Contains("opencode.ai")) return "opencode-go";
        if (u.Contains("api.openai.com")) return "openai";
        if (u.Contains("openrouter.ai")) return "openrouter";
        if (u.Contains("groq.com")) return "groq";
        if (u.Contains("integrate.api.nvidia.com")) return "nvidia-nim";
        if (u.Contains(":11434")) return "ollama";
        if (u.Contains(":1234")) return "lmstudio";
        return "custom";
    }
}

/// <summary>
/// Единственное место, где ставятся Authorization/extra-заголовки LLM-запросов
/// (OpenAiClient и AgentOrchestrator делят эту логику). "{session}" в значениях
/// ExtraHeaders заменяется на sessionId — клиент генерит его один раз (per-client кэш).
/// </summary>
public static class LlmRequestHeaders
{
    public static void Apply(HttpClient http, ApiConfig api, string sessionId)
    {
        var profile = LlmProviderRegistry.Resolve(api);
        var headers = http.DefaultRequestHeaders;

        headers.Authorization = null;
        headers.Remove("x-api-key");
        switch (profile.AuthKind)
        {
            case "none":
                break;
            case "x-api-key":
                if (!string.IsNullOrEmpty(api.ApiKey))
                    headers.TryAddWithoutValidation("x-api-key", api.ApiKey);
                break;
            default: // "bearer"
                if (!string.IsNullOrEmpty(api.ApiKey))
                    headers.Authorization = new AuthenticationHeaderValue("Bearer", api.ApiKey);
                break;
        }

        foreach (var (name, value) in profile.ExtraHeaders)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            headers.Remove(name);
            headers.TryAddWithoutValidation(name, value.Replace("{session}", sessionId));
        }

        // Свой User-Agent — шлюзы вроде opencode.ai просят идентифицируемый клиент.
        if (headers.UserAgent.Count == 0)
            headers.TryAddWithoutValidation("User-Agent",
                $"BrainstormBuddy/{typeof(LlmRequestHeaders).Assembly.GetName().Version?.ToString(3) ?? "1.0"}");
    }
}
