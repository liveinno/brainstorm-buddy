using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Выбор LLM-клиента для memory-пайплайна по MemoryConfig.MemoryModelMode:
/// "same" → переданный основной клиент ассистента; "separate" → новый OpenAiClient
/// на профиль из MemoryProviderId (LlmProviderRegistry: custom провайдеры юзера
/// затеняют встроенные) с моделью MemoryModel; "disabled" → null.
/// Отдельной системы API keys у memory нет (спека): переиспользуется ApiKey
/// основного ApiConfig — подменяются только BaseUrl/ProviderId/ChatModel.
/// </summary>
public static class ResolveMemoryApiClient
{
    private const string LogCategory = "Memory";

    /// <summary>Фабрика. mainClient — боевой IApiClient ассистента (для режима "same").</summary>
    public static IApiClient? Resolve(AppConfig config, IApiClient? mainClient,
        LoggingService logger)
    {
        var mem = config.Memory;
        switch ((mem.MemoryModelMode ?? "same").Trim().ToLowerInvariant())
        {
            case "same":
                if (mainClient == null)
                    logger.Warn("Memory model mode=same, но основной IApiClient отсутствует", LogCategory);
                return mainClient;

            case "disabled":
                return null;

            case "separate":
                break; // ниже

            default:
                logger.Warn($"Unknown MemoryModelMode '{mem.MemoryModelMode}' — использую 'same'", LogCategory);
                return mainClient;
        }

        var profile = LlmProviderRegistry.FindById(mem.MemoryProviderId, config.Api.CustomProviders);
        if (profile == null)
        {
            logger.Warn($"Memory provider '{mem.MemoryProviderId}' не найден — memory model выключена", LogCategory);
            return null;
        }

        // Наследуем настройки основного клиента (timeout/retries/ApiKey/CustomProviders),
        // подменяя точку входа и модель. Модель: MemoryModel, иначе первый пресет профиля.
        var apiConfig = new ApiConfig
        {
            ProviderId = profile.Id,
            BaseUrl = profile.BaseUrl,
            ApiKey = config.Api.ApiKey,
            ChatModel = !string.IsNullOrWhiteSpace(mem.MemoryModel)
                ? mem.MemoryModel
                : (profile.ModelPresets.Count > 0 ? profile.ModelPresets[0] : config.Api.ChatModel),
            SttBaseUrl = config.Api.SttBaseUrl,
            SttModel = config.Api.SttModel,
            SttLanguage = config.Api.SttLanguage,
            RequestTimeoutSeconds = config.Api.RequestTimeoutSeconds,
            MaxRetries = config.Api.MaxRetries,
            RateLimitSeconds = config.Api.RateLimitSeconds,
            SttTemperature = config.Api.SttTemperature,
            SttBeamSize = config.Api.SttBeamSize,
            SttVadFilter = config.Api.SttVadFilter,
            ReasoningEffortOverride = config.Api.ReasoningEffortOverride,
            CustomProviders = config.Api.CustomProviders
        };

        try
        {
            logger.Info($"Memory model: separate → {profile.Name} ({profile.Id}), model={apiConfig.ChatModel}, url={profile.BaseUrl}", LogCategory);
            return new OpenAiClient(apiConfig, logger);
        }
        catch (Exception ex)
        {
            logger.Error("Не удалось создать memory LLM-клиент", ex, LogCategory);
            return null;
        }
    }

}
