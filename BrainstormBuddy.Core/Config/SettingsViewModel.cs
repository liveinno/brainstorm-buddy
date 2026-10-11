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
        "Groq (openai/gpt-oss-120b, t-one)",
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
        "- Нечего добавить, идёт small talk, шум или обрывок — молчи: верни ПУСТОЙ ответ (без единого символа). НЕ пиши слова вроде (empty), «пустой ответ», «silent», скобок или иероглифов.\n" +
        "- НИКОГДА не показывай свои рассуждения, анализ или ход мыслей — выводи только готовый текст ответа, который пользователь может произнести вслух.\n" +
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
                "Translate each utterance into the opposite language: Russian→English, English→Russian. Preserve meaning and tone, be concise (1-2 sentences). Output only the translation — never show reasoning, analysis, meta-comments or tags. Translate [secret] too."
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
        },
        new NamedPrompt
        {
            Name = "Обучение (лекция)",
            Content = "Ты — суфлёр пользователя на ЛЕКЦИИ/обучении.\n" +
                "- [Динамик] — преподаватель: монолог, объяснения, вопросы аудитории.\n" +
                "- [Микрофон] — сам пользователь: его реплики — контекст.\n\n" +
                SourceDataSection +
                SuffleurRules +
                "## РЕЖИМ: СЛУШАТЬ\n" +
                "Это НЕ диалог: преподаватель ведёт монолог. НЕ комментируй каждую фразу — при пустом ответе приложение просто слушает дальше.\n\n" +
                "## ЗАДАЧА\n" +
                "Отвечай ТОЛЬКО когда преподаватель задаёт ЯВНЫЙ ВОПРОС (вопросительная интонация/фраза, «как вы думаете», «кто помнит», «как называется…», вопрос в конце). На явный вопрос дай короткий готовый ответ (1-3 предложения, точный факт).\n\n" +
                "МОЛЧИ (верни ПУСТОЙ ответ, без единого символа) на: изложение материала, примеры, истории, риторические обращения («понятно?», «идём дальше»), паузы и шум. НЕ пиши слова вроде (empty), «пустой ответ», «silent», скобок или иероглифов."
        }
    };

    public SettingsViewModel(AppConfig config, IApiClient? apiClient = null)
    {
        _config = config;
        ApiClient = apiClient;
        Presets = new ObservableCollection<string>(_presets);
        AudioDevices = new ObservableCollection<AudioDeviceInfo>(AudioDeviceEnumerator.GetAvailableDevices());
        // Только вычисляем подпись пресета — присваиваем ПОЛЕ, а не свойство: setter → ApplyPreset
        // затирал бы сохранённые BaseUrl/ChatModel при каждом старте (SettingsVm создаётся в App.OnStartup).
        _selectedPreset = DetectPreset();

        if (_config.Advanced.SystemPromptPresets == null || _config.Advanced.SystemPromptPresets.Count == 0)
        {
            _config.Advanced.SystemPromptPresets = new List<NamedPrompt>(DefaultPromptPresets);
        }
        else
        {
            // Освежаем встроенные пресеты до актуальных текстов (по имени), сохраняя
            // пользовательские. Так отточенные промпты доезжают до уже сохранённых конфигов.
            // Новый встроенный пресет вставляем в позицию по порядку DefaultPromptPresets
            // (иначе «Обучение (лекция)» приземлился бы в хвост, а не после «Технический
            // консультант» — заданный порядок комбо ломался у старых конфигов).
            for (var i = 0; i < DefaultPromptPresets.Count; i++)
            {
                var def = DefaultPromptPresets[i];
                var list = _config.Advanced.SystemPromptPresets;
                var existing = list.FirstOrDefault(p => p.Name == def.Name);
                if (existing != null) { existing.Content = def.Content; continue; }

                var insertAt = -1;
                for (var j = i - 1; j >= 0 && insertAt < 0; j--)
                {
                    var pos = list.FindIndex(p => p.Name == DefaultPromptPresets[j].Name);
                    if (pos >= 0) insertAt = pos + 1;
                }
                if (insertAt < 0) insertAt = list.Count; // предков в конфиге нет — в конец
                list.Insert(insertAt, def);
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

    /// <summary>Id выбранного LLM-профиля (встроенный или custom). Пусто → «не выбран».</summary>
    public string ProviderId
    {
        get => _config.Api.ProviderId;
        set { _config.Api.ProviderId = value; OnPropertyChanged(); }
    }

    /// <summary>Все профили для выпадающего списка: встроенные + пользовательские.</summary>
    public IReadOnlyList<LlmProviderProfile> LlmProviders =>
        LlmProviderRegistry.All(_config.Api.CustomProviders);

    /// <summary>Профиль по Id среди всех (built-in + custom).</summary>
    public LlmProviderProfile? FindLlmProvider(string? id) =>
        string.IsNullOrEmpty(id) ? null : LlmProviderRegistry.FindById(id, _config.Api.CustomProviders);

    /// <summary>Черновик нового custom-провайдера (ещё не сохранён).</summary>
    public LlmProviderProfile NewCustomProvider() => new()
    {
        Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
        Name = "Мой провайдер",
        IsBuiltIn = false
    };

    /// <summary>Копия профиля как черновик custom (для кнопки «дублировать»).</summary>
    public LlmProviderProfile CloneProvider(LlmProviderProfile src) => new()
    {
        Id = "custom-" + Guid.NewGuid().ToString("N")[..8],
        Name = src.Name + " (копия)",
        Protocol = src.Protocol,
        BaseUrl = src.BaseUrl,
        AuthKind = src.AuthKind,
        ExtraHeaders = src.ExtraHeaders.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList(),
        ModelsPath = src.ModelsPath,
        ChatPath = src.ChatPath,
        ContentPath = src.ContentPath,
        UsagePath = src.UsagePath,
        ModelPresets = new List<string>(src.ModelPresets),
        PrivacyNote = src.PrivacyNote,
        ReasoningEffort = src.ReasoningEffort,
        IsBuiltIn = false
    };

    /// <summary>Сохранить custom-профиль в конфиг (новый → добавить, существующий → заменить).</summary>
    public void SaveCustomProvider(LlmProviderProfile profile)
    {
        var list = _config.Api.CustomProviders;
        int idx = list.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0) list[idx] = profile; else list.Add(profile);
        OnPropertyChanged(nameof(LlmProviders));
    }

    /// <summary>Удалить custom-профиль; если он был выбран — сбросить выбор.</summary>
    public void DeleteCustomProvider(string id)
    {
        _config.Api.CustomProviders.RemoveAll(p => p.Id == id);
        if (ProviderId == id) ProviderId = string.Empty;
        OnPropertyChanged(nameof(LlmProviders));
    }

    /// <summary>Перечитать список провайдеров (после правок custom-профилей).</summary>
    public void RefreshLlmProviders() => OnPropertyChanged(nameof(LlmProviders));

    /// <summary>
    /// Список моделей для редактируемого комбо: пресеты провайдера + текущая модель
    /// первым пунктом, если её там нет. Editable ComboBox обнуляет Text, когда
    /// SelectedItem не находится в новом ItemsSource (OnSelectionChanged →
    /// SelectedItemUpdated), и TwoWay-биндинг затирает сохранённый ChatModel —
    /// поэтому текущее значение обязано присутствовать в списке.
    /// </summary>
    public static List<string> ModelItemsWithCurrent(IEnumerable<string>? presets, string? current)
    {
        var list = presets == null ? new List<string>() : new List<string>(presets);
        if (!string.IsNullOrWhiteSpace(current) && !list.Contains(current))
            list.Insert(0, current);
        return list;
    }

    /// <summary>Глобальный reasoning effort (Api.ReasoningEffortOverride) для комбо в UI.
    /// Маппинг getter: null → "по профилю", "" → "не слать", иначе само значение.
    /// Setter терпим к вариантам лейблов: null/"по профилю"/"(по профилю)"/"inherit"/"&lt;null&gt;"
    /// → null (брать из профиля провайдера); ""/"не слать"/"(не слать)" → ""
    /// (поле reasoning не шлём); остальное — как есть (low/medium/high).</summary>
    public string ReasoningEffortUi
    {
        get => _config.Api.ReasoningEffortOverride switch
        {
            null => "по профилю",
            "" => "не слать",
            var v => v
        };
        set
        {
            _config.Api.ReasoningEffortOverride = value switch
            {
                null or "по профилю" or "(по профилю)" or "inherit" or "<null>" => null,
                "" or "не слать" or "(не слать)" => "",
                var v => v
            };
            OnPropertyChanged();
        }
    }

    /// <summary>Дёрнуть INPC для ReasoningEffortUi — при смене провайдера лейбл "(по профилю)"
    /// остаётся тем же, а эффективное значение за ним меняется; UI перечитывает.</summary>
    public void NotifyReasoningEffortChanged() => OnPropertyChanged(nameof(ReasoningEffortUi));

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

    /// <summary>Максимум токенов ответа (config: Advanced.MaxResponseTokens, лимит 50..4096).</summary>
    public int MaxResponseTokens
    {
        get => _config.Advanced.MaxResponseTokens;
        set { _config.Advanced.MaxResponseTokens = Math.Clamp(value, 50, 4096); OnPropertyChanged(); }
    }

    public bool StreamLlmAnswersVm
    {
        get => _config.Advanced.StreamLlmAnswers;
        set { _config.Advanced.StreamLlmAnswers = value; OnPropertyChanged(); }
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

    private string _selectedAudioPresetId = "custom";

    /// <summary>Выбранный аудио-пресет по ID (Tag в XAML): custom/standard/fast/slow.
    /// UI-подписи локализованы в Strings.*.xaml, поэтому храним стабильный ID.</summary>
    public string SelectedAudioPresetId
    {
        get => _selectedAudioPresetId;
        set
        {
            _selectedAudioPresetId = value;
            ApplyAudioPreset(value);
            OnPropertyChanged();
        }
    }

    // Режим нарезки речи (эндпойнтинг): manual/adaptive/semantic (Tag в XAML).
    // В авто-режимах порог тишины подстраивается сам, ручные пресеты/слайдер неактивны.
    public string SelectedEndpointModeId
    {
        get => _config.Audio.EndpointMode?.ToLowerInvariant() switch
        {
            "adaptive" => "adaptive",
            "semantic" => "semantic",
            _ => "manual",
        };
        set
        {
            _config.Audio.EndpointMode = value switch
            {
                "adaptive" => "adaptive",
                "semantic" => "semantic",
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

    private void ApplyAudioPreset(string presetId)
    {
        switch (presetId)
        {
            case "standard":
                SilenceThresholdMs = 1800;
                MinSpeechMsUi = 1000;
                PreRollMsUi = 400;
                PostRollMsUi = 500;
                break;
            case "fast":
                SilenceThresholdMs = 600;
                MinSpeechMsUi = 300;
                PreRollMsUi = 150;
                PostRollMsUi = 150;
                break;
            case "slow":
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

    public bool HideTrayIcon
    {
        get => _config.Ui.HideTrayIcon;
        set { _config.Ui.HideTrayIcon = value; OnPropertyChanged(); }
    }

    // Лимит символов ответа LLM в оверлее. 0 = без обрезки (2.18.0).
    public int MaxAnswerChars
    {
        get => _config.Ui.MaxAnswerChars;
        set { _config.Ui.MaxAnswerChars = Math.Clamp(value, 0, 20000); OnPropertyChanged(); }
    }

    // Отступы текста внутри пузырей вопроса/ответа в оверлее (px, 2.18.0).
    public double BubblePadding
    {
        get => _config.Ui.BubblePadding;
        set { _config.Ui.BubblePadding = Math.Clamp(value, 0, 40); OnPropertyChanged(); }
    }

    // Кастомный промпт AI-саммари встречи; пусто = встроенный дефолт (2.18.0).
    public string SummaryPrompt
    {
        get => _config.Advanced.SummaryPrompt;
        set { _config.Advanced.SummaryPrompt = value ?? string.Empty; OnPropertyChanged(); }
    }

    /// <summary>
    /// Alias имени процесса для Диспетчера задач (стелс-режим). Пусто = выкл.
    /// Базовое имя без «.exe»; применяется при следующем запуске приложения.
    /// </summary>
    public string StealthProcessName
    {
        get => _config.Ui.StealthProcessName;
        set { _config.Ui.StealthProcessName = value?.Trim() ?? string.Empty; OnPropertyChanged(); }
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

    public string ToggleKaraokeHotkey
    {
        get => _config.Hotkeys.ToggleKaraoke;
        set { _config.Hotkeys.ToggleKaraoke = value; OnPropertyChanged(); }
    }

    /// <summary>Дёрнуть INPC для KaraokeEnabled — хоткей меняет Config напрямую, без сеттера VM.</summary>
    public void NotifyKaraokeEnabledChanged() => OnPropertyChanged(nameof(KaraokeEnabled));

    public string ToggleScreenOcrHotkey
    {
        get => _config.Hotkeys.ToggleScreenOcr;
        set { _config.Hotkeys.ToggleScreenOcr = value; OnPropertyChanged(); }
    }

    /// <summary>Хоткей «распознать экран сейчас» — разовый проход без включения наблюдения.</summary>
    public string ScreenOcrRecognizeNowHotkey
    {
        get => _config.Hotkeys.ScreenOcrRecognizeNow;
        set { _config.Hotkeys.ScreenOcrRecognizeNow = value; OnPropertyChanged(); }
    }

    /// <summary>Движок OCR: "tesseract" | "windows". Неизвестные значения трактуются как tesseract.</summary>
    public string ScreenOcrEngine
    {
        get => _config.ScreenOcr.Engine;
        set { _config.ScreenOcr.Engine = value; OnPropertyChanged(); }
    }

    /// <summary>Дёрнуть INPC для ScreenOcrEnabled — хоткей меняет Config напрямую, без сеттера VM.</summary>
    public void NotifyScreenOcrEnabledChanged() => OnPropertyChanged(nameof(ScreenOcrEnabled));

    public bool ScreenOcrEnabled
    {
        get => _config.ScreenOcr.Enabled;
        set { _config.ScreenOcr.Enabled = value; OnPropertyChanged(); }
    }

    /// <summary>-1 = основной монитор; 0..N — конкретный монитор из списка.</summary>
    public int ScreenOcrMonitorIndex
    {
        get => _config.ScreenOcr.MonitorIndex;
        set { _config.ScreenOcr.MonitorIndex = value; OnPropertyChanged(); }
    }

    public int ScreenOcrPollIntervalMs
    {
        get => _config.ScreenOcr.PollIntervalMs;
        set { _config.ScreenOcr.PollIntervalMs = value; OnPropertyChanged(); }
    }

    /// <summary>Интервал опроса в секундах — для слайдера 0.5–10 с (конфиг хранит мс).</summary>
    public double ScreenOcrPollIntervalSeconds
    {
        get => _config.ScreenOcr.PollIntervalMs / 1000.0;
        set
        {
            _config.ScreenOcr.PollIntervalMs = (int)Math.Round(Math.Clamp(value, 0.5, 10.0) * 1000);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScreenOcrPollIntervalMs));
        }
    }

    public double ScreenOcrChangeThreshold
    {
        get => _config.ScreenOcr.ChangeThreshold;
        set { _config.ScreenOcr.ChangeThreshold = value; OnPropertyChanged(); }
    }

    public double ScreenOcrCooldownSec
    {
        get => _config.ScreenOcr.CooldownSec;
        set { _config.ScreenOcr.CooldownSec = value; OnPropertyChanged(); }
    }

    /// <summary>Минимальная длина текста-кандидата (4–64 символов).</summary>
    public int ScreenOcrMinTextLength
    {
        get => _config.ScreenOcr.MinTextLength;
        set { _config.ScreenOcr.MinTextLength = Math.Clamp(value, 4, 64); OnPropertyChanged(); }
    }

    public bool ScreenOcrAutoAnswer
    {
        get => _config.ScreenOcr.AutoAnswer;
        set { _config.ScreenOcr.AutoAnswer = value; OnPropertyChanged(); }
    }

    /// <summary>Фоновое слежение за экраном (ScreenTextWatcher). Хоткей «распознать сейчас» от него не зависит.</summary>
    public bool ScreenOcrAutoWatch
    {
        get => _config.ScreenOcr.AutoWatchEnabled;
        set { _config.ScreenOcr.AutoWatchEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>Полупрозрачный шар состояния в правом нижнем углу (скрыт от захвата экрана).</summary>
    public bool ScreenOcrShowIndicator
    {
        get => _config.ScreenOcr.ShowStatusIndicator;
        set { _config.ScreenOcr.ShowStatusIndicator = value; OnPropertyChanged(); }
    }

    /// <summary>Разделение собеседников (диаризация) на loopback-чанках. Opt-in.</summary>
    public bool SpeakerDiarizationEnabled
    {
        get => _config.SpeakerDiarization.Enabled;
        set { _config.SpeakerDiarization.Enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeakerStatusText)); }
    }

    /// <summary>Бэкенд nemo-speech: "auto" | "cpu" | "vulkan" | "cuda". Только для Nemotron.</summary>
    public string SpeakerBackend
    {
        get => _config.SpeakerDiarization.NemotronBackend;
        set { _config.SpeakerDiarization.NemotronBackend = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeakerStatusText)); }
    }

    /// <summary>Статус диаризации для настроек: выкл/нет модели/готов (+бэкенд для Nemotron).
    /// VM живёт в Core (без WPF-ресурсов) — строки дублируют ключи L_SpeakerDiar_St_*
    /// из Strings.ru/en.xaml, выбор по Config.Ui.Language.</summary>
    public string SpeakerStatusText
    {
        get
        {
            var cfg = _config.SpeakerDiarization;
            if (!cfg.Enabled)
                return SpeakerTr("выключено", "disabled");
            // auto-порядок резолвера: cuda → vulkan → cpu; если диаризатор
            // уже работает — показываем фактический бэкенд через «→».
            var req = string.Equals(cfg.NemotronBackend, "auto", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(cfg.NemotronBackend)
                ? SpeakerTr("авто (CUDA→Vulkan→CPU)", "auto (CUDA→Vulkan→CPU)")
                : cfg.NemotronBackend.ToLowerInvariant();
            var act = Core.Speaker.NemotronDiarizer.LastActiveBackend;
            var be = act != null ? $"{req} → {act}" : req;
            if (ResolveNemotronModelPath() != null)
                return SpeakerTr($"Nemotron v3, {be}, модель найдена", $"Nemotron v3, {be}, model found");
            var shown = !string.IsNullOrWhiteSpace(cfg.NemotronModelPath)
                ? cfg.NemotronModelPath!
                : Path.Combine(AppContext.BaseDirectory, "models", NemotronModelFile);
            return SpeakerTr($"Nemotron v3, {be}, модель отсутствует: {shown}",
                             $"Nemotron v3, {be}, model missing: {shown}");
        }
    }

    // GGUF-модель Nemotron-3 (Sortformer) — имя совпадает с резолвером Nemotron-диаризатора.
    private const string NemotronModelFile = "Nemotron-3-Diarization.Q8_0.gguf";

    private string SpeakerTr(string ru, string en) =>
        string.Equals(_config.Ui.Language, "en", StringComparison.OrdinalIgnoreCase) ? en : ru;

    // Тот же каскад для GGUF Nemotron (контракт резолюции общий с Core-диаризатором).
    private string? ResolveNemotronModelPath()
    {
        var cfgPath = _config.SpeakerDiarization.NemotronModelPath;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrainstormBuddy");
        var candidates = new[]
        {
            cfgPath,
            Path.Combine(appData, "models", NemotronModelFile),
            Path.Combine(AppContext.BaseDirectory, "models", NemotronModelFile),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "artifacts", "speaker", NemotronModelFile)),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    public string ShowTrayIconHotkey
    {
        get => _config.Hotkeys.ShowTrayIcon;
        set { _config.Hotkeys.ShowTrayIcon = value; OnPropertyChanged(); }
    }

    /// <summary>Хоткей «Ask Memory» — окно вопроса к памяти (спека «ASK MEMORY»).</summary>
    public string AskMemoryHotkey
    {
        get => _config.Hotkeys.AskMemory;
        set { _config.Hotkeys.AskMemory = value; OnPropertyChanged(); }
    }

    /// <summary>Хоткей «Память вкл/выкл» — UseMemory для текущей встречи + дефолт.</summary>
    public string MemoryToggleHotkey
    {
        get => _config.Hotkeys.MemoryToggle;
        set { _config.Hotkeys.MemoryToggle = value; OnPropertyChanged(); }
    }

    /// <summary>Дёрнуть INPC для HideTrayIcon — хоткей возврата иконки меняет Config напрямую, без сеттера VM.</summary>
    public void NotifyHideTrayIconChanged() => OnPropertyChanged(nameof(HideTrayIcon));

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

    public bool ShowQuestionTimingLine
    {
        get => _config.Ui.ShowQuestionTimingLine;
        set { _config.Ui.ShowQuestionTimingLine = value; OnPropertyChanged(); }
    }

    // Инженерный режим: показывать на главном оверлее тонкие настройки (слайдеры паузы/чанка,
    // пресеты скорости речи, эквалайзер). Применяется по «Сохранить».
    public bool EngineerMode
    {
        get => _config.Ui.EngineerMode;
        set { _config.Ui.EngineerMode = value; OnPropertyChanged(); }
    }

    // ===== Караоке-оверлей =====

    public bool KaraokeEnabled
    {
        get => _config.Karaoke.Enabled;
        set { _config.Karaoke.Enabled = value; OnPropertyChanged(); }
    }

    public string KaraokePosition
    {
        get => _config.Karaoke.Position;
        set { _config.Karaoke.Position = value; OnPropertyChanged(); }
    }

    public string KaraokeContentAnchor
    {
        get => _config.Karaoke.ContentAnchor;
        set { _config.Karaoke.ContentAnchor = value; OnPropertyChanged(); }
    }

    public int KaraokeWidthPct
    {
        get => _config.Karaoke.WidthPct;
        set { _config.Karaoke.WidthPct = value; OnPropertyChanged(); }
    }

    public int KaraokeHeightPct
    {
        get => _config.Karaoke.HeightPct;
        set { _config.Karaoke.HeightPct = value; OnPropertyChanged(); }
    }

    public double KaraokeFontSize
    {
        get => _config.Karaoke.FontSize;
        set { _config.Karaoke.FontSize = value; OnPropertyChanged(); }
    }

    public string KaraokeTextColor
    {
        get => _config.Karaoke.TextColor;
        set { _config.Karaoke.TextColor = value; OnPropertyChanged(); }
    }

    public string KaraokeAccentColor
    {
        get => _config.Karaoke.AccentColor;
        set { _config.Karaoke.AccentColor = value; OnPropertyChanged(); }
    }

    public string KaraokeFreshColor
    {
        get => _config.Karaoke.FreshColor;
        set { _config.Karaoke.FreshColor = value; OnPropertyChanged(); }
    }

    public string KaraokeOldLinesColor
    {
        get => _config.Karaoke.OldLinesColor;
        set { _config.Karaoke.OldLinesColor = value; OnPropertyChanged(); }
    }

    public string KaraokeBackgroundColor
    {
        get => _config.Karaoke.BackgroundColor;
        set { _config.Karaoke.BackgroundColor = value; OnPropertyChanged(); }
    }

    public double KaraokeBackgroundOpacity
    {
        get => _config.Karaoke.BackgroundOpacity;
        set { _config.Karaoke.BackgroundOpacity = value; OnPropertyChanged(); }
    }

    public bool KaraokeShowBackground
    {
        get => _config.Karaoke.ShowBackground;
        set { _config.Karaoke.ShowBackground = value; OnPropertyChanged(); }
    }

    public bool KaraokeTextShadow
    {
        get => _config.Karaoke.TextShadow;
        set { _config.Karaoke.TextShadow = value; OnPropertyChanged(); }
    }

    public double KaraokeOpacity
    {
        get => _config.Karaoke.Opacity;
        set { _config.Karaoke.Opacity = value; OnPropertyChanged(); }
    }

    public int KaraokeMaxLines
    {
        get => _config.Karaoke.MaxLines;
        set { _config.Karaoke.MaxLines = value; OnPropertyChanged(); }
    }

    public int KaraokeSlideDurationMs
    {
        get => _config.Karaoke.SlideDurationMs;
        set { _config.Karaoke.SlideDurationMs = value; OnPropertyChanged(); }
    }

    public int KaraokeFadeDurationMs
    {
        get => _config.Karaoke.FadeDurationMs;
        set { _config.Karaoke.FadeDurationMs = value; OnPropertyChanged(); }
    }

    public int KaraokeInnerPadding
    {
        get => _config.Karaoke.InnerPadding;
        set { _config.Karaoke.InnerPadding = value; OnPropertyChanged(); }
    }

    public int KaraokeMarginPx
    {
        get => _config.Karaoke.MarginPx;
        set { _config.Karaoke.MarginPx = value; OnPropertyChanged(); }
    }

    public string KaraokeTextAlignment
    {
        get => _config.Karaoke.TextAlignment;
        set { _config.Karaoke.TextAlignment = value; OnPropertyChanged(); }
    }

    public bool KaraokeShowAnswers
    {
        get => _config.Karaoke.ShowAnswers;
        set { _config.Karaoke.ShowAnswers = value; OnPropertyChanged(); }
    }

    public bool KaraokeShowQuestions
    {
        get => _config.Karaoke.ShowQuestions;
        set { _config.Karaoke.ShowQuestions = value; OnPropertyChanged(); }
    }

    public bool KaraokeShowUserLines
    {
        get => _config.Karaoke.ShowUserLines;
        set { _config.Karaoke.ShowUserLines = value; OnPropertyChanged(); }
    }

    public bool KaraokeHistoryFade
    {
        get => _config.Karaoke.HistoryFade;
        set { _config.Karaoke.HistoryFade = value; OnPropertyChanged(); }
    }

    public double KaraokeMinHistoryOpacity
    {
        get => _config.Karaoke.MinHistoryOpacity;
        set { _config.Karaoke.MinHistoryOpacity = value; OnPropertyChanged(); }
    }

    public int KaraokeHistoryLineCount
    {
        get => _config.Karaoke.HistoryLineCount;
        set { _config.Karaoke.HistoryLineCount = value; OnPropertyChanged(); }
    }

    public string KaraokeQuestionColor
    {
        get => _config.Karaoke.QuestionColor;
        set { _config.Karaoke.QuestionColor = value; OnPropertyChanged(); }
    }

    public bool KaraokeAgeColorFade
    {
        get => _config.Karaoke.AgeColorFade;
        set { _config.Karaoke.AgeColorFade = value; OnPropertyChanged(); }
    }

    public bool KaraokeShowMarker
    {
        get => _config.Karaoke.ShowMarker;
        set { _config.Karaoke.ShowMarker = value; OnPropertyChanged(); }
    }

    public bool KaraokeHiddenFromCapture
    {
        get => _config.Karaoke.HiddenFromCapture;
        set { _config.Karaoke.HiddenFromCapture = value; OnPropertyChanged(); }
    }

    public bool KaraokeClickThrough
    {
        get => _config.Karaoke.ClickThrough;
        set { _config.Karaoke.ClickThrough = value; OnPropertyChanged(); }
    }

    public bool KaraokeWordWrap
    {
        get => _config.Karaoke.WordWrap;
        set { _config.Karaoke.WordWrap = value; OnPropertyChanged(); }
    }

    // ===== Memory Properties =====

    public bool MemoryEnabled
    {
        get => _config.Memory.Enabled;
        set { _config.Memory.Enabled = value; OnPropertyChanged(); }
    }

    public bool MemoryDefaultUse
    {
        get => _config.Memory.DefaultUseMemory;
        set { _config.Memory.DefaultUseMemory = value; OnPropertyChanged(); }
    }

    public bool MemoryDefaultWrite
    {
        get => _config.Memory.DefaultWriteMemory;
        set { _config.Memory.DefaultWriteMemory = value; OnPropertyChanged(); }
    }

    public string MemorySaveMode
    {
        get => _config.Memory.SaveMode;
        set { _config.Memory.SaveMode = value; OnPropertyChanged(); }
    }

    public string MemoryReviewMode
    {
        get => _config.Memory.ReviewMode;
        set { _config.Memory.ReviewMode = value; OnPropertyChanged(); }
    }

    public string MemoryModelMode
    {
        get => _config.Memory.MemoryModelMode;
        set { _config.Memory.MemoryModelMode = value; OnPropertyChanged(); }
    }

    public string MemoryProviderId
    {
        get => _config.Memory.MemoryProviderId;
        set { _config.Memory.MemoryProviderId = value; OnPropertyChanged(); }
    }

    public string MemoryModel
    {
        get => _config.Memory.MemoryModel;
        set { _config.Memory.MemoryModel = value; OnPropertyChanged(); }
    }

    public string MemoryEmbeddingsMode
    {
        get => _config.Memory.EmbeddingsMode;
        set { _config.Memory.EmbeddingsMode = value; OnPropertyChanged(); }
    }

    public string MemoryEmbeddingsModel
    {
        get => _config.Memory.EmbeddingsModel;
        set { _config.Memory.EmbeddingsModel = value; OnPropertyChanged(); }
    }

    /// <summary>Режим удержания E5 в RAM: alwaysloaded|balanced|memorysaver (спека §10-§13).</summary>
    public string MemoryLocalRuntimeMode
    {
        get => _config.Memory.LocalEmbeddingRuntimeMode;
        set { _config.Memory.LocalEmbeddingRuntimeMode = value; OnPropertyChanged(); }
    }

    /// <summary>Idle-выгрузка Balanced, минуты (1|5|10|30|60).</summary>
    public int MemoryLocalUnloadMinutes
    {
        get => _config.Memory.LocalEmbeddingUnloadAfterIdleMinutes;
        set { _config.Memory.LocalEmbeddingUnloadAfterIdleMinutes = value; OnPropertyChanged(); }
    }

    public bool MemoryAutoRecall
    {
        get => _config.Memory.AutoRecall;
        set { _config.Memory.AutoRecall = value; OnPropertyChanged(); }
    }

    public int MemoryRecallMaxTokens
    {
        get => _config.Memory.RecallMaxTokens;
        set { _config.Memory.RecallMaxTokens = value; OnPropertyChanged(); }
    }

    public int MemoryRecallMaxItems
    {
        get => _config.Memory.RecallMaxItems;
        set { _config.Memory.RecallMaxItems = value; OnPropertyChanged(); }
    }

    public string MemoryTranscriptRetention
    {
        get => _config.Memory.TranscriptRetention;
        set { _config.Memory.TranscriptRetention = value; OnPropertyChanged(); }
    }

    public string MemoryRetention
    {
        get => _config.Memory.MemoryRetention;
        set { _config.Memory.MemoryRetention = value; OnPropertyChanged(); }
    }

    public int MemoryRetentionDays
    {
        get => _config.Memory.MemoryRetentionDays;
        set { _config.Memory.MemoryRetentionDays = value; OnPropertyChanged(); }
    }

    public double MemoryDedupeSimilarity
    {
        get => _config.Memory.DedupeSimilarity;
        set { _config.Memory.DedupeSimilarity = value; OnPropertyChanged(); }
    }

    public int MemoryJobStaleSec
    {
        get => _config.Memory.JobStaleSec;
        set { _config.Memory.JobStaleSec = value; OnPropertyChanged(); }
    }

    public long? MemoryActiveProjectId
    {
        get => _config.Memory.ActiveProjectId;
        set { _config.Memory.ActiveProjectId = value; OnPropertyChanged(); }
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
            case "Groq (openai/gpt-oss-120b, t-one)":
                BaseUrl = "https://api.groq.com/openai/v1";
                ChatModel = "openai/gpt-oss-120b";
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
