using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Audio;

namespace BrainstormBuddy.Config;

public class SettingsViewModel : INotifyPropertyChanged
{
    private AppConfig _config;
    private readonly List<string> _presets = new()
    {
        "Groq (llama3-8b-8192, t-one)",
        "OpenAI (gpt-4o-mini, t-one)",
        "OpenRouter (openai/gpt-oss-120b, t-one)",
        "OpenRouter (openrouter/free, t-one)",
        "NVIDIA NIM (meta/llama-3.3-70b-instruct, t-one)",
        "LocalAI (local model, t-one)",
        "Custom"
    };

    private const string SourceDataSection =
        "## ИСТОЧНИК ДАННЫХ\n" +
        "Ты получаешь транскрибацию речи через STT (speech-to-text) — это результат распознавания русской речи.\n\n" +
        "Важно:\n" +
        "- Текст размечен тегами: [Динамик] (системный звук), [Микрофон] (микрофон пользователя), [secret] (ручное сообщение)\n" +
        "- STT может содержать ошибки распознавания, опечатки, отсутствовать пунктуация\n" +
        "- Анализируй даже обрывки фраз и извлекай из них техническую суть\n" +
        "- Английских букв в тексте НЕТ — только русский язык\n" +
        "- Если что-то распознано нечётко — отвечай по контексту, не запрашивай уточнений\n\n";

    // Единые правила суфлёра для всех пресетов (по аналогии с отточенным мульти-агентным
    // промптом собеседования): помогаем ПОЛЬЗОВАТЕЛЮ (владельцу микрофона), а не собеседнику.
    private const string SuffleurRules =
        "## КАК ОТВЕЧАТЬ\n" +
        "- Ты суфлёр ПОЛЬЗОВАТЕЛЯ (владельца микрофона [Микрофон]). Твой вывод — готовая реплика, которую он может сказать вслух.\n" +
        "- [Динамик] — собеседник (другая сторона). Отвечай на его вопросы и реплики.\n" +
        "- [Микрофон] — сам пользователь. Это КОНТЕКСТ (что он уже сказал): учитывай, но не отвечай на него и не молчи ради ответа.\n" +
        "- [secret] — прямая команда пользователя тебе: выполни её обязательно и в первую очередь.\n" +
        "- Нечего добавить, идёт small talk, шум или обрывок — молчи (пустой ответ), не комментируй.\n" +
        "- Русский язык, по делу, без воды. НИКАКИХ служебных пометок, ролей и тегов в ответе — только сама реплика.\n" +
        "- Не выдумывай факты, цифры и названия. Бери их из разговора; чего не знаешь — формулируй общими словами.\n\n";

    private static readonly List<NamedPrompt> DefaultPromptPresets = new()
    {
        new NamedPrompt
        {
            Name = "Собеседование: шпаргалка",
            Content = "Ты — суфлёр КАНДИДАТА на собеседовании. Помогаешь пользователю (кандидату) отвечать.\n" +
                "- [Динамик] — интервьюер: вопросы, уточнения, задачи.\n" +
                "- [Микрофон] — сам кандидат (пользователь): его ответы — это контекст.\n\n" +
                SourceDataSection +
                SuffleurRules +
                "## ЗАДАЧА\n" +
                "- На вопрос интервьюера дай ГОТОВЫЙ ответ от первого лица — так, чтобы кандидат просто повторил.\n" +
                "- Проектный/поведенческий вопрос → короткий STAR: ситуация → задача → действия → результат (без выдуманных цифр).\n" +
                "- Теоретический вопрос → суть за 2-4 предложения, точно и без воды.\n" +
                "- Не задавай вопросов интервьюеру и не советуй, «что спросить» — ты на стороне кандидата."
        },
        new NamedPrompt
        {
            Name = "Совещание: аналитика",
            Content = "Ты — аналитик рядом с пользователем на рабочем совещании.\n" +
                "- [Динамик] — коллеги, докладчики.\n" +
                "- [Микрофон] — сам пользователь.\n\n" +
                SourceDataSection +
                SuffleurRules +
                "## ЗАДАЧА\n" +
                "- Подсвечивай риски и неочевидные последствия обсуждаемых решений.\n" +
                "- Предлагай альтернативы и удачные формулировки для возражений/предложений — готовой репликой.\n" +
                "- После длинного блока речи резюмируй ключевой тезис одним предложением.\n" +
                "- Конкретика: называй цифры, сроки, имена, если они звучали."
        },
        new NamedPrompt
        {
            Name = "Мозговой штурм",
            Content = "Ты — партнёр пользователя по мозговому штурму.\n" +
                "- [Динамик] — другие участники. [Микрофон] — сам пользователь.\n\n" +
                SourceDataSection +
                SuffleurRules +
                "## ЗАДАЧА\n" +
                "На услышанную идею дай 3 неочевидных развития или альтернативы. Формат: «1) … 2) … 3) …». Каждое — одна законченная мысль, коротко."
        },
        new NamedPrompt
        {
            Name = "Протокол встречи",
            Content = "Ты — секретарь встречи рядом с пользователем.\n" +
                "- [Динамик] — участники. [Микрофон] — сам пользователь.\n\n" +
                SourceDataSection +
                SuffleurRules +
                "## ЗАДАЧА\n" +
                "После каждого значимого блока речи формулируй краткий тезис (1 предложение) для протокола: решение, задача, ответственный, срок. Приветствия, small talk и паузы игнорируй."
        },
        new NamedPrompt
        {
            Name = "Продажи: работа с возражениями",
            Content = "Ты — суфлёр МЕНЕДЖЕРА по продажам. Помогаешь пользователю (менеджеру) вести диалог.\n" +
                "- [Динамик] — клиент: вопросы, возражения.\n" +
                "- [Микрофон] — сам менеджер (пользователь).\n\n" +
                SourceDataSection +
                SuffleurRules +
                "## ЗАДАЧА\n" +
                "На возражение клиента дай готовый ответ от первого лица по технике «присоединение → аргумент → вопрос». 2-4 предложения, тёплый деловой тон, без давления."
        },
        new NamedPrompt
        {
            Name = "Перевод RU-EN",
            Content = "You are a live interpreter for the user.\n\n" +
                "## SOURCE\n" +
                "Speech via STT. Tags: [Динамик] = the other side, [Микрофон] = the user, [secret] = manual message. STT may have errors, typos, missing punctuation.\n\n" +
                "## TASK\n" +
                "Translate each utterance into the opposite language: Russian→English, English→Russian. Preserve meaning and tone, be concise (1-2 sentences). Output only the translation — no notes, no tags. Translate [secret] too."
        },
        new NamedPrompt
        {
            Name = "Технический консультант",
            Content = "Ты — senior инженер-консультант рядом с пользователем в технической дискуссии.\n" +
                "- [Динамик] — собеседник(и). [Микрофон] — сам пользователь.\n\n" +
                SourceDataSection +
                SuffleurRules +
                "## ЗАДАЧА\n" +
                "На технический вопрос или проблему дай конкретный ответ: команда, архитектурный паттерн, best practice, короткое определение термина. Код оборачивай в ```. Без общих слов — по сути."
        }
    };

    public SettingsViewModel(AppConfig config, IApiClient? apiClient = null)
    {
        _config = config;
        ApiClient = apiClient;
        Presets = new ObservableCollection<string>(_presets);
        AudioDevices = new ObservableCollection<AudioDeviceInfo>(AudioDeviceEnumerator.GetAvailableDevices());
        SelectedPreset = DetectPreset();

        if (_config.Advanced.SystemPromptPresets == null || _config.Advanced.SystemPromptPresets.Count == 0)
        {
            _config.Advanced.SystemPromptPresets = new List<NamedPrompt>(DefaultPromptPresets);
        }
        else
        {
            // Освежаем встроенные пресеты до актуальных текстов (по имени), сохраняя
            // пользовательские. Так отточенные промпты доезжают до уже сохранённых конфигов.
            foreach (var def in DefaultPromptPresets)
            {
                var existing = _config.Advanced.SystemPromptPresets.FirstOrDefault(p => p.Name == def.Name);
                if (existing != null) existing.Content = def.Content;
                else _config.Advanced.SystemPromptPresets.Add(def);
            }
        }
        SystemPromptPresets = new ObservableCollection<NamedPrompt>(_config.Advanced.SystemPromptPresets);
        if (!string.IsNullOrEmpty(_config.Advanced.ActiveSystemPromptName))
        {
            SelectedSystemPromptPreset = SystemPromptPresets.FirstOrDefault(p => p.Name == _config.Advanced.ActiveSystemPromptName);
        }
        if (SelectedSystemPromptPreset == null && SystemPromptPresets.Count > 0)
        {
            SelectedSystemPromptPreset = SystemPromptPresets[0];
        }
        if (SelectedSystemPromptPreset != null)
        {
            CurrentSystemPrompt = SelectedSystemPromptPreset.Content;
        }
    }

    public IApiClient? ApiClient { get; }
    public ObservableCollection<string> Presets { get; }
    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; }
    public ObservableCollection<NamedPrompt> SystemPromptPresets { get; }

    public AppConfig Config => _config;

    public string BaseUrl
    {
        get => _config.Api.BaseUrl;
        set { _config.Api.BaseUrl = value; OnPropertyChanged(); }
    }

    public string ApiKey
    {
        get => _config.Api.ApiKey;
        set { _config.Api.ApiKey = value; OnPropertyChanged(); }
    }

    public string ChatModel
    {
        get => _config.Api.ChatModel;
        set { _config.Api.ChatModel = value; OnPropertyChanged(); }
    }

    public string SttModel
    {
        get => _config.Api.SttModel;
        set { _config.Api.SttModel = value; OnPropertyChanged(); }
    }

    public string? SttBaseUrl
    {
        get => string.IsNullOrEmpty(_config.Api.SttBaseUrl) ? null : _config.Api.SttBaseUrl;
        set { _config.Api.SttBaseUrl = value ?? string.Empty; OnPropertyChanged(); }
    }

    public double RmsThreshold
    {
        get => _config.Audio.RmsThreshold;
        set { _config.Audio.RmsThreshold = value; OnPropertyChanged(); }
    }

    // Авто-калибровка порога распознавания. ВКЛ (дефолт) → приложение само подстраивает порог
    // под уровень звука; ползунок порога неактивен. ВЫКЛ → ручной порог (ползунок).
    public bool AutoCalibrateThreshold
    {
        get => _config.Audio.AutoCalibrateThreshold;
        set { _config.Audio.AutoCalibrateThreshold = value; OnPropertyChanged(); OnPropertyChanged(nameof(ManualThreshold)); }
    }
    /// <summary>Ручной порог активен только когда авто-калибровка ВЫКЛючена.</summary>
    public bool ManualThreshold => !_config.Audio.AutoCalibrateThreshold;

    public bool MicOnly
    {
        get => _config.Audio.MicOnly;
        set { _config.Audio.MicOnly = value; OnPropertyChanged(); }
    }

    public bool CaptureMic
    {
        get => _config.Audio.CaptureMic;
        set { _config.Audio.CaptureMic = value; OnPropertyChanged(); }
    }

    public int VadModeUi
    {
        get => _config.Audio.VadMode;
        set { _config.Audio.VadMode = value; OnPropertyChanged(); }
    }

    public bool MergeMicLoopback
    {
        get => _config.Advanced.MergeMicLoopback;
        set { _config.Advanced.MergeMicLoopback = value; OnPropertyChanged(); }
    }

    public int HistorySize
    {
        get => _config.Advanced.HistorySize;
        set { _config.Advanced.HistorySize = value; OnPropertyChanged(); }
    }

    public int MaxResponseTokensVm
    {
        get => _config.Advanced.MaxResponseTokens;
        set { _config.Advanced.MaxResponseTokens = value; OnPropertyChanged(); }
    }

    public bool EnableDebugLogs
    {
        get => _config.Audio.EnableDebugLogs;
        set { _config.Audio.EnableDebugLogs = value; OnPropertyChanged(); }
    }

    // — Диагностика / логи —
    public bool EnableFileLogging
    {
        get => _config.Logging.Enabled;
        set { _config.Logging.Enabled = value; OnPropertyChanged(); }
    }

    public bool VerboseLogging
    {
        get => _config.Logging.Verbose;
        set { _config.Logging.Verbose = value; OnPropertyChanged(); }
    }

    public int LogMaxFiles
    {
        get => _config.Logging.MaxFiles;
        set { _config.Logging.MaxFiles = Math.Max(1, value); OnPropertyChanged(); }
    }

    public double SilenceThresholdMs
    {
        get => _config.Audio.SilenceSeconds * 1000;
        set { _config.Audio.SilenceSeconds = value / 1000.0; OnPropertyChanged(); }
    }

    public int MinSpeechMsUi
    {
        get => _config.Audio.MinSpeechMs;
        set { _config.Audio.MinSpeechMs = value; OnPropertyChanged(); }
    }

    public int PreRollMsUi
    {
        get => _config.Audio.PreRollMs;
        set { _config.Audio.PreRollMs = value; OnPropertyChanged(); }
    }

    public int PostRollMsUi
    {
        get => _config.Audio.PostRollMs;
        set { _config.Audio.PostRollMs = value; OnPropertyChanged(); }
    }

    private string _selectedAudioPreset = "Свой";
    public string SelectedAudioPreset
    {
        get => _selectedAudioPreset;
        set
        {
            _selectedAudioPreset = value;
            ApplyAudioPreset(value);
            OnPropertyChanged();
        }
    }

    public ObservableCollection<string> AudioPresets { get; } = new()
    {
        "Свой",
        "Стандарт",
        "Быстрая речь",
        "Медленная/Вдумчивая"
    };

    // Режим нарезки речи (эндпойнтинг). В авто-режимах порог тишины подстраивается сам,
    // ручные пресеты/слайдер ниже становятся неактивны (их держит контроллер).
    public ObservableCollection<string> EndpointModes { get; } = new()
    {
        "Ручной (пресеты ниже)",
        "Авто — по паузам",
        "Авто — умная склейка",
    };

    public string SelectedEndpointMode
    {
        get => _config.Audio.EndpointMode?.ToLowerInvariant() switch
        {
            "adaptive" => "Авто — по паузам",
            "semantic" => "Авто — умная склейка",
            _ => "Ручной (пресеты ниже)",
        };
        set
        {
            _config.Audio.EndpointMode = value switch
            {
                "Авто — по паузам" => "adaptive",
                "Авто — умная склейка" => "semantic",
                _ => "manual",
            };
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsManualEndpoint));
        }
    }

    /// <summary>true в ручном режиме — тогда пресеты/слайдер порога активны.</summary>
    public bool IsManualEndpoint =>
        string.Equals(_config.Audio.EndpointMode, "manual", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrEmpty(_config.Audio.EndpointMode);

    private void ApplyAudioPreset(string preset)
    {
        switch (preset)
        {
            case "Стандарт":
                SilenceThresholdMs = 1800;
                MinSpeechMsUi = 1000;
                PreRollMsUi = 400;
                PostRollMsUi = 500;
                break;
            case "Быстрая речь":
                SilenceThresholdMs = 600;
                MinSpeechMsUi = 300;
                PreRollMsUi = 150;
                PostRollMsUi = 150;
                break;
            case "Медленная/Вдумчивая":
                SilenceThresholdMs = 2000;
                MinSpeechMsUi = 2000;
                PreRollMsUi = 500;
                PostRollMsUi = 500;
                break;
        }
    }

    public double WindowOpacity
    {
        get => _config.Ui.WindowOpacity;
        set { _config.Ui.WindowOpacity = value; OnPropertyChanged(); }
    }

    public string WindowPosition
    {
        get => _config.Ui.WindowPosition;
        set { _config.Ui.WindowPosition = value; OnPropertyChanged(); }
    }


    public string Theme
    {
        get => _config.Ui.Theme;
        set
        {
            if (_config.Ui.Theme != value)
            {
                _config.Ui.Theme = value;
                OnPropertyChanged();
                ThemeChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<string>? ThemeChanged;
    public string SavePath
    {
        get => _config.Ui.SavePath;
        set { _config.Ui.SavePath = value; OnPropertyChanged(); }
    }

    public bool ShowCompatibilityBorder
    {
        get => _config.Ui.ShowCompatibilityBorder;
        set { _config.Ui.ShowCompatibilityBorder = value; OnPropertyChanged(); }
    }

    public string ToggleVisibilityHotkey
    {
        get => _config.Hotkeys.ToggleVisibility;
        set { _config.Hotkeys.ToggleVisibility = value; OnPropertyChanged(); }
    }

    public string ChangeOpacityHotkey
    {
        get => _config.Hotkeys.ChangeOpacity;
        set { _config.Hotkeys.ChangeOpacity = value; OnPropertyChanged(); }
    }

    public string OpenSettingsHotkey
    {
        get => _config.Hotkeys.OpenSettings;
        set { _config.Hotkeys.OpenSettings = value; OnPropertyChanged(); }
    }

    public string TogglePauseHotkey
    {
        get => _config.Hotkeys.TogglePause;
        set { _config.Hotkeys.TogglePause = value; OnPropertyChanged(); }
    }

    public int RateLimitSeconds
    {
        get => _config.Api.RateLimitSeconds;
        set { _config.Api.RateLimitSeconds = value; OnPropertyChanged(); }
    }

    public bool ClickThrough
    {
        get => _config.Ui.ClickThrough;
        set { _config.Ui.ClickThrough = value; OnPropertyChanged(); }
    }

    public int FontSize
    {
        get => _config.Ui.FontSize;
        set { _config.Ui.FontSize = value; OnPropertyChanged(); }
    }

    public bool ShowTimingLine
    {
        get => _config.Ui.ShowTimingLine;
        set { _config.Ui.ShowTimingLine = value; OnPropertyChanged(); }
    }

    // Инженерный режим: показывать на главном оверлее тонкие настройки (слайдеры паузы/чанка,
    // пресеты скорости речи, эквалайзер). Применяется по «Сохранить».
    public bool EngineerMode
    {
        get => _config.Ui.EngineerMode;
        set { _config.Ui.EngineerMode = value; OnPropertyChanged(); }
    }

    public string SystemPrompt
    {
        get => _config.Advanced.SystemPrompt;
        set { _config.Advanced.SystemPrompt = value; OnPropertyChanged(); }
    }

    private NamedPrompt? _selectedSystemPromptPreset;
    public NamedPrompt? SelectedSystemPromptPreset
    {
        get => _selectedSystemPromptPreset;
        set
        {
            if (_selectedSystemPromptPreset != null)
            {
                _selectedSystemPromptPreset.Content = CurrentSystemPrompt;
            }
            _selectedSystemPromptPreset = value;
            if (value != null)
            {
                CurrentSystemPrompt = value.Content;
                _config.Advanced.ActiveSystemPromptName = value.Name;
            }
            OnPropertyChanged();
        }
    }

    private string _currentSystemPrompt = string.Empty;
    public string CurrentSystemPrompt
    {
        get => _currentSystemPrompt;
        set
        {
            _currentSystemPrompt = value;
            _config.Advanced.SystemPrompt = value;
            if (_selectedSystemPromptPreset != null)
            {
                _selectedSystemPromptPreset.Content = value;
            }
            OnPropertyChanged();
        }
    }

    public void SaveAsNewPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var existing = SystemPromptPresets.FirstOrDefault(p => p.Name == name);
        if (existing != null)
        {
            existing.Content = CurrentSystemPrompt;
        }
        else
        {
            var np = new NamedPrompt { Name = name, Content = CurrentSystemPrompt };
            SystemPromptPresets.Add(np);
            _config.Advanced.SystemPromptPresets.Add(np);
        }
        OnPropertyChanged(nameof(SystemPromptPresets));
    }

    public void DeleteSelectedPreset()
    {
        if (_selectedSystemPromptPreset == null) return;
        var name = _selectedSystemPromptPreset.Name;
        SystemPromptPresets.Remove(_selectedSystemPromptPreset);
        _config.Advanced.SystemPromptPresets.RemoveAll(p => p.Name == name);
        SelectedSystemPromptPreset = SystemPromptPresets.FirstOrDefault();
    }

    private string _selectedPreset = "Custom";
    public string SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            _selectedPreset = value;
            ApplyPreset(value);
            OnPropertyChanged();
        }
    }

    private string _connectionStatus = string.Empty;
    public string ConnectionStatus
    {
        get => _connectionStatus;
        set { _connectionStatus = value; OnPropertyChanged(); }
    }

    private bool _isApiKeyVisible;
    public bool IsApiKeyVisible
    {
        get => _isApiKeyVisible;
        set { _isApiKeyVisible = value; OnPropertyChanged(); }
    }

    private string _currentRms = "0.000";
    public string CurrentRms
    {
        get => _currentRms;
        set { _currentRms = value; OnPropertyChanged(); }
    }

    public bool IsLocalSttEnabled
    {
        get => _config.LocalStt.Enabled;
        set { _config.LocalStt.Enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLocalSttDisabled)); }
    }

    public bool IsLocalSttDisabled => !IsLocalSttEnabled;

    public string LocalSttModel
    {
        get => _config.LocalStt.Model;
        set { _config.LocalStt.Model = value; OnPropertyChanged(); }
    }

    public ObservableCollection<string> LocalSttModels { get; } = new()
    {
        "gigaam-v2",
        "gigaam-e2e"
    };

    private string _localSttStatus = "Остановлен";
    public string LocalSttStatus
    {
        get => _localSttStatus;
        set { _localSttStatus = value; OnPropertyChanged(); }
    }

    private string _localSttBuildLog = string.Empty;
    public string LocalSttBuildLog
    {
        get => _localSttBuildLog;
        set { _localSttBuildLog = value; OnPropertyChanged(); }
    }

    private string _localSttCpu = "-";
    public string LocalSttCpu
    {
        get => _localSttCpu;
        set { _localSttCpu = value; OnPropertyChanged(); }
    }

    private string _localSttRam = "-";
    public string LocalSttRam
    {
        get => _localSttRam;
        set { _localSttRam = value; OnPropertyChanged(); }
    }

    private string _localSttPing = "-";
    public string LocalSttPing
    {
        get => _localSttPing;
        set { _localSttPing = value; OnPropertyChanged(); }
    }

    private string _localSttUptime = "-";
    public string LocalSttUptime
    {
        get => _localSttUptime;
        set { _localSttUptime = value; OnPropertyChanged(); }
    }

    private string _localSttTestResult = string.Empty;
    public string LocalSttTestResult
    {
        get => _localSttTestResult;
        set { _localSttTestResult = value; OnPropertyChanged(); }
    }

    public void UpdateRms(double rms)
    {
        CurrentRms = rms.ToString("F3");
        OnPropertyChanged(nameof(CurrentRms));
    }

    // Живой эффективный порог детекции (в авто-режиме — калиброванный). Обновляется из App
    // тем же таймером, что и RMS; настройки показывают его вместо статичного ползунка.
    private double _liveEffectiveThreshold = 0.01;
    public double LiveEffectiveThreshold
    {
        get => _liveEffectiveThreshold;
        set { _liveEffectiveThreshold = value; OnPropertyChanged(); }
    }
    public void UpdateEffectiveThreshold(double threshold)
    {
        LiveEffectiveThreshold = threshold;
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        OnPropertyChanged(string.Empty);
    }

    private void ApplyPreset(string preset)
    {
        // Пресеты задают ТОЛЬКО LLM (BaseUrl/ChatModel). STT настраивается отдельно в разделе
        // «Распознавание речи (STT)» — пресет его не трогает (иначе затирал бы адрес пользователя).
        switch (preset)
        {
            case "Groq (llama3-8b-8192, t-one)":
                BaseUrl = "https://api.groq.com/openai/v1";
                ChatModel = "llama3-8b-8192";
                break;
            case "OpenAI (gpt-4o-mini, t-one)":
                BaseUrl = "https://api.openai.com/v1";
                ChatModel = "gpt-4o-mini";
                break;
            case "OpenRouter (openai/gpt-oss-120b, t-one)":
                BaseUrl = "https://openrouter.ai/api/v1";
                ChatModel = "openai/gpt-oss-120b";
                break;
            case "OpenRouter (openrouter/free, t-one)":
                BaseUrl = "https://openrouter.ai/api/v1";
                ChatModel = "openrouter/free";
                break;
            case "NVIDIA NIM (meta/llama-3.3-70b-instruct, t-one)":
                BaseUrl = "https://integrate.api.nvidia.com/v1";
                ChatModel = "meta/llama-3.3-70b-instruct";
                break;
            case "LocalAI (local model, t-one)":
                BaseUrl = "http://localhost:8080/v1";
                ChatModel = "gpt-4o-mini";
                break;
        }
    }

    private string DetectPreset()
    {
        if (_config.Api.BaseUrl.Contains("groq.com")) return _presets[0];
        if (_config.Api.BaseUrl.Contains("api.openai.com")) return _presets[1];
        if (_config.Api.BaseUrl.Contains("openrouter.ai"))
            return _config.Api.ChatModel.Contains("gpt-oss") ? _presets[2] : _presets[3];
        if (_config.Api.BaseUrl.Contains("integrate.api.nvidia.com")) return _presets[4];
        if (_config.Api.BaseUrl.Contains("localhost")) return _presets[5];
        return _presets[6];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));

    // ===== Multi-Agent Properties =====

    public bool IsMultiAgentEnabled
    {
        get => _config.MultiAgent.Enabled;
        set { _config.MultiAgent.Enabled = value; OnPropertyChanged(); }
    }

    public string ActiveScenarioId
    {
        get => _config.MultiAgent.ActiveScenarioId;
        set { _config.MultiAgent.ActiveScenarioId = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActiveScenario)); OnPropertyChanged(nameof(ActiveScenarioAgents)); }
    }

    public ScenarioConfig? ActiveScenario => _config.MultiAgent.Scenarios.FirstOrDefault(s => s.Id == ActiveScenarioId);

    public ObservableCollection<ScenarioConfig> Scenarios => new(_config.MultiAgent.Scenarios);

    public List<AgentConfig> ActiveScenarioAgents => ActiveScenario?.Agents ?? new();

    // User Profile
    public string UserProfileSummary
    {
        get => _config.MultiAgent.UserProfile.Summary;
        set { _config.MultiAgent.UserProfile.Summary = value; OnPropertyChanged(); }
    }
    public string UserProfileCannotDo
    {
        get => _config.MultiAgent.UserProfile.CannotDo;
        set { _config.MultiAgent.UserProfile.CannotDo = value; OnPropertyChanged(); }
    }
    public ObservableCollection<string> UserProfileCases => new(_config.MultiAgent.UserProfile.Cases);
    public ObservableCollection<string> UserProfileTechSkills => new(_config.MultiAgent.UserProfile.TechnicalSkills);
    public ObservableCollection<string> UserProfileSoftSkills => new(_config.MultiAgent.UserProfile.SoftSkills);

    // ===== Сессионный LLM-лог (Настройки → Диагностика) =====
    // Храним готовые СТРОКИ, а не объекты: пишут два источника с разными форматами
    // (одиночный LLM через событие OpenAiClient.Exchange и мульти-агент через LlmLogEntry),
    // а показывается всё одним TextBox. Новые записи сверху. Лимит ~200 — защита памяти
    // на долгих сессиях; для истории есть файловый app.log.
    private readonly List<string> _llmLogLines = new();
    private const int MaxLlmLogLines = 200;

    private string _llmLogText = "";
    public string LlmLogText
    {
        get => _llmLogText;
        set { _llmLogText = value; OnPropertyChanged(); }
    }

    public void AddLlmLogLine(string line)
    {
        _llmLogLines.Insert(0, line);
        while (_llmLogLines.Count > MaxLlmLogLines) _llmLogLines.RemoveAt(_llmLogLines.Count - 1);
        UpdateLlmLogText();
    }

    public void AddLlmLog(LlmLogEntry entry) => AddLlmLogLine(entry.Format());

    public void ClearLlmLogs()
    {
        _llmLogLines.Clear();
        UpdateLlmLogText();
    }

    private void UpdateLlmLogText()
        => LlmLogText = string.Join(Environment.NewLine, _llmLogLines);
}

public class LlmLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ScenarioName { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Color { get; set; } = "";
    public string UserText { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string Response { get; set; } = "";
    public long LatencyMs { get; set; }
    public bool IsSilent { get; set; }
    public bool IsError { get; set; }
    public string Status => IsError ? "✗" : "✓";
    public string DisplayText => IsSilent ? "[SILENT]" : Response;

    public string Format()
    {
        var ts = Timestamp.ToString("HH:mm:ss");
        return $"[{ts}] SCENARIO={ScenarioName} AGENT={AgentName} STATUS={(IsError ? "ERR" : "200")} TIME={LatencyMs}ms\nSYSTEM: {SystemPrompt}\nUSER: {UserText}\nRESPONSE: {DisplayText}\n";
    }
}
