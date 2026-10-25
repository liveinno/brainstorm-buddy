using BrainstormBuddy.Ai;
using System.IO;
using System.Linq;
using System.Text.Json;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Config;

public class ConfigLoader
{
    // Текущая версия схемы конфига. Повышать при смене дефолтов/структуры — тогда
    // старые конфиги пройдут миграцию (чистка dev-остатков, дедуп пресетов и т.п.).
    public const int CurrentSchemaVersion = 12;

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

        // 1) Dev-остаток STT: модель «t-one» из ранних dev-сборок — на чужой машине
        //    висит недоступным сервером (STT-шторм «task canceled»). По URL не судим:
        //    у пользователя может быть свой легитимный LAN-STT.
        if (string.Equals(c.Api.SttModel, "t-one", StringComparison.OrdinalIgnoreCase))
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
        //    0.01 → актуальный дефолт 0.004 (выше шумового пола, ловит тихую речь/видео);
        //    кастомное значение юзера не трогаем.
        if (c.SchemaVersion < 5)
        {
            if (c.Audio.AutoCalibrateThreshold)
            {
                _logger.Info("Migration: авто-калибровка порога была включена → выключаю (дефолт для всех — выкл)", "Config");
                c.Audio.AutoCalibrateThreshold = false;
            }
            if (Math.Abs(c.Audio.RmsThreshold - 0.01) < 1e-9)
            {
                _logger.Info("Migration: RmsThreshold 0.01 (старый дефолт) → 0.004", "Config");
                c.Audio.RmsThreshold = 0.004;
            }
        }

        // 7) Схема 6: появился реестр LLM-провайдеров — бэкфиллим ProviderId по старому
        //    BaseUrl, чтобы профиль (заголовки/пути) подобрался без ручной правки.
        if (string.IsNullOrWhiteSpace(c.Api.ProviderId))
        {
            c.Api.ProviderId = LlmProviderRegistry.InferProviderId(c.Api.BaseUrl);
            _logger.Info($"Migration: ProviderId пуст → вывели по BaseUrl: '{c.Api.ProviderId}' ({c.Api.BaseUrl})", "Config");
        }

        // 8) Схема 7 (2.16.0): ScreenOcr.Enabled разделён на «фича доступна» (Enabled) и
        //    «фоновое слежение» (AutoWatchEnabled, дефолт false). У пользователей со
        //    включённым OCR авто-слежение РАБОТАЛО — не глушим его апгрейдом:
        //    поднимаем AutoWatch для старых конфигов с Enabled=true. Новые установки
        //    получают честный дефолт false (включается галочкой в «Экран OCR»).
        if (c.SchemaVersion < 7 && c.ScreenOcr.Enabled && !c.ScreenOcr.AutoWatchEnabled)
        {
            _logger.Info("Migration: ScreenOcr.Enabled=true → AutoWatchEnabled=true (раньше слежение было неотделимо)", "Config");
            c.ScreenOcr.AutoWatchEnabled = true;
        }
        // 9) Схема 8 (2.16.1): лимит ответа и память «из коробки» должны дойти до
        //    СУЩЕСТВУЮЩИХ конфигов, а не только до чистых установок. Меняем ТОЛЬКО
        //    старые дефолтные значения — кастомные настройки юзера не трогаем.
        if (c.SchemaVersion < 8)
        {
            // Старый дефолт 180 рвал ответы на полуслове (finish_reason=length в
            // логах 2026-10-08). Кастомный лимит (больше/меньше) — оставляем.
            if (c.Advanced.MaxResponseTokens == 180)
            {
                _logger.Info("Migration: MaxResponseTokens 180 → 400 (старый дефолт обрезал ответы)", "Config");
                c.Advanced.MaxResponseTokens = 400;
            }
            // Память «из коробки»: у конфигов эпохи ≤2.15 Enabled=false и
            // EmbeddingsMode="disabled" лежали как ДЕФОЛТЫ (юзер их не ставил) —
            // после апгрейда память оставалась мёртвой ровно из-за этого. Поднимаем;
            // кто осознанно выключил — выключит галочкой, это один клик.
            if (!c.Memory.Enabled)
            {
                _logger.Info("Migration: Memory.Enabled=false (старый дефолт) → true — память из коробки", "Config");
                c.Memory.Enabled = true;
            }
            if (string.Equals(c.Memory.EmbeddingsMode, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info("Migration: EmbeddingsMode=disabled → local (e5: в Full бандлится, в Lite качается по кнопке)", "Config");
                c.Memory.EmbeddingsMode = "local";
            }
        }

        // 10) Схема 9 (2.17.0): память «из коробки» включает и чтение, и запись по
        //    умолчанию — у старых конфигов DefaultUseMemory/DefaultWriteMemory могли
        //    остаться false из эпохи до 2.16. Поднимаем false → true (кастомный true
        //    не трогаем). CooldownSec OCR стал double: допустимый диапазон 0.3–30 с,
        //    выходящие значения зажимаем.
        if (c.SchemaVersion < 9)
        {
            if (!c.Memory.DefaultUseMemory)
            {
                _logger.Info("Migration: DefaultUseMemory=false → true (память читает прошлые встречи)", "Config");
                c.Memory.DefaultUseMemory = true;
            }
            if (!c.Memory.DefaultWriteMemory)
            {
                _logger.Info("Migration: DefaultWriteMemory=false → true (память пишет новые встречи)", "Config");
                c.Memory.DefaultWriteMemory = true;
            }
            if (c.ScreenOcr.CooldownSec > 30)
            {
                _logger.Info($"Migration: ScreenOcr.CooldownSec {c.ScreenOcr.CooldownSec} → 30 (допустимый диапазон 0.3-30)", "Config");
                c.ScreenOcr.CooldownSec = 30;
            }
            else if (c.ScreenOcr.CooldownSec < 0.3)
            {
                _logger.Info($"Migration: ScreenOcr.CooldownSec {c.ScreenOcr.CooldownSec} → 0.3 (допустимый диапазон 0.3-30)", "Config");
                c.ScreenOcr.CooldownSec = 0.3;
            }
        }

        // schema 10 (2.18.0): новые дефолты «из коробки» — стриминг, шрифт 12,
        // диаризация. Меняем только точные старые дефолтные значения, чтобы не
        // переопределять осознанный кастом.
        if (c.SchemaVersion < 10)
        {
            if (!c.Advanced.StreamLlmAnswers)
            {
                _logger.Info("Migration: StreamLlmAnswers=false → true (2.18.0: стриминг по умолчанию)", "Config");
                c.Advanced.StreamLlmAnswers = true;
            }
            if (c.Ui.FontSize == 17)
            {
                _logger.Info("Migration: Ui.FontSize 17 → 12 (2.18.0: компактный дефолт)", "Config");
                c.Ui.FontSize = 12;
            }
            if (!c.SpeakerDiarization.Enabled)
            {
                _logger.Info("Migration: SpeakerDiarization.Enabled=false → true (2.18.0: диаризация по умолчанию)", "Config");
                c.SpeakerDiarization.Enabled = true;
            }
        }

        // Схема 11: дефолт лимита ответа поднят до 10000 (владелец: обычный чат и
        // вопросы по документу должны получать видимый ответ без ручной настройки;
        // 1500 не хватало reasoning-моделям — видимый ответ рвался finish=length).
        // Переписываем ТОЛЬКО известные прежние дефолты (180 → 400 → 1500 по
        // версиям); кастомные значения юзера (3500 и т.п.) не трогаем.
        if (c.SchemaVersion < 11)
        {
            if (c.Advanced.MaxResponseTokens is 180 or 400 or 1500)
            {
                _logger.Info($"Migration: Advanced.MaxResponseTokens {c.Advanced.MaxResponseTokens} -> 10000 (стандартный лимит ответа)", "Config");
                c.Advanced.MaxResponseTokens = 10000;
            }
        }

        // Схема 12 (требование владельца 2026-10-12): стандартный бюджет ответа
        // поднят до 25000. Переписываем ТОЛЬКО известные прежние дефолты —
        // включая 10000 из схемы 11 и 4096, который лежал в сохранённых конфигах
        // (лог app.1.log 12:12:31 — запрос уходил с maxTokens=4096); кастомное
        // значение юзера (8000 и т.п.) не трогаем.
        if (c.SchemaVersion < 12)
        {
            if (c.Advanced.MaxResponseTokens is 1500 or 4096 or 10000)
            {
                _logger.Info($"Migration: Advanced.MaxResponseTokens {c.Advanced.MaxResponseTokens} -> 25000 (стандартный бюджет ответа)", "Config");
                c.Advanced.MaxResponseTokens = 25000;
            }
        }

        c.SchemaVersion = CurrentSchemaVersion;
    }

    private void Validate(AppConfig config)
    {
        if (config.MultiAgent.Scenarios.Count == 0)
        {
            // Пустые сценарии = мульти-агенты молча не работают (ProcessAsync вернёт [])
            _logger.Warn("MultiAgent.Scenarios empty → filling defaults (6 scenarios)", "Config");
            var userProfile = config.MultiAgent.UserProfile;
            config.MultiAgent = MultiAgentConfig.CreateDefaults();
            config.MultiAgent.UserProfile = userProfile;
        }
        else
        {
            // Старым конфигам доезжают новые встроенные сценарии (2.8.x: «Обучение»),
            // в каноничной позиции после ближайшего существующего предка.
            var builtins = new List<ScenarioConfig>
            {
                ScenarioConfig.CreateInterview(), ScenarioConfig.CreateBrainstorm(),
                ScenarioConfig.CreateCustomerCall(), ScenarioConfig.CreateCareerConsult(),
                ScenarioConfig.CreateOneOnOne(), ScenarioConfig.CreateTraining()
            };
            for (var i = 0; i < builtins.Count; i++)
            {
                if (config.MultiAgent.Scenarios.Any(s => s.Id == builtins[i].Id)) continue;
                var insertAt = -1;
                for (var j = i - 1; j >= 0 && insertAt < 0; j--)
                {
                    var pos = config.MultiAgent.Scenarios.FindIndex(s => s.Id == builtins[j].Id);
                    if (pos >= 0) insertAt = pos + 1;
                }
                if (insertAt < 0) insertAt = config.MultiAgent.Scenarios.Count;
                config.MultiAgent.Scenarios.Insert(insertAt, builtins[i]);
                _logger.Info($"Migration: scenario '{builtins[i].Id}' added to config", "Config");
            }
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
            _logger.Warn("Invalid RmsThreshold, using 0.004", "Config");
            config.Audio.RmsThreshold = 0.004;
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
        // Пользовательские провайдеры: выбрасываем неработоспособные (без Id/Name),
        // протокол из белого списка — иначе openai-chat.
        if (config.Api.CustomProviders.Count > 0)
        {
            var bad = config.Api.CustomProviders.RemoveAll(p =>
                string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name));
            if (bad > 0) _logger.Warn($"CustomProviders: отброшено {bad} профилей без Id/Name", "Config");
            foreach (var p in config.Api.CustomProviders)
                if (p.Protocol is not ("openai-chat" or "openai-responses" or "anthropic"))
                {
                    _logger.Warn($"CustomProviders[{p.Id}]: неизвестный протокол '{p.Protocol}' → openai-chat", "Config");
                    p.Protocol = "openai-chat";
                }
        }
        // ProviderId должен быть всегда (для всяких «голых» конфигов без миграции).
        if (string.IsNullOrWhiteSpace(config.Api.ProviderId))
            config.Api.ProviderId = LlmProviderRegistry.InferProviderId(config.Api.BaseUrl);
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
            _logger.Warn("Invalid MaxResponseTokens, using 25000", "Config");
            config.Advanced.MaxResponseTokens = 25000;
        }
    }
}
