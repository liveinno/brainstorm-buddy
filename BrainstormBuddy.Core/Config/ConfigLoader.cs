using System.IO;
using System.Linq;
using System.Text.Json;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Config;

public class ConfigLoader
{
    // Текущая версия схемы конфига. Повышать при смене дефолтов/структуры — тогда
    // старые конфиги пройдут миграцию (чистка dev-остатков, дедуп пресетов и т.п.).
    public const int CurrentSchemaVersion = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configPath;
    private readonly LoggingService _logger;

    public ConfigLoader(string configPath, LoggingService logger)
    {
        _configPath = configPath;
        _logger = logger;
    }

    public AppConfig Load()
    {
        _logger.Debug($"ConfigLoader.Load: path={_configPath}", "Config");
        try
        {
            if (!File.Exists(_configPath))
            {
                _logger.Info($"Config not found → creating defaults", "Config");
                var defaults = CreateDefaults();
                Validate(defaults);
                Save(defaults);
                return defaults;
            }

            var fileInfo = new FileInfo(_configPath);
            _logger.Debug($"Reading config: {fileInfo.Length} bytes, modified {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}", "Config");

            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config == null)
            {
                _logger.Warn("Config deserialized to null → using defaults", "Config");
                return CreateDefaults();
            }

            Validate(config);

            // Миграция старых конфигов (ручные сборки без версии): чистка dev-остатков STT,
            // дедуп пресетов, сброс висячего активного пресета. Резюме/пресеты/ключи сохраняются.
            if (config.SchemaVersion < CurrentSchemaVersion)
            {
                Migrate(config);
                Save(config);
            }

            _logger.Debug("Config validated OK", "Config");
            return config;
        }
        catch (JsonException ex)
        {
            _logger.Error($"Invalid config JSON: {ex.Message}. Falling back to defaults", ex, "Config");
            return CreateDefaults();
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load config: {ex.Message}", ex, "Config");
            return CreateDefaults();
        }
    }

    public void Save(AppConfig config)
    {
        _logger.Debug($"ConfigLoader.Save: path={_configPath}", "Config");
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                _logger.Debug($"Created directory: {dir}", "Config");
            }

            var backupPath = _configPath + ".bak";
            if (File.Exists(_configPath))
            {
                File.Copy(_configPath, backupPath, overwrite: true);
                _logger.Debug($"Backup created: {backupPath}", "Config");
            }

            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_configPath, json);
            _logger.Info($"Config saved: {json.Length} chars", "Config");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to save config: {ex.Message}", ex, "Config");
            throw;
        }
    }

    private static AppConfig CreateDefaults() => new() { SchemaVersion = CurrentSchemaVersion };

    // Миграция конфига старой версии к текущей схеме. НЕ трогает резюме, ключи и тексты пресетов —
    // только чистит устаревшие dev-значения и приводит структуру к актуальной.
    private void Migrate(AppConfig c)
    {
        _logger.Info($"Config migration: schema {c.SchemaVersion} → {CurrentSchemaVersion}", "Config");

        // 1) Dev-остатки STT (старый LAN-адрес / t-one) — попадали в конфиги ручных сборок и на
        //    чужой машине висят недоступным сервером (STT-шторм «task canceled»).
        if ((c.Api.SttBaseUrl?.Contains("192.168.") ?? false) ||
            string.Equals(c.Api.SttModel, "t-one", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn($"Migration: чищу dev-STT (url={c.Api.SttBaseUrl}, model={c.Api.SttModel})", "Config");
            c.Api.SttBaseUrl = string.Empty;   // пусто → возьмётся адрес LLM
            c.Api.SttModel = "whisper-1";
        }

        // 2) Дедуп пресетов по имени (точные дубли от старых миграций).
        if (c.Advanced.SystemPromptPresets is { Count: > 0 })
        {
            var seen = new HashSet<string>();
            var deduped = c.Advanced.SystemPromptPresets.Where(p => p != null && seen.Add(p.Name)).ToList();
            if (deduped.Count != c.Advanced.SystemPromptPresets.Count)
                _logger.Warn($"Migration: дедуп пресетов {c.Advanced.SystemPromptPresets.Count} → {deduped.Count}", "Config");
            c.Advanced.SystemPromptPresets = deduped;
        }

        // 3) Активный пресет указывает на несуществующий → сброс на дефолт (первый в списке).
        if (!string.IsNullOrEmpty(c.Advanced.ActiveSystemPromptName) &&
            c.Advanced.SystemPromptPresets.All(p => p.Name != c.Advanced.ActiveSystemPromptName))
        {
            _logger.Warn($"Migration: активный пресет '{c.Advanced.ActiveSystemPromptName}' не найден → сброс", "Config");
            c.Advanced.ActiveSystemPromptName = string.Empty;
        }

        // 4) Мульти-агенты по умолчанию ВЫКЛючены у ВСЕХ (требование владельца продукта):
        //    экспериментальный режим, который на старых конфигах мог остаться включённым.
        //    Разово выключаем при миграции; кто хочет — включит галочкой в Настройках → Агенты
        //    (она рабочая и применяется на лету).
        if (c.MultiAgent.Enabled)
        {
            _logger.Info("Migration: мульти-агент был включён → выключаю (дефолт для всех — выкл)", "Config");
            c.MultiAgent.Enabled = false;
        }

        // 5) Агрессивные дефолты эндпойнтинга глушили нормальную/тихую речь: MinSpeech=2000мс
        //    отбрасывал реплики короче 2с (частая причина «STT перестал распознавать»), Silence=4с.
        //    Приводим к новым разумным дефолтам ТОЛЬКО если стоят старые дефолтные значения
        //    (кастомную настройку юзера не трогаем).
        if (c.Audio.MinSpeechMs == 2000)
        {
            _logger.Info("Migration: MinSpeechMs 2000 → 400 (2с отбрасывал короткую/тихую речь)", "Config");
            c.Audio.MinSpeechMs = 400;
        }
        if (Math.Abs(c.Audio.SilenceSeconds - 4.0) < 0.001)
        {
            _logger.Info("Migration: SilenceSeconds 4.0 → 1.8", "Config");
            c.Audio.SilenceSeconds = 1.8;
        }

        // 6) Схема 5: авто-калибровка порога — ВЫКЛ у всех (решение владельца по живым замерам:
        //    на обычных микрофонах шум ~0, ручной порог предсказуемее). Порог: старый дефолт
        //    0.01 → 0.001 (ловит тихую речь/видео); кастомное значение юзера не трогаем.
        if (c.SchemaVersion < 5)
        {
            if (c.Audio.AutoCalibrateThreshold)
            {
                _logger.Info("Migration: авто-калибровка порога была включена → выключаю (дефолт для всех — выкл)", "Config");
                c.Audio.AutoCalibrateThreshold = false;
            }
            if (Math.Abs(c.Audio.RmsThreshold - 0.01) < 1e-9)
            {
                _logger.Info("Migration: RmsThreshold 0.01 (старый дефолт) → 0.001", "Config");
                c.Audio.RmsThreshold = 0.001;
            }
        }

        c.SchemaVersion = CurrentSchemaVersion;
    }

    private void Validate(AppConfig config)
    {
        if (config.MultiAgent.Scenarios.Count == 0)
        {
            // Пустые сценарии = мульти-агенты молча не работают (ProcessAsync вернёт [])
            _logger.Warn("MultiAgent.Scenarios empty → filling defaults (5 scenarios)", "Config");
            var userProfile = config.MultiAgent.UserProfile;
            config.MultiAgent = MultiAgentConfig.CreateDefaults();
            config.MultiAgent.UserProfile = userProfile;
        }
        // Пустое резюме вырождает фактологичность агентов (нечего цитировать → выдумки).
        // Восстанавливаем дефолтное выдуманное резюме, если юзер его не заполнил.
        if (string.IsNullOrWhiteSpace(config.MultiAgent.UserProfile?.Summary))
        {
            _logger.Warn("UserProfile.Summary пуст → подставляю дефолтное резюме", "Config");
            config.MultiAgent.UserProfile = UserProfile.CreateDefault();
        }
        if (config.Audio.SampleRate <= 0)
        {
            _logger.Warn("Invalid SampleRate, using 16000", "Config");
            config.Audio.SampleRate = 16000;
        }
        if (config.Audio.ChunkMaxSeconds <= 0)
        {
            _logger.Warn("Invalid ChunkMaxSeconds, using 60", "Config");
            config.Audio.ChunkMaxSeconds = 60;
        }
        if (config.Audio.SilenceSeconds < 0)
        {
            _logger.Warn("Invalid SilenceSeconds, using 0.5", "Config");
            config.Audio.SilenceSeconds = 0.5;
        }
        if (config.Audio.RmsThreshold <= 0)
        {
            _logger.Warn("Invalid RmsThreshold, using 0.001", "Config");
            config.Audio.RmsThreshold = 0.001;
        }
        if (config.Api.RequestTimeoutSeconds <= 0)
        {
            _logger.Warn("Invalid RequestTimeoutSeconds, using 30", "Config");
            config.Api.RequestTimeoutSeconds = 30;
        }
        if (config.Api.MaxRetries < 0)
        {
            _logger.Warn("Invalid MaxRetries, using 2", "Config");
            config.Api.MaxRetries = 2;
        }
        if (config.Ui.WindowOpacity <= 0 || config.Ui.WindowOpacity > 1)
        {
            _logger.Warn("Invalid WindowOpacity, using 0.9", "Config");
            config.Ui.WindowOpacity = 0.9;
        }
        if (config.Advanced.HistorySize <= 0)
        {
            _logger.Warn("Invalid HistorySize, using 6", "Config");
            config.Advanced.HistorySize = 6;
        }
        if (config.Advanced.MaxResponseTokens <= 0)
        {
            _logger.Warn("Invalid MaxResponseTokens, using 180", "Config");
            config.Advanced.MaxResponseTokens = 180;
        }
    }
}
