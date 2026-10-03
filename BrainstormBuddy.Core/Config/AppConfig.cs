using System.Text.Json.Serialization;
using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Config;

public class AppConfig
{
    // Версия схемы конфига — для миграции при апгрейде. 0 = конфиг до версионирования
    // (старые ручные сборки): при загрузке ConfigLoader прогонит миграцию и повысит.
    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = 0;

    [JsonPropertyName("Api")]
    public ApiConfig Api { get; set; } = new();

    [JsonPropertyName("Audio")]
    public AudioConfig Audio { get; set; } = new();

    [JsonPropertyName("Ui")]
    public UiConfig Ui { get; set; } = new();

    [JsonPropertyName("Hotkeys")]
    public HotkeysConfig Hotkeys { get; set; } = new();

    [JsonPropertyName("LocalStt")]
    public LocalSttConfig LocalStt { get; set; } = new();

    [JsonPropertyName("Advanced")]
    public AdvancedConfig Advanced { get; set; } = new();

    [JsonPropertyName("MultiAgent")]
    public MultiAgentConfig MultiAgent { get; set; } = new();

    [JsonPropertyName("Logging")]
    public LoggingConfig Logging { get; set; } = new();

    [JsonPropertyName("Karaoke")]
    public KaraokeConfig Karaoke { get; set; } = new();
}

public class LoggingConfig
{
    // Файловое логирование включено по умолчанию — нужно для диагностики и поддержки.
    // Выключение = «полное отключение»: приложение перестаёт писать app.log на диск.
    public bool Enabled { get; set; } = true;
    // Подробный (Debug) уровень. Выкл по умолчанию: меньше шума в логах и меньше
    // риска, что в файл попадут фрагменты распознанной речи (пишутся только на Debug).
    public bool Verbose { get; set; } = false;
    // Ротация: сколько файлов app.log хранить (текущий + N−1 архивных) и предельный
    // размер одного файла в МБ. MaxFiles=1 → без архивов (ротация «по кругу» одного файла).
    public int MaxFiles { get; set; } = 5;
    public int MaxFileSizeMb { get; set; } = 1;
}

public class MultiAgentConfig
{
    // По умолчанию мульти-агент (ТехЛид/HR) ВЫКЛЮЧЕН — работает одиночный ассистент
    // по активному пресету (напр. «Собеседование: шпаргалка»). Включается в настройках.
    public bool Enabled { get; set; } = false;
    public string ActiveScenarioId { get; set; } = "interview";
    public List<ScenarioConfig> Scenarios { get; set; } = new();
    public UserProfile UserProfile { get; set; } = new();

    public static MultiAgentConfig CreateDefaults()
    {
        return new MultiAgentConfig
        {
            Enabled = false,
            ActiveScenarioId = "interview",
            UserProfile = UserProfile.CreateDefault(),
            Scenarios = new List<ScenarioConfig>
            {
                ScenarioConfig.CreateInterview(),
                ScenarioConfig.CreateBrainstorm(),
                ScenarioConfig.CreateCustomerCall(),
                ScenarioConfig.CreateCareerConsult(),
                ScenarioConfig.CreateOneOnOne()
            }
        };
    }
}

public class ApiConfig
{
    // Выбранный провайдер из реестра (LlmProviderRegistry): "" = старый конфиг до миграции,
    // "custom" = ручной BaseUrl без профиля. Детали профиля (заголовки, пути) — в реестре.
    public string ProviderId { get; set; } = "";
    public List<LlmProviderProfile> CustomProviders { get; set; } = new();
    /// <summary>Глобальный override reasoning effort для ВСЕХ провайдеров.
    /// null (дефолт) = брать Profile.ReasoningEffort; "" = не слать поле reasoning
    /// вообще; "low"/"medium"/"high" = слать это значение.</summary>
    public string? ReasoningEffortOverride { get; set; } = null;
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string ChatModel { get; set; } = "qwen2.5vl:7b";
    public string SttBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string SttModel { get; set; } = "whisper-1";
    public string SttLanguage { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;
    // 0 = без искусственной паузы между LLM-запросами. Раньше 15с — это тормозило живой
    // ассистент (1 ответ в 15с) И блокировало STT-воркер, из-за чего при неготовом LLM
    // очередь чанков забивалась за ~3 мин и STT переставал выводить текст. Для строгих
    // free-tier API можно поднять в настройках.
    public int RateLimitSeconds { get; set; } = 0;
    public double SttTemperature { get; set; } = 0.0;
    public int SttBeamSize { get; set; } = 10;
    public bool SttVadFilter { get; set; } = true;
}

public class AudioConfig
{
    public int SampleRate { get; set; } = 16000;
    public int ChunkMaxSeconds { get; set; } = 60;
    // Дефолт паузы для нарезки. Был 4.0с — слишком много (при адаптиве перекрывается,
    // но база важна на старте). 1.8с = разумный разговорный дефолт (совпадает с кнопкой сброса).
    public double SilenceSeconds { get; set; } = 1.8;
    // Порог речи. Дефолт 0.004 (требование владельца): выше шумового пола обычного
    // микрофона (хлопки/щелчки ~0.000-0.001 не дают ложных срабатываний из коробки),
    // при этом нормальная/тихая речь ~0.009+ ловится уверенно.
    public double RmsThreshold { get; set; } = 0.004;
    // Авто-калибровка порога распознавания под реальный уровень звука (шумовой пол × запас).
    // ВКЛ по умолчанию — работает и с тихим (видео/loopback), и с громким звуком без ручной настройки.
    // ВЫКЛ → используется фиксированный RmsThreshold (ползунок в настройках).
    // Дефолт ВЫКЛ (решение владельца по живым замерам): ручной порог 0.004 стоит выше
    // шумового пола обычного микрофона и ловит речь без сюрпризов авто-адаптации.
    // Авто — опция для реально шумного окружения.
    public bool AutoCalibrateThreshold { get; set; } = false;
    public int VadMode { get; set; } = 0;
    /// <summary>
    /// DEPRECATED: WebRtcVadSharp removed in v3 (native DLL 0x8007000B). Use VadMode only.
    /// Field kept for config.json backward compatibility; ignored by AudioBuffer.
    /// </summary>
    public bool UseWebRtcVad { get; set; } = true;
    public int PreRollMs { get; set; } = 600;
    public int PostRollMs { get; set; } = 400;
    public int OverlapMs { get; set; } = 1000;
    // Мин. длительность речи для валидного чанка. Был 2000мс — ОТБРАСЫВАЛ нормальные/короткие
    // реплики (особенно на тихом микрофоне, где «длительность речи» = только громкие куски),
    // из-за чего STT «переставал» распознавать. 400мс отсекает клики/шум, но пропускает речь.
    public int MinSpeechMs { get; set; } = 400;
    // Режим нарезки речи (эндпойнтинг): "manual" (порог задаёт юзер пресетами/слайдером) |
    // "adaptive" (авто по паузам говорящего) | "semantic" (adaptive + текстовая склейка мыслей).
    // Дефолт — "adaptive" (старый комментарий врал про "manual" и путал диагностику).
    public string EndpointMode { get; set; } = "adaptive";
    // Выученные пороги авто-режима для тёплого старта (сохраняются между сессиями). 0 = не задано.
    public double AdaptiveLastLoopbackSeconds { get; set; } = 0;
    public double AdaptiveLastMicSeconds { get; set; } = 0;
    public string MicDeviceId { get; set; } = string.Empty;
    public string LoopbackDeviceId { get; set; } = string.Empty;
    public bool MicOnly { get; set; } = false;
    public bool CaptureMic { get; set; } = true;
    // Движок распознавания: "remote" (внешний сервер/Docker) | "native" (встроенный GigaAM ONNX).
    // Дефолт "native": инсталлятор кладёт модель рядом с exe, работает офлайн из коробки.
    // Если модели нет (компактная установка), CreateSttEngine сам откатится на "remote".
    public string SttEngine { get; set; } = "native";
    // Ускорение нативного движка: "cpu" (везде, дефолт) | "directml" (GPU) | "auto".
    // ВАЖНО: на слабых iGPU (Intel UHD) DirectML часто медленнее CPU — дефолт CPU.
    public string SttAccel { get; set; } = "cpu";
    // Индекс GPU (DXGI-адаптер) для DirectML: 0 обычно встройка, 1 — дискретная (NVIDIA/AMD).
    public int SttGpuDevice { get; set; } = 0;
    // Оборудование ФАЙЛОВОЙ транскрибации (окно «Транскрибация файла») — отдельно от живого STT:
    // файлы выгодно гнать на GPU, живой режим чаще на CPU. "auto" = прежнее поведение
    // (GigaAM: переиспользовать живой движок/CPU; Whisper: по WhisperAccel). "cpu"/"gpu" —
    // явный выбор юзера в окне; FileSttGpuDevice — DXGI-индекс адаптера (-1 = авто: дискретный).
    public string FileSttAccel { get; set; } = "auto";
    public int FileSttGpuDevice { get; set; } = -1;
    // Путь к ONNX-модели GigaAM (пусто → авто: %APPDATA%\models, рядом с exe, или artifacts для dev).
    public string SttModelPath { get; set; } = "";
    // Докачка модели (Фаза 1.3): URL (GitLab Release/Package) + ожидаемый размер для проверки.
    public string SttModelUrl { get; set; } = "";
    public long SttModelBytes { get; set; } = 0;
    public string SttModelSha256 { get; set; } = "";
    // Для приватного репо: имя/значение auth-заголовка (напр. "PRIVATE-TOKEN"). Держать в
    // roaming-конфиге, НЕ в исходниках. Пусто → без авторизации (публичный URL).
    public string SttModelAuthHeader { get; set; } = "";
    public string SttModelAuthValue { get; set; } = "";
    // Whisper large-v3-turbo (ggml) — «качественный» движок для транскрибации файлов.
    // Модель качается отдельно (q5_0 ~574 МБ). URL — официальный whisper.cpp на HuggingFace.
    public string WhisperModelUrl { get; set; } =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin?download=true";
    public long WhisperModelBytes { get; set; } = 0; // 0 → без строгой проверки размера
    public string WhisperLanguage { get; set; } = "auto"; // "auto" (детект+смешанная речь) | "ru"
    // Ускорение Whisper: "auto" (GPU через Vulkan, откат на CPU) | "gpu" | "cpu".
    // Vulkan работает на NVIDIA/AMD/Intel через драйвер (без CUDA). Применяется после перезапуска.
    public string WhisperAccel { get; set; } = "auto";
    // GPU для Whisper (Vulkan): индекс DXGI/WMI-адаптера (тот же порядок, что SttGpuDevice и
    // GpuEnumerator.List()); -1 = авто (дискретная/первая). Внутри маппится на Vulkan-индекс.
    public int WhisperGpuDevice { get; set; } = -1;
    public bool EnableDebugLogs { get; set; } = false;
}

public class UiConfig
{
    public double WindowOpacity { get; set; } = 0.90;
    public string WindowPosition { get; set; } = "TopRight";
    public double WindowPositionX { get; set; } = -1;
    public double WindowPositionY { get; set; } = -1;
    public bool ShowCompatibilityBorder { get; set; } = false;
    public int FontSize { get; set; } = 14;
    public bool ClickThrough { get; set; } = false;
    public string SavePath { get; set; } = string.Empty;
    public string Theme { get; set; } = "HackerTheme";
    public bool TimelineCollapsed { get; set; } = true; // эквалайзер по умолчанию свёрнут
    public bool ShowTimingLine { get; set; } = true;    // тех-строка rec/STT/LLM у ответа
    public bool OnboardingDone { get; set; } = false;   // визард первого старта показан
    public string Language { get; set; } = "ru";
    // Инженерный режим: показывает тонкие настройки на главном оверлее (слайдеры паузы/чанка,
    // пресеты скорости речи, эквалайзер). По умолчанию выключен — главный экран чистый.
    public bool EngineerMode { get; set; } = false;
    // Скрыть иконку в трее: вернуть её хоткеем Hotkeys.ShowTrayIcon (по умолчанию Ctrl+Shift+T).
    public bool HideTrayIcon { get; set; } = false;
    // Состояния кнопок нижней панели оверлея — переживают перезапуск (требование владельца:
    // выключил LLM/динамик перед встречей — после рестарта режим тот же, без сюрпризов).
    public bool AudioPaused { get; set; } = false;   // микрофон: пауза захвата аудио
    public bool LlmDisabled { get; set; } = false;   // «LLM выкл»: чистый транскрибатор, в LLM ничего не уходит
    public bool LoopbackMuted { get; set; } = false; // «Динамик выкл»: loopback-кадры выбрасываются до буфера
}

public class KaraokeConfig
{
    public bool Enabled { get; set; } = false;                 // показывать окно
    public string Position { get; set; } = "Bottom";           // Bottom|Top|Left|Right
    public int WidthPct { get; set; } = 60;                    // 20-100, % ширины экрана (для Left/Right — ширина верт. полосы)
    public int HeightPct { get; set; } = 0;                    // 0 = авто (по шрифту/числу строк); 1-100 = % высоты рабочей области
    public double FontSize { get; set; } = 26;                 // 12-64
    public string TextColor { get; set; } = "#FFFFFFFF";       // #AARRGGBB; единый цвет строк при AgeColorFade=false (в фейде не участвует)
    public string AccentColor { get; set; } = "#FF4ADE80";     // маркер свежей строки
    public string FreshColor { get; set; } = "#FFFFE9A8";      // цвет самой свежей строки (age=0) при градиенте по возрасту
    public string OldLinesColor { get; set; } = "#FFFFFFFF";   // цвет старших строк (age>=1) при градиенте по возрасту
    public string BackgroundColor { get; set; } = "#FF0A1622"; // фон полосы
    public double BackgroundOpacity { get; set; } = 0.78;      // 0-1 прозрачность фона полосы
    public bool ShowBackground { get; set; } = true;           // false = скрыть прямоугольник-фон, оставить только текст
    public bool TextShadow { get; set; } = true;               // тень текста при ShowBackground=false (читаемость на любом фоне)
    public double Opacity { get; set; } = 0.9;                 // 0.25-1 прозрачность всего окна
    public int MaxLines { get; set; } = 4;                     // 1-30 видимых строк (при HeightPct=100 — до 30 на весь экран)
    public int SlideDurationMs { get; set; } = 700;            // 100-2000 скорость въезда
    public int FadeDurationMs { get; set; } = 600;             // 100-2000 затухание/смена возраста
    public int InnerPadding { get; set; } = 24;                // 0-80 px заполнение (padding полосы)
    public int MarginPx { get; set; } = 30;                    // 0-200 отступ от края экрана
    public string TextAlignment { get; set; } = "Left";        // Left|Center
    public bool ShowAnswers { get; set; } = true;              // ответы LLM
    public bool ShowQuestions { get; set; } = false;           // реплики собеседника [Динамик]
    public bool ShowUserLines { get; set; } = false;           // реплики юзера [Микрофон]
    public bool DimByAge { get; set; } = true;                 // гасить старые строки по возрасту
    public bool AgeColorFade { get; set; } = true;             // градиент цвета по возрасту: золото (FreshColor) на свежей строке → OldLinesColor на старших
    public bool ShowMarker { get; set; } = true;               // акцентный маркер у свежей строки
    public bool HiddenFromCapture { get; set; } = true;        // true = видно только юзеру (WDA_EXCLUDEFROMCAPTURE), false = видно всем
    public bool ClickThrough { get; set; } = true;             // клики проходят сквозь
    public bool WordWrap { get; set; } = true;                 // перенос строк
}

public class HotkeysConfig
{
    public string ToggleVisibility { get; set; } = "Ctrl+Shift+H";
    public string ChangeOpacity { get; set; } = "Ctrl+Shift+O";
    public string OpenSettings { get; set; } = "Ctrl+Shift+S";
    public string TogglePause { get; set; } = "Ctrl+Shift+P";
    public string ToggleKaraoke { get; set; } = "Ctrl+Shift+K";
    // Возврат иконки трея, когда Ui.HideTrayIcon скрыл её из трея.
    public string ShowTrayIcon { get; set; } = "Ctrl+Shift+T";
}

public class AdvancedConfig
{
    public string SystemPrompt { get; set; } =
        "Ты — скрытый ассистент кандидата на техническом собеседовании. " +
        "Отвечай СРАЗУ и КОРОТКО (1-3 предложения) готовым текстом, который кандидат может произнести. " +
        "ЗАПРЕЩЕНО: рассуждать вслух, описывать свой анализ, упоминать теги [Динамик]/[Микрофон]/[secret], задавать вопросы, писать мета-комментарии. " +
        "Теги — только метка источника: [Динамик]=вопрос интервьюера, [Микрофон]=реплика кандидата, [secret]=личное указание. " +
        "На любой вопрос давай прямой ответ на русском.";
    public int HistorySize { get; set; } = 15;
    public int MaxResponseTokens { get; set; } = 180;
    // Default=false (2.7.1): часть шлюзов в SSE-режиме игнорит max_tokens и не
    // шлёт finish_reason у пустого ответа — латентность вырастает ×3. OpenAiClient
    // сам отключает стрим на сессию при таком симптоме, но лучше не включать его
    // по умолчанию. У юзеров со включённой настройкой в config.json всё остаётся.
    public bool StreamLlmAnswers { get; set; } = false;
    public bool MergeMicLoopback { get; set; } = false;
    public string ExpandInstruction { get; set; } =
        "## РЕЖИМ РАСШИРЕНИЯ ОТВЕТА\n" +
        "Пользователь отправил триггер [EXPAND]. Твоя задача — развернуть предыдущий ответ.\n\n" +
        "Вопрос: {PREVIOUS_QUESTION}\n" +
        "Предыдущий ответ: {PREVIOUS_ANSWER}\n\n" +
        "Ответь развёрнуто (100-200 слов), сохранив роль и стиль активного пресета.";
    public List<NamedPrompt> SystemPromptPresets { get; set; } = new();
    public string ActiveSystemPromptName { get; set; } = string.Empty;
}

public class LocalSttConfig
{
    public bool Enabled { get; set; } = false;
    public string Model { get; set; } = "gigaam-v2";
    public int Port { get; set; } = 8765;
    public string ContainerName { get; set; } = "brainstorm-local-stt";
    public bool AutoStart { get; set; } = false;
}

public class NamedPrompt
{
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    // ComboBox/ListBox без DisplayMemberPath показывают ToString —
    // без override в UI выводится имя типа (BrainstormBuddy.Config.NamedPrompt)
    public override string ToString() => Name;
}
