using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Embeddings;

/// <summary>
/// Выбор IMemoryEmbeddingProvider по MemoryConfig.EmbeddingsMode:
///   disabled (дефолт) — заглушка, recall только по FTS;
///   local   — локальная E5 ONNX-модель из {appDataDir}\models\e5
///             (или абсолютный путь из EmbeddingsModel). Модель опциональна
///             и не скачивается — нет файлов → провайдер IsAvailable=false;
///   openai  — OpenAI-совместимый {api.BaseUrl}/embeddings, модель из
///             EmbeddingsModel (дефолт text-embedding-3-small).
/// Неизвестное значение режима → Warn + disabled.
/// </summary>
public static class EmbeddingFactory
{
    private const string LogCat = "Memory";
    public const string DefaultOpenAiEmbeddingModel = "text-embedding-3-small";

    /// <summary>Каталог локальной E5-модели по умолчанию.</summary>
    public static string DefaultLocalModelDir(string appDataDir)
        => Path.Combine(appDataDir, "models", "e5");

    public static IMemoryEmbeddingProvider Create(MemoryConfig cfg, ApiConfig api,
        string appDataDir, LoggingService? logger = null)
    {
        var mode = (cfg.EmbeddingsMode ?? "disabled").Trim().ToLowerInvariant();
        switch (mode)
        {
            case "local":
            {
                // EmbeddingsModel может держать явный путь к каталогу модели;
                // пусто/имя без разделителей — подкаталог в models\, либо
                // каталог модели, развёрнутый установщиком рядом с exe (§22 Full).
                var dir = FindLocalModelDir(appDataDir, cfg.EmbeddingsModel);
                return new LocalE5EmbeddingProvider(dir, logger, cfg);
            }
            case "openai":
            {
                var model = string.IsNullOrWhiteSpace(cfg.EmbeddingsModel)
                    ? DefaultOpenAiEmbeddingModel
                    : cfg.EmbeddingsModel.Trim();
                return new OpenAiMemoryEmbeddingProvider(api, model, logger);
            }
            case "disabled":
            case "":
                return DisabledEmbeddingProvider.Instance;
            default:
                logger?.Warn($"Unknown EmbeddingsMode '{cfg.EmbeddingsMode}' → disabled", LogCat);
                return DisabledEmbeddingProvider.Instance;
        }
    }

    /// <summary>Стандартные места поиска каталога E5-модели для тестов/UI
    /// (%APPDATA%\models\e5 → каталог рядом с приложением — layout §22 Full).</summary>
    public static IEnumerable<string> CandidateLocalModelDirs(string appDataDir)
    {
        yield return DefaultLocalModelDir(appDataDir);
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "models", "e5");
        yield return Path.Combine(baseDir, "..", "..", "models", "e5");
    }

    /// <summary>Первый существующий каталог модели из candidates, иначе дефолт
    /// AppData (туда докачает сценарий Lite).</summary>
    public static string FindLocalModelDir(string appDataDir, string? explicitDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            var m = explicitDir.Trim();
            if (Path.IsPathRooted(m) || m.Contains(Path.DirectorySeparatorChar) || m.Contains('/'))
                return m;                                     // явный путь к каталогу
            var sub = Path.Combine(appDataDir, "models", m);  // имя подкаталога, напр. "e5-small"
            if (E5ModelDownloader.StaticIsInstalled(sub))
                return sub;
        }
        foreach (var d in CandidateLocalModelDirs(appDataDir))
            if (E5ModelDownloader.StaticIsInstalled(d))
                return Path.GetFullPath(d);
        return DefaultLocalModelDir(appDataDir);
    }
}
