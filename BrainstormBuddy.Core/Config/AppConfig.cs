using System.Text.Json.Serialization;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Core.Speaker;

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

    [JsonPropertyName("ScreenOcr")]
    public ScreenOcrConfig ScreenOcr { get; set; } = new();
    [JsonPropertyName("Memory")]
    public MemoryConfig Memory { get; set; } = new();

    [JsonPropertyName("SpeakerDiarization")]
    public SpeakerDiarizationConfig SpeakerDiarization { get; set; } = new();
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
                ScenarioConfig.CreateOneOnOne(),
                ScenarioConfig.CreateTraining()
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
    // Движок распознавания: "native3" (встроенный GigaAM v3 INT8, BPE-CTC) | "native"
    // (встроенный GigaAM v2) | "whisper" | "remote" (внешний сервер/Docker).
    // Дефолт "native3" для чистых конфигов: модель v3 ищется в тех же местах, что v2.
    // Существующие конфиги с "native" продолжают работать на v2 — принудительно не мигрируем.
    // Если моделей нет (компактная установка), CreateSttEngine сам откатится на "remote".
    public string SttEngine { get; set; } = "native3";
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
    // Путь к ONNX-модели GigaAM v2 (пусто → авто: %APPDATA%\models, рядом с exe, или artifacts для dev).
    public string SttModelPath { get; set; } = "";
    // Путь к модели GigaAM v3: файл v3_e2e_ctc.int8.onnx или каталог с ним (пусто → тот же авто-поиск).
    public string SttV3ModelPath { get; set; } = "";
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
    public int FontSize { get; set; } = 12;   // компактный дефолт (2.18.0; было 17)
    // Лимит длины ответа в оверлее (символов). 0 = без обрезки. При превышении
    // текст обрезается «…» + подсказка со ссылкой на настройки (2.18.0).
    public int MaxAnswerChars { get; set; } = 1500;
    // Внутренние отступы текста в пузырях вопроса/ответа (px, симметрично). 2.18.0.
    public double BubblePadding { get; set; } = 12;
    public bool ClickThrough { get; set; } = false;
    public string SavePath { get; set; } = string.Empty;
    public string Theme { get; set; } = "HackerTheme";
    public bool TimelineCollapsed { get; set; } = true; // эквалайзер по умолчанию свёрнут
    public bool ShowTimingLine { get; set; } = false;         // тех-строка rec/STT/LLM у ответа
    public bool ShowQuestionTimingLine { get; set; } = false; // тех-метки STT/OCR/аудио у вопроса (точка-статус + mono-строка)
    public bool OnboardingDone { get; set; } = false;   // визард первого старта показан
    public string Language { get; set; } = "ru";
    // Инженерный режим: показывает тонкие настройки на главном оверлее (слайдеры паузы/чанка,
    // пресеты скорости речи, эквалайзер). По умолчанию выключен — главный экран чистый.
    public bool EngineerMode { get; set; } = false;
    // Скрыть иконку в трее: вернуть её хоткеем Hotkeys.ShowTrayIcon (по умолчанию Ctrl+Shift+T).
    public bool HideTrayIcon { get; set; } = false;
    // Состояния кнопок нижней панели оверлея — переживают перезапуск (требование владельца:
    // выключил LLM/динамик перед встречей — после рестарта режим тот же, без сюрпризов).
    public bool AudioPaused { get; set; } = false;   // пауза захвата аудио целиком (хоткей Ctrl+Shift+P)
    public bool LlmDisabled { get; set; } = false;   // «LLM выкл»: чистый транскрибатор, в LLM ничего не уходит
    public bool LoopbackMuted { get; set; } = false; // «Динамик выкл»: loopback-кадры выбрасываются до буфера
    public bool MicMuted { get; set; } = false;      // «Мик выкл»: mic-кадры выбрасываются до буфера (кнопка микрофона)
    // Стелс-режим: alias имени процесса для Диспетчера задач. Пусто = выкл.
    // При заданном первый запуск перезапускает приложение копией
    // %APPDATA%\BrainstormBuddy\stealth\<alias>.exe — колонка «Имя процесса» = alias
    // (StealthModeService). Применяется при следующем запуске.
    public string StealthProcessName { get; set; } = string.Empty;
}

public class KaraokeConfig
{
    public bool Enabled { get; set; } = false;                 // показывать окно
    public string Position { get; set; } = "Bottom";           // Bottom|Top|Left|Right
    public string ContentAnchor { get; set; } = "Bottom";      // Bottom|Top — куда прижимать контент внутри полосы (осмысленно при HeightPct>0: Top → текст у верхней кромки, пустота снизу)
    public int WidthPct { get; set; } = 60;                    // 20-100, % ширины экрана (для Left/Right — ширина верт. полосы)
    public int HeightPct { get; set; } = 25;                   // 0 = резиновая высота по контенту; 1-100 = ЖЁСТКАЯ высота полосы в % рабочей области
    public double FontSize { get; set; } = 14;                 // 12-64
    public string TextColor { get; set; } = "#FFFFFFFF";       // #AARRGGBB; единый цвет строк при AgeColorFade=false (в фейде не участвует)
    public string AccentColor { get; set; } = "#FF4ADE80";     // маркер свежей строки
    public string FreshColor { get; set; } = "#FFFFE9A8";      // цвет самой свежей строки (age=0) при градиенте по возрасту
    public string OldLinesColor { get; set; } = "#FFFFFFFF";   // цвет старших строк (age>=1) при градиенте по возрасту
    public string QuestionColor { get; set; } = "#FFF27A8A";   // цвет реплик собеседника (роль "question") — розовый акцент, не зависит от возраста
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
    public bool DimByAge { get => HistoryFade; set => HistoryFade = value; } // legacy-имя HistoryFade (старые config.json); то же свойство
    public bool HistoryFade { get; set; } = true;              // мастер-флаг затухания старых строк; false = все строки полной яркости
    public double MinHistoryOpacity { get; set; } = 0.35;      // 0-1: минимальная прозрачность самой старой видимой строки (и нижний край OpacityMask)
    public int HistoryLineCount { get; set; } = 3;             // 0-30: старших строк над свежей; лимит ленты = min(MaxLines, HistoryLineCount+1)
    public bool AgeColorFade { get; set; } = true;             // градиент цвета по возрасту: золото (FreshColor) на свежей строке → OldLinesColor на старших
    public bool ShowMarker { get; set; } = true;               // акцентный маркер у свежей строки
    public bool HiddenFromCapture { get; set; } = true;        // true = видно только юзеру (WDA_EXCLUDEFROMCAPTURE), false = видно всем
    public bool ClickThrough { get; set; } = true;             // клики проходят сквозь
    public bool WordWrap { get; set; } = true;                 // перенос строк
}

/// <summary>
/// Экран OCR: распознавание текста с экрана и отправка найденных вопросов в LLM.
/// </summary>
public class ScreenOcrConfig
{
    public bool Enabled { get; set; } = false;          // фича доступна: шар-индикатор; хоткей «распознать сейчас» (Ctrl+Shift+N) ею НЕ глушится — разовый проход работает всегда
    public bool AutoWatchEnabled { get; set; } = false; // фоновое слежение за экраном (ScreenTextWatcher); хоткей работает независимо
    public bool ShowStatusIndicator { get; set; } = true; // полупрозрачный шар состояния в правом нижнем углу (скрыт от захвата экрана)
    public int PollIntervalMs { get; set; } = 2000;     // интервал опроса, мс (250-60000)
    public int MonitorIndex { get; set; } = -1;         // -1 = основной монитор; 0..N = конкретный
    public double ChangeThreshold { get; set; } = 0.6;  // доля общих токенов ниже порога = новый текст (0.1-0.95)
    public double CooldownSec { get; set; } = 20.0;       // пауза после отправки вопроса, сек (0.3-30)
    public int MinTextLength { get; set; } = 12;        // минимальная длина текста-кандидата, символов
    public bool AutoAnswer { get; set; } = true;        // отправлять вопрос в LLM автоматически
    // Движок распознавания: "tesseract" (по умолчанию) или "windows" (WinRT Windows.Media.Ocr).
    // Неизвестные значения трактуются как "tesseract" — не ломаемся на ручной правке конфига.
    // Оба движка работают на CPU — GPU-выбора для OCR нет (см. docs/BACKLOG.md п.8).
    public string Engine { get; set; } = "tesseract";
    // Кадры шире этого порога ужимаются целочисленным даунскейлом перед OCR (скорость):
    // полный экран 4K без даунскейла распознаётся 5-7 с, после /2 — ~1-2 с.
    // 0 = без даунскейла (координаты слов остаются экранными). Не выносится в UI.
    public int MaxOcrWidth { get; set; } = 2560;
}

public class HotkeysConfig
{
    public string ToggleVisibility { get; set; } = "Ctrl+Shift+H";
    public string ChangeOpacity { get; set; } = "Ctrl+Shift+O";
    public string OpenSettings { get; set; } = "Ctrl+Shift+S";
    public string TogglePause { get; set; } = "Ctrl+Shift+P";
    public string ToggleKaraoke { get; set; } = "Ctrl+Shift+K";
    // Экран OCR: вкл/выкл распознавание экрана (вопросы с экрана → LLM).
    public string ToggleScreenOcr { get; set; } = "Ctrl+Shift+X";
    // Экран OCR: разовый проход прямо сейчас (тот же конвейер, что у кнопки «Распознать сейчас»).
    public string ScreenOcrRecognizeNow { get; set; } = "Ctrl+Shift+N";
    // Возврат иконки трея, когда Ui.HideTrayIcon скрыл её из трея.
    public string ShowTrayIcon { get; set; } = "Ctrl+Shift+T";
    // Ask Memory: окно свободного вопроса к памяти (спека «ASK MEMORY»).
    public string AskMemory { get; set; } = "Ctrl+Shift+M";
    // Memory ON/OFF: быстрый тогл UseMemory для текущей встречи + дефолт для новых.
    public string MemoryToggle { get; set; } = "Ctrl+Shift+G";
}

public class AdvancedConfig
{
    public string SystemPrompt { get; set; } =
        "Ты — скрытый ассистент кандидата на техническом собеседовании. " +
        "Отвечай СРАЗУ и КОРОТКО (1-3 предложения) готовым текстом, который кандидат может произнести. " +
        "ЗАПРЕЩЕНО: рассуждать вслух, описывать свой анализ, упоминать теги [Динамик]/[Микрофон]/[secret], задавать вопросы, писать мета-комментарии. " +
        "Теги — только метка источника: [Динамик]=вопрос интервьюера, [Микрофон]=реплика кандидата, [secret]=личное указание. " +
        "На любой вопрос давай прямой ответ на русском. " +
        "Если решил молчать — верни ПУСТОЙ ответ (без единого символа). НЕ пиши слова вроде (empty), «пустой ответ», «silent», скобок или иероглифов.";
    public int HistorySize { get; set; } = 15;
    // 1500 (2.28.2, было 400): reasoning-модели (deepseek-r*/v*, o*, qwq) сжигали
    // ~390 токенов на скрытые «размышления» — видимый ответ рвался «…» (finish_reason=length).
    public int MaxResponseTokens { get; set; } = 1500;
    // Default=true (2.18.0, требование владельца — «стриминг включён из коробки»).
    // Защита от шлюзов, игнорирующих max_tokens в SSE, остаётся: OpenAiClient сам
    // отключает стрим на сессию при симптоме (latency ×3).
    public bool StreamLlmAnswers { get; set; } = true;
    // Кастомный системный промпт для AI-саммари встречи (FileTranscriptionWindow).
    // Пусто = встроенный дефолт. Редактируется в настройках (2.18.0).
    public string SummaryPrompt { get; set; } = string.Empty;
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

/// <summary>
/// Память встреч: извлечение фактов/решений из транскриптов и подмешивание их
/// в контекст LLM (recall). С 2.16.0 включена по умолчанию («из коробки»):
/// E5-модель бандлится в Full-инсталлятор, в Lite докачивается по кнопке.
/// Ошибки памяти не должны ломать audio/STT/assistant.
/// </summary>
public class MemoryConfig
{
    public bool Enabled { get; set; } = true;                   // память включена по умолчанию (2.16.0)
    public bool DefaultUseMemory { get; set; } = true;          // читать прошлую память в новых встречах
    public bool DefaultWriteMemory { get; set; } = true;        // запоминать новую встречу
    public string SaveMode { get; set; } = "smart";             // off|summary|smart|full
    public string ReviewMode { get; set; } = "automatic";       // automatic|review|manual
    public string MemoryModelMode { get; set; } = "same";       // same|separate|disabled — LLM для извлечения
    public string MemoryProviderId { get; set; } = "";          // провайдер отдельной memory-модели (при MemoryModelMode=separate)
    public string MemoryModel { get; set; } = "";               // модель отдельной memory-модели
    public string EmbeddingsMode { get; set; } = "local";       // disabled|local|openai (2.16.0: local — e5 из коробки)
    public string EmbeddingsModel { get; set; } = "";           // openai: имя модели; local: явный путь к каталогу модели
    public string LocalEmbeddingModel { get; set; } = "multilingual-e5-small"; // id локальной модели (в EmbeddingTag)
    public string LocalEmbeddingRuntimeMode { get; set; } = "balanced"; // alwaysloaded|balanced|memorysaver
    public int LocalEmbeddingUnloadAfterIdleMinutes { get; set; } = 10; // idle-выгрузка для balanced: 1|5|10|30|60
    public bool AutoRecall { get; set; } = true;                // автоподмешивание блока <memory_context> в промпт
    public int RecallMaxTokens { get; set; } = 2000;            // лимит токенов блока контекста памяти
    public int RecallMaxItems { get; set; } = 8;                // макс. фактов в блоке контекста
    public string TranscriptRetention { get; set; } = "days30"; // never|days7|days30|days90|forever — срок хранения сегментов
    public string MemoryRetention { get; set; } = "forever";    // forever|days30|days90|custom — срок хранения фактов
    public int MemoryRetentionDays { get; set; } = 0;           // дни хранения при MemoryRetention=custom
    public double DedupeSimilarity { get; set; } = 0.92;        // cos-порог дедупликации на нормализованном тексте/эмбеддинге
    public int JobStaleSec { get; set; } = 300;                 // running-джоба старше этого => retryable
    public string TranscriptExtractRules { get; set; } = "";    // доп. правила пользователя для промпта извлечения фактов (2.17.0)
    public long? ActiveProjectId { get; set; }                  // выбранный проект для новых встреч (null = Global)
}
