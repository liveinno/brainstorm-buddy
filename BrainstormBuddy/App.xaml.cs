using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Audio;
using BrainstormBuddy.Config;
using BrainstormBuddy.Native;
using BrainstormBuddy.Services;
using Application = System.Windows.Application;

namespace BrainstormBuddy;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public LoggingService Logger { get; private set; } = null!;
    public ErrorHandlingService ErrorHandler { get; private set; } = null!;
    public AppConfig Config { get; private set; } = null!;
    public IApiClient ApiClient { get; private set; } = null!;
    public ISttEngine SttEngine { get; private set; } = null!;
    // Последнее известное состояние подсистем (для баннера-уведомления в оверлее).
    private bool _sttOk = true;
    private bool _llmOk = true;
    public AgentOrchestrator? Orchestrator { get; private set; }
    public AudioCaptureEngine AudioEngine { get; private set; } = null!;
    public AudioBuffer AudioBuffer { get; private set; } = null!;
    public AudioBuffer? MicAudioBuffer { get; private set; }
    // Адаптивные контроллеры порога эндпойнтинга (по одному на канал), null в ручном режиме.
    private PauseAdaptiveController? _loopbackAdaptive;
    private PauseAdaptiveController? _micAdaptive;
    // Текстовая склейка мыслей (semantic-режим), по одной на канал; null иначе.
    private TurnAggregator? _loopbackAgg;
    private TurnAggregator? _micAgg;
    public AudioDiagnostics AudioDiagnostics { get; private set; } = null!;
    public SettingsViewModel SettingsVm { get; private set; } = null!;
    public HotkeyManager? HotkeyManager { get; private set; }
    public ToastNotifier Notifier { get; private set; } = null!;
    public NotifyIcon TrayIcon { get; private set; } = null!;
    public LogWindow? LogWindow { get; private set; }
    public QaHistoryLogger? QaLogger { get; private set; }
    public LocalSttService LocalStt { get; private set; } = null!;
    private UiWatchdog? _watchdog;

    public string ConfigPath { get; private set; } = string.Empty;
    public string AppDataDir { get; private set; } = string.Empty;
    public bool ApiKeyMissing { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsLlmPaused { get; private set; }

    private int _totalPromptTokens;
    private int _totalCompletionTokens;
    public int TotalPromptTokens => _totalPromptTokens;
    public int TotalCompletionTokens => _totalCompletionTokens;
    public int TotalTokens => _totalPromptTokens + _totalCompletionTokens;

    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private FileTranscriptionWindow? _fileTranscriptionWindow;
    private BrainstormBuddy.Stt.WhisperSttEngine? _whisperEngine; // ленивый кэш (модель ~600МБ)
    private CancellationTokenSource? _mainLoopCts;
    private Task? _mainLoopTask;
    private bool _shuttingDown;
    // Single-instance: держим mutex всё время жизни процесса (см. OnStartup/OnExit).
    private Mutex? _singleInstanceMutex;
    // Живой writer STT-очереди из MainLoop: нужен флашу при паузе (TogglePause),
    // чтобы недоговорённые фразы уходили в распознавание тем же путём, что и чанки VAD.
    private System.Threading.Channels.ChannelWriter<ChunkItem>? _chunkWriter;
    // Живой reader той же очереди — для инженерного индикатора её глубины в оверлее
    // (bounded-канал умеет Count; проверка CanCount — на случай смены типа канала).
    private System.Threading.Channels.ChannelReader<ChunkItem>? _chunkReader;

    /// <summary>Чанков в очереди STT (ещё не взяты воркером). 0, если MainLoop не поднят.</summary>
    public int SttQueueDepth
    {
        get { var r = _chunkReader; return r is { CanCount: true } ? r.Count : 0; }
    }

    /// <summary>Секунды аудио, накопленные в VAD-буферах обоих каналов и ещё не отданные в очередь STT.</summary>
    public double PendingAudioSeconds
    {
        get
        {
            var sr = Config?.Audio?.SampleRate ?? 16000;
            long samples = (AudioBuffer?.CurrentSampleCount ?? 0) + (MicAudioBuffer?.CurrentSampleCount ?? 0);
            return samples / (double)sr;
        }
    }

    public event EventHandler? ToggleVisibilityRequested;
    public event EventHandler? OpenSettingsRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Инсталлятор — Inno Setup (см. packaging/inno). Velopack убран: его
        // install/update-хуков под Inno нет, поэтому здесь ничего вызывать не нужно.

        base.OnStartup(e);
        _shuttingDown = false;

        // Один экземпляр на систему: в живом логе были следы ДВУХ одновременных процессов
        // (перемешанные сессии, драка за конфиг и аудио-устройства). Проверяем ДО создания
        // логгера, чтобы второй экземпляр не успел ничего перетереть. Global\ — на всю машину.
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Global\BrainstormBuddy_SingleInstance", out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Mutex создан из другой учётки без прав на открытие — значит, экземпляр уже есть.
            createdNew = false;
        }
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "BrainstormBuddy уже запущен — ищите его в трее (иконка у часов).",
                "BrainstormBuddy", MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        // VAD: pure C# (RMS + ZCR) — no native dependencies
        try
        {
            AppDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrainstormBuddy");
            Directory.CreateDirectory(AppDataDir);
            ConfigPath = Path.Combine(AppDataDir, "config.json");

            Logger = new LoggingService(AppDataDir);
            ErrorHandler = new ErrorHandlingService(Logger);

            Logger.Info("=== BrainstormBuddy starting ===", "App");
            Logger.Info($"Args: {(e.Args.Length == 0 ? "(none)" : string.Join(' ', e.Args))}", "App");
            Logger.Info($"OS: {Environment.OSVersion}, 64-bit: {Environment.Is64BitOperatingSystem}", "App");
            Logger.Info($"CLR: {Environment.Version}, CPU count: {Environment.ProcessorCount}", "App");
            Logger.Info($"Working dir: {Environment.CurrentDirectory}", "App");
            Logger.Info($"BaseDirectory: {AppContext.BaseDirectory}", "App");
            Logger.Info($"AppData dir: {AppDataDir}", "App");
            Logger.Info($"Config path: {ConfigPath}", "App");
            Logger.Info("Logger + ErrorHandler initialized", "App");

            // «Хлебная крошка» в неперенаправляемый путь. Если процесс живёт под файловой
            // виртуализацией (MSIX/sandbox), его AppData уезжает в контейнер — логи/конфиг
            // сессии потом не найти (реальный случай: сессия бага 2.5.5 не оставила следов
            // в обычном %APPDATA%). Эта строка всегда говорит, где ИМЕННО лежат файлы запуска.
            try
            {
                var crumbDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "BrainstormBuddy");
                Directory.CreateDirectory(crumbDir);
                var crumb = Path.Combine(crumbDir, "last-session.txt");
                if (File.Exists(crumb) && new FileInfo(crumb).Length > 262_144) File.Delete(crumb);
                File.AppendAllText(crumb,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} " +
                    $"ver={typeof(App).Assembly.GetName().Version} exe={AppContext.BaseDirectory} " +
                    $"appdata={AppDataDir} log={Logger.LogFilePath}{Environment.NewLine}");
            }
            catch { /* best-effort: крошка не должна мешать старту */ }

            // Папка экспорта по умолчанию (Документы\BrainstormBuddy) — создаём и проверяем на старте.
            try { var ed = DefaultExportDir; Logger.Info($"Export dir: {ed} (exists={Directory.Exists(ed)})", "App"); }
            catch (Exception ex) { Logger.Warn($"Export dir init failed: {ex.Message}", "App"); }

            Logger.Debug("Loading config...", "Config");
            var loader = new ConfigLoader(ConfigPath, Logger);
            Config = loader.Load();
            Logger.Info("Config loaded", "Config");

            // Применяем пользовательские настройки логирования (вкл/выкл файла, verbose, ротация).
            Logger.Configure(Config.Logging);

            // Локальные эндпоинты (Ollama/localhost) API-ключа не требуют — не считаем «отсутствие ключа» проблемой,
            // иначе окно настроек лезет при каждом старте. Проверяем РЕАЛЬНЫЙ BaseUrl из конфига.
            var isLocalLlm = Config.Api.BaseUrl.Contains("127.0.0.1") || Config.Api.BaseUrl.Contains("localhost");
            ApiKeyMissing = string.IsNullOrWhiteSpace(Config.Api.ApiKey) && !isLocalLlm;
            Logger.Info($"ApiKey present: {!ApiKeyMissing}", "Config");
            Logger.Info($"API: {Config.Api.BaseUrl}, ChatModel: {Config.Api.ChatModel}, SttModel: {Config.Api.SttModel}", "Config");
            Logger.Info($"Audio: SampleRate={Config.Audio.SampleRate}, ChunkMax={Config.Audio.ChunkMaxSeconds}s, Silence={Config.Audio.SilenceSeconds}s, RMS={Config.Audio.RmsThreshold:F4}, VadMode={Config.Audio.VadMode}, PreRoll={Config.Audio.PreRollMs}ms, PostRoll={Config.Audio.PostRollMs}ms, Overlap={Config.Audio.OverlapMs}ms, MinSpeech={Config.Audio.MinSpeechMs}ms, MicOnly={Config.Audio.MicOnly}", "Config");

            ApiClient = new OpenAiClient(Config.Api, Logger);
            ApiClient.HealthChanged += OnApiHealthChanged;
            // Сессионный LLM-лог (Настройки → Диагностика): каждая пара запрос/ответ строкой.
            // SettingsVm создаётся ниже — обработчик обязан переживать ранние события (null-чек).
            if (ApiClient is OpenAiClient oaClient)
                oaClient.Exchange += OnLlmExchange;
            Logger.Debug("OpenAiClient created", "Ai");

            // Движок STT выбирается по конфигу: "native" (встроенный GigaAM ONNX) или
            // "remote" (внешний сервер/Docker). При проблеме с native — откат на remote.
            SttEngine = CreateSttEngine();
            _sttEngineKey = ComputeSttEngineKey();
            Logger.Info($"STT engine: {SttEngine.Name}", "Ai");
            _ = Task.Run(() => WarmUpStt(SttEngine)); // прогрев в фоне — первая реплика распознаётся быстро
            _ = Task.Run(WarmUpLlm); // прогрев LLM: греет коннект + обновляет health (гасит/ставит плашку) + ловит битую модель ДО первой реплики

            AudioDiagnostics = new AudioDiagnostics(Logger, Config.Audio.EnableDebugLogs);
            AudioBuffer = new AudioBuffer(
                Config.Audio.SampleRate,
                Config.Audio.ChunkMaxSeconds,
                Config.Audio.SilenceSeconds,
                Config.Audio.RmsThreshold,
                Config.Audio.VadMode,
                Config.Audio.PreRollMs,
                Config.Audio.PostRollMs,
                Config.Audio.OverlapMs,
                Config.Audio.MinSpeechMs,
                Logger);
            MicAudioBuffer = Config.Audio.CaptureMic ? new AudioBuffer(
                Config.Audio.SampleRate,
                Config.Audio.ChunkMaxSeconds,
                Config.Audio.SilenceSeconds,
                Config.Audio.RmsThreshold,
                Config.Audio.VadMode,
                Config.Audio.PreRollMs,
                Config.Audio.PostRollMs,
                Config.Audio.OverlapMs,
                Config.Audio.MinSpeechMs,
                Logger) : null;
            EnableAdaptiveEndpointingIfConfigured();

            AudioEngine = new AudioCaptureEngine(Config.Audio, AudioBuffer, AudioDiagnostics, Logger, MicAudioBuffer);
            AudioEngine.RmsUpdated += OnRmsUpdated;
            AudioEngine.DeviceError += OnAudioDeviceError;
            // Жёлтые тосты про аудио-устройства (10с, с кнопкой закрытия) — требование владельца:
            // юзер должен ВИДЕТЬ, что ПО заметило наушники/новое устройство и перестроилось.
            AudioEngine.DefaultRenderChanged += (s, name) => Dispatcher.InvokeAsync(() =>
                _overlay?.ShowDeviceToast("Устройство вывода изменилось",
                    $"Теперь слушаю: {name}. Захват звука с динамика перезапущен."));
            AudioEngine.DeviceArrived += (s, name) => Dispatcher.InvokeAsync(() =>
            {
                // Троттлинг: BT-гарнитура рождает серию событий (динамик + микрофон + профили).
                if (Environment.TickCount64 - _lastDeviceToastTicks < 5000) return;
                _lastDeviceToastTicks = Environment.TickCount64;
                _overlay?.ShowDeviceToast("Новое аудио-устройство",
                    $"{name} — если хотите использовать его микрофон, выберите в Настройках → Аудио.");
            });
            // Прокидка настроек, которых нет в конструкторе буфера (авто-калибровка порога и пр.):
            // без этого до первого «Сохранить» буферы жили с дефолтами полей, а не с конфигом юзера.
            AudioEngine.UpdateConfig(Config.Audio);
            // Восстановление кнопок нижней панели (персист в Ui.*): мьют динамика — сразу в движок;
            // пауза аудио — ДО MainLoop, чтобы он вообще не стартовал захват (без старт/стоп-дёрганья).
            AudioEngine.LoopbackMuted = Config.Ui.LoopbackMuted;
            IsPaused = Config.Ui.AudioPaused;
            if (IsPaused || Config.Ui.LlmDisabled || Config.Ui.LoopbackMuted)
                Logger.Info($"Restored overlay toggles: audioPaused={IsPaused}, llmDisabled={Config.Ui.LlmDisabled}, loopbackMuted={Config.Ui.LoopbackMuted}", "App");
            Logger.Info($"Audio pipeline created (VAD={AudioBuffer.VadAvailable}, DualBuffer={MicAudioBuffer != null}, endpoint={Config.Audio.EndpointMode}, autoCal={Config.Audio.AutoCalibrateThreshold}, rms={Config.Audio.RmsThreshold:F4}, minSpeech={Config.Audio.MinSpeechMs}ms, vadMode={Config.Audio.VadMode})", "Audio");

            SettingsVm = new SettingsViewModel(Config, ApiClient);
            SettingsVm.ThemeChanged += (s, theme) => ApplyTheme(theme);
            ApplyTheme(Config.Ui.Theme);
            ApplyLanguage(Config.Ui.Language);

            // Уважаем LLM-настройки из конфига — без принудительного перетирания на локальный адрес.
            // Дефолт чистой установки: локальный Ollama (AppConfig: BaseUrl 127.0.0.1:11434, ChatModel qwen2.5vl:7b).
            Orchestrator = new AgentOrchestrator(Config.MultiAgent, Config.Api.ApiKey, Config.Api.BaseUrl)
            {
                Log = s => Logger.Info(s, "Agent")
            };
            Logger.Info($"AgentOrchestrator initialized ({Config.MultiAgent.Scenarios.Count} scenarios, url={Config.Api.BaseUrl})", "Agent");

            LocalStt = new LocalSttService(Config.LocalStt, Logger);
            if (Config.LocalStt.Enabled)
            {
                // Локальный STT в Docker (GigaAM) вместо LAN-сервера
                Config.Api.SttBaseUrl = LocalStt.EndpointUrl;
                Config.Api.SttModel = Config.LocalStt.Model;
                Logger.Info($"STT: local Docker ({LocalStt.EndpointUrl}, model={Config.LocalStt.Model})", "LocalStt");
                // Поднимаем Docker-мост только если движок РЕАЛЬНО оказался remote (после
                // проверки доступности). Иначе на встроенном движке — ложная «Docker не найден».
                if (Config.LocalStt.AutoStart && string.Equals(SttEngine.Name, "remote", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await LocalStt.StartAsync(); }
                        catch (Exception ex) { Logger.Error("LocalStt autostart failed", ex, "LocalStt"); }
                    });
                }
            }
            // else: локальный Docker выключен → берём STT-адрес/модель из конфига КАК ЕСТЬ
            // (внешний сервер «свой адрес», либо пусто = адрес LLM). Никаких зашитых адресов.

            Notifier = new ToastNotifier();
            QaLogger = new QaHistoryLogger(AppDataDir);
            Logger.Info($"QaHistoryLogger initialized at {QaLogger.FilePath}", "UI");
            Logger.Debug("UI helpers created", "UI");

            SetupTray();
            Logger.Info("Tray icon initialized", "UI");

            // Создаём LogWindow сразу, но не показываем
            LogWindow = new LogWindow(Logger);

            ShowOverlay();

            if (ApiKeyMissing)
            {
                Logger.Warn("API key missing → opening settings", "App");
                OpenSettings();
            }

            _mainLoopCts = new CancellationTokenSource();
            _mainLoopTask = Task.Run(() => MainLoop(_mainLoopCts.Token));
            Logger.Info("Main loop started", "App");

            _watchdog = new UiWatchdog(Dispatcher, Logger, AppDataDir);
            _watchdog.Start();
        }
        catch (Exception ex)
        {
            // На этом этапе Logger может быть null — пишем в Debug + показываем MessageBox
            System.Diagnostics.Debug.WriteLine($"FATAL: {ex}");
            try { Logger?.Error("Fatal startup error", ex, "App"); } catch { }
            MessageBox.Show($"Не удалось запустить приложение:\n{ex.Message}",
                "BrainstormBuddy — критическая ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private long _lastDeviceToastTicks;

    private void OnRmsUpdated(object? sender, float rms)
    {
        SettingsVm?.UpdateRms(rms);
        SettingsVm?.UpdateEffectiveThreshold(GetCurrentEffectiveThreshold());
    }

    private void OnAudioDeviceError(object? sender, string device)
    {
        Dispatcher.Invoke(() =>
        {
            Logger.Warn($"Audio device error: {device}", "Audio");
            if (device == "microphone")
            {
                Notifier.ShowWarning("Микрофон недоступен",
                    "Не удалось запустить микрофон. Откройте настройки (Ctrl+Shift+S).");
            }
            else if (device == "loopback")
            {
                Notifier.ShowInfo("Только микрофон",
                    "Loopback-устройство недоступно. Работаем только с микрофоном.");
            }
        });
    }

    public void ShowOverlay()
    {
        Logger.Debug("ShowOverlay() called", "UI");
        if (_overlay != null)
        {
            Logger.Debug("Overlay already exists — restoring to normal state", "UI");
            // Восстанавливаем в полный размер: из свёрнутого/скрытого возвращаем Normal,
            // выносим на передний план (иначе из трея показывался огрызок/не активировался).
            _overlay.Show();
            if (_overlay.WindowState == WindowState.Minimized)
                _overlay.WindowState = WindowState.Normal;
            _overlay.Topmost = true;
            _overlay.Activate();
            return;
        }

        try
        {
            _overlay = new OverlayWindow();
            _overlay.Initialize(Config.Ui, WindowHelper.ApplyClickThrough, WindowHelper.ApplyExcludeFromCapture);
            _overlay.Show();
            _overlay.SetActivePresets(SettingsVm.SystemPromptPresets, Config.Advanced.ActiveSystemPromptName);
            _overlay.RefreshAudioPauseButton();
            _overlay.RefreshLlmPauseButton();
            _overlay.RefreshLlmOffButton();
            _overlay.RefreshSpeakerMuteButton();
            // Отложенное уведомление о состоянии STT-движка (модель не скачана / GPU нет / откат)
            if (_pendingSttNotice is { } notice)
            {
                _overlay.SetConnectionNotice(notice.title, notice.body, "stt");
                _pendingSttNotice = null;
            }
            // Первый старт → дружелюбный визард-обучение (один раз)
            if (!Config.Ui.OnboardingDone)
                Dispatcher.InvokeAsync(ShowOnboarding, System.Windows.Threading.DispatcherPriority.Background);
            Logger.Info("Overlay window created and shown", "UI");

            // Индикация прогрева STT (он идёт/уже прошёл в фоне) — чтобы юзер знал, что говорить
            // для «прогрева» НЕ надо: «⏳ Прогрев…» или «✓ Готово — говорите».
            ShowSttWarmupStatus();

            // Deep-link: открыть окно транскрибации файла сразу после старта (--file-transcription).
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--file-transcription", StringComparison.OrdinalIgnoreCase)))
                Dispatcher.InvokeAsync(ShowFileTranscription, System.Windows.Threading.DispatcherPriority.Background);

            // Deep-link: запустить интерактивное обучение (--coach).
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--coach", StringComparison.OrdinalIgnoreCase)))
                Dispatcher.InvokeAsync(ShowOnboarding, System.Windows.Threading.DispatcherPriority.Background);

            if (_overlay.IsLoaded)
            {
                HotkeyManager = new HotkeyManager(_overlay);
                HotkeyManager.HotkeyPressed += OnHotkeyPressed;

                var t = HotkeyManager.Register(Config.Hotkeys.ToggleVisibility, "toggle");
                var o = HotkeyManager.Register(Config.Hotkeys.ChangeOpacity, "opacity");
                var s = HotkeyManager.Register(Config.Hotkeys.OpenSettings, "settings");
                var l = HotkeyManager.Register("Ctrl+Shift+L", "logs");
                var p = HotkeyManager.Register(Config.Hotkeys.TogglePause, "pause");
                var y = HotkeyManager.Register("Ctrl+Shift+Y", "llmpause");
                var c = HotkeyManager.Register("Ctrl+Shift+C", "screenshot");

                Logger.Info($"Hotkeys: toggle={t}, opacity={o}, settings={s}, logs={l}, pause={p}, llmpause={y}, screenshot={c}", "UI");
                if (!(t && o && s && l && p && y && c))
                    Notifier.ShowWarning("Горячие клавиши", "Не удалось зарегистрировать некоторые горячие клавиши. Возможно, они заняты.");

                // Применить текущий ClickThrough из конфига
                if (Config.Ui.ClickThrough) _overlay.ToggleCloak();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show overlay", ex, "UI");
            ErrorHandler.Handle(ex, "ShowOverlay");
        }
    }

    // Максимум из loopback и микрофона — чтобы эквалайзер/волна реагировали и на речь в микрофон.
    public float GetCurrentRms() => Math.Max(AudioBuffer?.CurrentRms ?? 0f, MicAudioBuffer?.CurrentRms ?? 0f);

    // Эффективный порог детекции (для индикатора в настройках). В авто-режиме — калиброванный
    // по фоновому шуму; берём буфер с более высоким живым RMS (тот, что сейчас «говорит»).
    public double GetCurrentEffectiveThreshold()
    {
        var loop = AudioBuffer;
        var mic = MicAudioBuffer;
        if (loop == null) return mic?.CurrentEffectiveThreshold ?? 0.01;
        if (mic == null) return loop.CurrentEffectiveThreshold;
        return (mic.CurrentRms > loop.CurrentRms ? mic : loop).CurrentEffectiveThreshold;
    }
    public bool IsThresholdAutoCalibrating() => AudioBuffer?.IsAutoCalibrating ?? true;

    public void HideOverlay()
    {
        Logger.Debug("HideOverlay() called", "UI");
        _overlay?.Hide();
    }

    // Безопасно сохранить текущий конфиг (используется визардом и др.).
    public void SaveConfigSafe()
    {
        try { new ConfigLoader(ConfigPath, Logger!).Save(Config); }
        catch (Exception ex) { Logger?.Warn($"SaveConfigSafe failed: {ex.Message}", "Config"); }
    }

    // Показать визард-обучение (первый старт или по кнопке в настройках).
    // Интерактивное обучение: открывает настройки и подсвечивает реальные контролы рамкой
    // с пояснениями (вместо старого слайд-шоу WizardWindow). Без затемнения экрана.
    public void ShowOnboarding()
    {
        try
        {
            OpenSettings();
            var s = _settingsWindow;
            if (s == null) return;

            void Start()
            {
                try
                {
                    // Настройки — к левому краю, чтобы не накладываться на оверлей (он справа-сверху).
                    try
                    {
                        var wa = System.Windows.SystemParameters.WorkArea;
                        var h = s.ActualHeight > 0 ? s.ActualHeight : s.Height;
                        s.Left = wa.Left + 40;
                        s.Top = wa.Top + Math.Max(20, (wa.Height - h) / 2);
                    }
                    catch { /* позиционирование best-effort */ }

                    var coach = new CoachMarkOverlay();
                    // Тур по оверлею — ТОЛЬКО после полного прохождения тура настроек.
                    // «Пропустить»/Esc = юзер закончил с обучением: без второго незваного пузыря.
                    coach.Completed += (o, finishedAll) =>
                    {
                        if (finishedAll) StartOverlayOnboardingTour();
                        else { Config.Ui.OnboardingDone = true; SaveConfigSafe(); }
                    };
                    coach.Start(s, BuildCoachSteps(s));
                }
                catch (Exception ex) { Logger?.Error("Coach tour start failed", ex, "UI"); }
            }

            if (s.IsLoaded)
                Dispatcher.BeginInvoke((Action)Start, System.Windows.Threading.DispatcherPriority.Loaded);
            else
                s.Loaded += (o, e) => Dispatcher.BeginInvoke((Action)Start, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) { Logger?.Error("ShowOnboarding failed", ex, "UI"); }
    }

    // Шаги тура по НАСТРОЙКАМ — подсвечивают реальные поля/кнопки (вкладка выбирается автоматически).
    private static List<CoachStep> BuildCoachSteps(SettingsWindow s) => new()
    {
        new CoachStep
        {
            Title = "Движок распознавания речи",
            Body = "С чего всё начинается — распознавание речи (STT). Варианты: «Встроенный GigaAM» — быстро и офлайн, прямо на ПК; " +
                   "«Whisper» — точнее и с пунктуацией (лучше на видеокарте); «Локальный Docker» — свой контейнер; «Внешний сервер» — свой адрес. " +
                   "Для старта подойдёт встроенный GigaAM.",
            Target = () => s.SttEngineCombo
        },
        new CoachStep
        {
            Title = "Проверить скорость",
            Body = "Кнопкой «Замерить скорость» проверишь, как быстро распознаётся речь на твоём железе (RTF меньше — лучше). " +
                   "Если сервер недоступен — покажет честно, а не будет висеть.",
            Target = () => s.SttSpeedTestBtn
        },
        new CoachStep
        {
            Title = "Ускорение (CPU/GPU)",
            Body = "CPU работает у всех из коробки. GPU быстрее в разы, но только с дискретной видеокартой (NVIDIA/AMD); на встройке лучше CPU. " +
                   "Блок виден, когда выбран встроенный GigaAM.",
            Target = () => s.SttAccelCombo.IsVisible ? s.SttAccelCombo : null
        },
        new CoachStep
        {
            Title = "Твоё резюме",
            Body = "Сюда вставь своё резюме — факты, проекты, цифры. Ассистент берёт данные строго отсюда и не выдумывает. " +
                   "Чем подробнее — тем точнее подсказки на собеседовании.",
            Target = () => s.ResumeSummaryBox
        },
        new CoachStep
        {
            Title = "Инженерный режим",
            Body = "Выключен — главный экран чистый. Включи, если хочешь видеть на оверлее тонкие настройки: " +
                   "ползунки паузы/чанка, пресеты скорости речи и эквалайзер.",
            Target = () => s.EngineerModeCheck
        },
        new CoachStep
        {
            Title = "Транскрибация файлов",
            Body = "Отсюда открывается окно распознавания готовых записей (mp4, webm и др.): " +
                   "транскрипт с тайм-кодами, AI-саммари, история и экспорт.",
            Target = () => s.OpenFileTranscriptionBtn
        },
        new CoachStep
        {
            Title = "Сохрани изменения",
            Body = "Поменял настройки — жми «Сохранить». Это обучение всегда можно открыть заново: " +
                   "кнопка «Запустить обучение» внизу слева. Дальше покажу главное окно.",
            Target = () => s.SaveBtn
        },
    };

    // После тура по настройкам — короткий тур по главному окну (оверлею): фото-режим и пр.
    private void StartOverlayOnboardingTour()
    {
        void Finish() { Config.Ui.OnboardingDone = true; SaveConfigSafe(); }
        try
        {
            if (_overlay == null || !_overlay.IsVisible) { Finish(); return; }
            var oc = new CoachMarkOverlay();
            oc.Completed += (o, _) => Finish();
            oc.Start(_overlay, BuildOverlayCoachSteps(_overlay));
        }
        catch (Exception ex) { Logger?.Error("Overlay onboarding tour failed", ex, "UI"); Finish(); }
    }

    // Шаги тура по ОВЕРЛЕЮ — подсвечивают кнопки главного окна золотой рамкой.
    private static List<CoachStep> BuildOverlayCoachSteps(OverlayWindow o) => new()
    {
        new CoachStep
        {
            Title = "Это твой ассистент",
            Body = "Главное окно — оверлей. Слева реплики собеседника, справа готовые ответы ИИ. " +
                   "Окно можно двигать за шапку и тянуть за края.",
            Target = () => null
        },
        new CoachStep
        {
            Title = "Фото-режим: скрыть от записи",
            Body = "Эта кнопка прячет оверлей от записи и демонстрации экрана: ты видишь подсказки, а на созвоне и скриншоте собеседника их нет. " +
                   "Нажми ещё раз — снова показать (например, чтобы самому заскринить). Горячая клавиша Ctrl+Shift+C.",
            Target = () => o.ScreenShotButton
        },
        new CoachStep
        {
            Title = "Пауза звука",
            Body = "Ставит на паузу и возобновляет захват ВСЕГО звука — и собеседника, и твоего микрофона. Рядом живая волна показывает уровень.",
            Target = () => o.MicPauseButton
        },
        new CoachStep
        {
            Title = "Отправить сейчас",
            Body = "Молния отправляет реплику в ИИ немедленно, не дожидаясь паузы. Готово — удачи на собеседовании!",
            Target = () => o.LiveSendBtn
        },
    };

    // Окно локальной транскрибации медиафайлов (single-instance, как OpenSettings).
    public void ShowFileTranscription()
    {
        try
        {
            if (_fileTranscriptionWindow != null)
            {
                if (_fileTranscriptionWindow.WindowState == WindowState.Minimized)
                    _fileTranscriptionWindow.WindowState = WindowState.Normal;
                _fileTranscriptionWindow.Show();
                _fileTranscriptionWindow.Activate();
                return;
            }
            _fileTranscriptionWindow = new FileTranscriptionWindow();
            _fileTranscriptionWindow.Closed += (s, e) => _fileTranscriptionWindow = null;
            _fileTranscriptionWindow.Show();
        }
        catch (Exception ex) { Logger?.Error("ShowFileTranscription failed", ex, "UI"); }
    }

    // Встроенный (native) STT-движок для транскрибации файлов. Переиспользует уже
    // загруженный App.SttEngine, если он native (без повторной загрузки модели ~900МБ);
    // иначе поднимает свой (owns=true → вызывающий обязан Dispose). MelFrontend и
    // ONNX-сессия потокобезопасны, поэтому совместное использование с живым STT безопасно.
    public BrainstormBuddy.Stt.NativeGigaamSttService? ResolveNativeSttEngine(out string? error, out bool owns)
    {
        error = null;
        owns = false;
        if (SttEngine is BrainstormBuddy.Stt.NativeGigaamSttService nat) return nat;
        try
        {
            var (model, labels) = ResolveGigaamModel();
            if (model == null || labels == null)
            {
                error = "Модель GigaAM не найдена. Скачайте её в настройках → Локальный STT, либо переустановите полную версию.";
                return null;
            }
            owns = true;
            return new BrainstormBuddy.Stt.NativeGigaamSttService(model, labels, BrainstormBuddy.Stt.SttAccel.Cpu);
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    // Ищет ggml-модель Whisper (large-v3-turbo, любой квант) по приоритету:
    // %APPDATA%\models → рядом с exe\models → dev artifacts\whisper.
    public string? ResolveWhisperModel()
    {
        var dirs = new[]
        {
            Path.Combine(AppDataDir, "models"),
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "whisper")),
        };
        foreach (var d in dirs)
        {
            try
            {
                if (!Directory.Exists(d)) continue;
                var m = Directory.GetFiles(d, "ggml-large-v3-turbo*.bin")
                    .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                if (m != null) return m;
            }
            catch { /* каталог недоступен — пропускаем */ }
        }
        return null;
    }

    // Кэшированный движок Whisper (модель грузится один раз, переиспользуется).
    public BrainstormBuddy.Stt.WhisperSttEngine? GetWhisperEngine(out string? error)
    {
        error = null;
        if (_whisperEngine != null) return _whisperEngine;
        var model = ResolveWhisperModel();
        if (model == null)
        {
            error = "Модель Whisper (turbo) не скачана. Скачайте её в Настройки → Локальный STT.";
            return null;
        }
        try
        {
            // auto: если в системе ЕСТЬ GPU (любой) → Vulkan; иначе CPU. Дискретную предпочитаем.
            string accel = (Config.Audio.WhisperAccel ?? "auto").ToLowerInvariant();
            int disc = BrainstormBuddy.Native.GpuEnumerator.BestDiscreteIndex();
            var gpus = BrainstormBuddy.Native.GpuEnumerator.List();
            if (accel == "auto") accel = gpus.Count > 0 ? "gpu" : "cpu";
            int gpuDev = accel == "gpu" ? (disc >= 0 ? disc : (gpus.Count > 0 ? gpus[0].Index : 0)) : -1;
            _whisperEngine = new BrainstormBuddy.Stt.WhisperSttEngine(model, Config.Audio.WhisperLanguage, accel, gpuDev);
            Logger.Info($"Whisper ready: {model} (accel={accel}, vkDevice={gpuDev})", "Ai");
            return _whisperEngine;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>Путь к найденной модели GigaAM (или null) — для статуса/удаления в настройках.</summary>
    public string? GetGigaamModelPath()
    {
        var (m, _) = ResolveGigaamModel();
        return m;
    }

    // Освобождает закэшированный Whisper (например, перед удалением файла модели).
    public void DisposeWhisperEngine()
    {
        try { _whisperEngine?.Dispose(); } catch { /* уже освобождён */ }
        _whisperEngine = null;
    }

    // Похоже ли на вопрос — для фолбэка, когда все агенты промолчали (нарратив пропускаем, вопрос отвечаем).
    // Включает адаптивный эндпойнтинг на обоих каналах, если так задано в конфиге.
    // Режимы "adaptive" и "semantic" оба используют авто-порог на уровне аудио.
    private void EnableAdaptiveEndpointingIfConfigured()
    {
        var mode = Config.Audio.EndpointMode?.ToLowerInvariant();
        if (mode != "adaptive" && mode != "semantic") return;

        _loopbackAdaptive = new PauseAdaptiveController(new AdaptiveEndpointConfig(),
            Config.Audio.AdaptiveLastLoopbackSeconds > 0 ? Config.Audio.AdaptiveLastLoopbackSeconds : 1.2);
        AudioBuffer.EnableAdaptiveEndpointing(_loopbackAdaptive);

        if (MicAudioBuffer != null)
        {
            _micAdaptive = new PauseAdaptiveController(new AdaptiveEndpointConfig(),
                Config.Audio.AdaptiveLastMicSeconds > 0 ? Config.Audio.AdaptiveLastMicSeconds : 1.2);
            MicAudioBuffer.EnableAdaptiveEndpointing(_micAdaptive);
        }
        if (mode == "semantic")
        {
            _loopbackAgg = new TurnAggregator();
            _micAgg = new TurnAggregator();
        }
        Logger.Info($"Adaptive endpointing ON (mode={mode}, loopback cold={_loopbackAdaptive.AppliedSeconds:F2}s, " +
                    $"mic={(_micAdaptive?.AppliedSeconds ?? 0):F2}s)", "Audio");
    }

    // Смена режима нарезки НА ЛЕТУ. Раньше режим применялся только при старте: юзер щёлкал
    // adaptive↔semantic↔manual в настройках, а буферы жили в старом режиме до перезапуска
    // (живой лог: после выбора semantic все строки — по-прежнему mode=AdaptivePauses).
    private void ApplyEndpointModeLive()
    {
        var mode = Config.Audio.EndpointMode?.ToLowerInvariant();
        if (mode == "adaptive" || mode == "semantic")
        {
            if (_loopbackAdaptive == null)
            {
                EnableAdaptiveEndpointingIfConfigured(); // авто включили впервые за сессию
            }
            else
            {
                AudioBuffer.SetEndpointMode(EndpointMode.AdaptivePauses);
                MicAudioBuffer?.SetEndpointMode(EndpointMode.AdaptivePauses);
            }
            // Семантическая склейка — только в semantic; при выходе из него агрегаторы убираем,
            // чтобы придержанные фрагменты не зависали (воркер увидит null на следующем чанке).
            _loopbackAgg = mode == "semantic" ? (_loopbackAgg ?? new TurnAggregator()) : null;
            _micAgg = mode == "semantic" ? (_micAgg ?? new TurnAggregator()) : null;
        }
        else // manual
        {
            AudioBuffer.SetEndpointMode(EndpointMode.Manual);
            MicAudioBuffer?.SetEndpointMode(EndpointMode.Manual);
            _loopbackAgg = null;
            _micAgg = null;
        }
        Logger.Info($"Endpoint mode applied live: {mode} (semanticGlue={_loopbackAgg != null})", "Audio");
    }

    private static bool LooksLikeQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.ToLowerInvariant();
        if (t.Contains('?')) return true;
        string[] qw =
        {
            "что ", "кто ", "как ", "како", "почему", "зачем", "где ", "когда", "сколько",
            "чем ", "куда", "чему", "в чём", "расскажи", "объясни", "поясни", "перечисли", "назови",
            "what", "how ", "who ", "why", "when", "which", "where", "explain"
        };
        return qw.Any(w => t.Contains(w));
    }

    // Транскрибатор файла по выбору движка ("whisper" | "gigaam"/иначе).
    public BrainstormBuddy.Stt.IFileTranscriber? ResolveFileTranscriber(string engineChoice, out string? error)
    {
        error = null;
        // Оборудование файловой транскрибации: явный выбор юзера в окне ("cpu"/"gpu") перекрывает
        // глобальные настройки; "auto" — прежнее поведение (кэш/переиспользование живого движка).
        // При явном выборе создаём СОБСТВЕННЫЙ движок на время задачи (окно его Dispose-ит):
        // кэш живого распознавания не трогаем — смена железа в окне не дёргает созвон.
        var fileAccel = (Config.Audio.FileSttAccel ?? "auto").ToLowerInvariant();

        if (string.Equals(engineChoice, "whisper", StringComparison.OrdinalIgnoreCase))
        {
            if (fileAccel is "cpu" or "gpu")
            {
                var wm = ResolveWhisperModel();
                if (wm == null) { error = "Модель Whisper (turbo) не скачана. Скачайте её в Настройки → Локальный STT."; return null; }
                try
                {
                    int dev = fileAccel == "gpu" ? ResolveFileGpuIndex() : -1;
                    var owned = new BrainstormBuddy.Stt.WhisperSttEngine(wm, Config.Audio.WhisperLanguage, fileAccel, dev);
                    Logger.Info($"File Whisper: явное железо accel={fileAccel}, vkDevice={dev}", "Ai");
                    return owned; // WhisperSttEngine сам IFileTranscriber; owns — окно диспозит
                }
                catch (Exception ex) { error = ex.Message; return null; }
            }
            var w = GetWhisperEngine(out error);
            return w == null ? null : new BrainstormBuddy.Stt.NonOwningFileTranscriber(w);
        }

        if (fileAccel is "cpu" or "gpu")
        {
            try
            {
                var (model, labels) = ResolveGigaamModel();
                if (model == null || labels == null)
                {
                    error = "Модель GigaAM не найдена. Скачайте её в настройках → Локальный STT, либо переустановите полную версию.";
                    return null;
                }
                var accel = fileAccel == "gpu" ? BrainstormBuddy.Stt.SttAccel.DirectML : BrainstormBuddy.Stt.SttAccel.Cpu;
                int dev = fileAccel == "gpu" ? ResolveFileGpuIndex() : 0;
                var owned = new BrainstormBuddy.Stt.NativeGigaamSttService(model, labels, accel, dev);
                Logger.Info($"File GigaAM: явное железо accel={fileAccel}, device={dev}, provider={owned.ActiveProvider}", "Ai");
                return new BrainstormBuddy.Stt.GigaamFileTranscriber(owned, ownsStt: true);
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }
        var gig = ResolveNativeSttEngine(out error, out bool owns);
        return gig == null ? null : new BrainstormBuddy.Stt.GigaamFileTranscriber(gig, owns);
    }

    // DXGI-индекс GPU для файловой транскрибации: явный выбор юзера, иначе дискретный/первый.
    private int ResolveFileGpuIndex()
    {
        if (Config.Audio.FileSttGpuDevice >= 0) return Config.Audio.FileSttGpuDevice;
        int disc = BrainstormBuddy.Native.GpuEnumerator.BestDiscreteIndex();
        if (disc >= 0) return disc;
        var gpus = BrainstormBuddy.Native.GpuEnumerator.List();
        return gpus.Count > 0 ? gpus[0].Index : 0;
    }

    // Уведомление, которое надо показать в оверлее ПОСЛЕ его создания (движок выбирается на старте,
    // когда оверлея ещё нет). Показывается в ShowOverlay.
    private (string title, string body)? _pendingSttNotice;

    // Сигнатура текущего STT-движка — чтобы пересоздавать его только при реальной смене настроек.
    private string _sttEngineKey = "";
    private string ComputeSttEngineKey() =>
        $"{Config.Audio.SttEngine}|{Config.Audio.SttAccel}|{Config.Audio.SttGpuDevice}|{Config.Audio.WhisperAccel}|{Config.LocalStt.Enabled}";

    // Горячая замена движка распознавания при смене настроек — БЕЗ перезапуска приложения.
    // Модель грузится в фоне; воркеры читают App.SttEngine на каждом чанке и подхватят новый.
    public void RecreateSttEngineIfChanged()
    {
        var newKey = ComputeSttEngineKey();
        if (newKey == _sttEngineKey) return;
        _sttEngineKey = newKey;
        Logger.Info($"STT-настройки изменились → пересоздаю движок ({newKey})", "Ai");
        Dispatcher.InvokeAsync(() => Notifier?.ShowInfo("Переключаю распознавание", "загружаю и прогреваю движок…"));
        _ = Task.Run(() =>
        {
            try
            {
                var fresh = CreateSttEngine();
                SttEngine = fresh; // атомарная замена ссылки — следующий чанк уйдёт в новый движок
                Logger.Info($"STT engine switched → {fresh.Name}, прогреваю…", "Ai");
                WarmUpStt(fresh);  // прогон сэмпла — первый реальный чанк не платит холодный старт
                Dispatcher.InvokeAsync(() =>
                {
                    if (_pendingSttNotice is { } n) { _overlay?.SetConnectionNotice(n.title, n.body, "stt"); _pendingSttNotice = null; }
                    else Notifier?.ShowInfo("Распознавание готово", fresh.Name);
                });
            }
            catch (Exception ex) { Logger.Error("STT hot-swap failed", ex, "Ai"); }
        });
    }

    // Прогрев STT: один прогон на коротком сэмпле (модель JIT'ится/оптимизируется) —
    // чтобы первый реальный чанк не платил цену холодного старта. Прогрев АВТОМАТИЧЕСКИЙ
    // (синтетический сэмпл), юзеру НЕ нужно ничего говорить — об этом сообщаем плашкой.
    private volatile bool _sttWarmedUp;
    private void WarmUpStt(BrainstormBuddy.Ai.ISttEngine engine)
    {
        try
        {
            _sttWarmedUp = false;
            Dispatcher.InvokeAsync(ShowSttWarmupStatus); // «⏳ Прогрев…» если оверлей уже есть
            var t0 = DateTime.UtcNow;
            engine.TranscribeAsync(BuildWarmupWav()).GetAwaiter().GetResult();
            Logger.Info($"STT warmup [{engine.Name}] done in {(DateTime.UtcNow - t0).TotalMilliseconds:F0}ms", "Ai");
        }
        catch (Exception ex) { Logger.Warn($"STT warmup failed: {ex.Message}", "Ai"); }
        finally
        {
            _sttWarmedUp = true;
            Dispatcher.InvokeAsync(ShowSttWarmupStatus); // «✓ Готово — говорите»
        }
    }

    // Видимая индикация прогрева STT: «⏳ Прогрев…» → «✓ Готово — говорите» (через 4с → «Слушаю»).
    // Чтобы юзер НЕ думал, что должен «прогревать» голосом и не смотрел в пустой экран.
    private void ShowSttWarmupStatus()
    {
        if (_overlay == null) return;
        if (!_sttWarmedUp)
        {
            _overlay.SetStatus("⏳ Прогрев распознавания…");
            return;
        }
        _overlay.SetStatus("✓ Распознавание готово — говорите");
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (s, e) => { t.Stop(); _overlay?.SetStatus(T("L_Listening")); };
        t.Start();
    }

    // Прогрев LLM на старте: лёгкий запрос (ключ/URL + чат-пинг моделью) — греет коннект,
    // сразу обновляет health (гасит/ставит плашку), ловит битую модель/ключ ДО первой реплики.
    private async Task WarmUpLlm()
    {
        // «LLM выкл» (чистый транскрибатор): в LLM не уходит ДАЖЕ прогрев — иначе на старте
        // прилетал бы health-ивент и плашка ошибки, которые в этом режиме заглушены.
        if (Config.Ui.LlmDisabled)
        {
            Logger.Info("LLM warmup skipped: LLM disabled (чистый транскрибатор)", "Ai");
            return;
        }
        try
        {
            var (ok, detail) = await ApiClient.CheckLlmConnectionAsync(
                Config.Advanced.MaxResponseTokens, Config.Advanced.SystemPrompt);
            Logger.Info($"LLM warmup: ok={ok} — {detail}", "Ai");
        }
        catch (Exception ex) { Logger.Warn($"LLM warmup failed: {ex.Message}", "Ai"); }
    }

    // Короткий WAV (16кГц mono, 0.6с тихого тона) для прогрева движка.
    private static byte[] BuildWarmupWav()
    {
        const int sr = 16000; int n = sr * 6 / 10;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataBytes = n * 2;
        void Tag(string s) => bw.Write(System.Text.Encoding.ASCII.GetBytes(s));
        Tag("RIFF"); bw.Write(36 + dataBytes); Tag("WAVE"); Tag("fmt ");
        bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
        Tag("data"); bw.Write(dataBytes);
        for (int i = 0; i < n; i++) { short v = (short)(Math.Sin(2 * Math.PI * 220 * i / sr) * 700); bw.Write(v); }
        bw.Flush(); return ms.ToArray();
    }

    // Создаёт STT-движок по конфигу. "native" (GigaAM ONNX) с откатом на "remote" при проблеме.
    private BrainstormBuddy.Ai.ISttEngine CreateSttEngine()
    {
        var eng = Config.Audio.SttEngine?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(eng)) eng = "native"; // пусто → встроенный GigaAM, НЕ remote

        // Явно выбран Whisper: Whisper → (фолбэк) встроенный GigaAM → remote.
        if (eng == "whisper")
        {
            var w = GetWhisperEngine(out var werr);
            if (w != null) { Logger.Info("STT engine: Whisper", "Ai"); return w; }
            Logger.Warn($"Whisper недоступен ({werr}) → пробую встроенный GigaAM", "Ai");
            var fallbackGig = TryCreateNativeGigaam(out _);
            if (fallbackGig != null)
            {
                _pendingSttNotice = ("Whisper не готов",
                    "Выбран Whisper, но модель не скачана — работаю на встроенном GigaAM. Скачайте Whisper в Настройки → Распознавание речи (STT).");
                Logger.Info("STT engine: native (фолбэк с Whisper)", "Ai");
                return fallbackGig;
            }
            _pendingSttNotice = ("Модель распознавания не скачана",
                "Встроенные движки недоступны — временно работаю через внешний сервер. Скачайте модель в Настройки → Распознавание речи (STT).");
            return new BrainstormBuddy.Ai.RemoteSttService(ApiClient);
        }

        // Встроенный GigaAM (по умолчанию): GigaAM → (фолбэк) Whisper → remote.
        // ВАЖНО: никогда не проваливаемся молча в remote, если локальная модель есть.
        if (eng == "native")
        {
            var gig = TryCreateNativeGigaam(out var gerr);
            if (gig != null) return gig;

            Logger.Warn($"Встроенный GigaAM недоступен ({gerr}) → пробую Whisper", "Ai");
            var w = GetWhisperEngine(out _);
            if (w != null)
            {
                _pendingSttNotice = ("Встроенный GigaAM не готов",
                    "Использую Whisper. Проверьте модель GigaAM в Настройки → Распознавание речи (STT).");
                Logger.Info("STT engine: Whisper (фолбэк с GigaAM)", "Ai");
                return w;
            }
            _pendingSttNotice = ("Модель распознавания не скачана",
                "Встроенные движки недоступны — временно работаю через внешний сервер. Скачайте модель в Настройки → Распознавание речи (STT).");
            return new BrainstormBuddy.Ai.RemoteSttService(ApiClient);
        }

        // Явно выбран внешний сервер (remote / Docker / LAN).
        // Если сервер отвечает — используем его. Если молчит (напр. Docker не запущен),
        // а локальная модель есть — падаем на встроенный движок, чтобы STT не был мёртв.
        if (IsRemoteSttReachable())
        {
            Logger.Info("STT engine: remote (сервер доступен)", "Ai");
            return new BrainstormBuddy.Ai.RemoteSttService(ApiClient);
        }
        Logger.Warn("STT: внешний сервер не отвечает → пробую встроенные движки", "Ai");
        var gigRemoteFb = TryCreateNativeGigaam(out _);
        if (gigRemoteFb != null)
        {
            _pendingSttNotice = ("Внешний сервер распознавания недоступен",
                "Сервер STT не отвечает — работаю на встроенном GigaAM. Проверьте Docker/адрес в Настройки → Распознавание речи (STT).");
            Logger.Info("STT engine: native (фолбэк с remote)", "Ai");
            return gigRemoteFb;
        }
        var wRemoteFb = GetWhisperEngine(out _);
        if (wRemoteFb != null)
        {
            _pendingSttNotice = ("Внешний сервер распознавания недоступен",
                "Сервер STT не отвечает — работаю на Whisper. Проверьте Docker/адрес в Настройки → Распознавание речи (STT).");
            Logger.Info("STT engine: Whisper (фолбэк с remote)", "Ai");
            return wRemoteFb;
        }
        Logger.Info("STT engine: remote (встроенных движков нет)", "Ai");
        return new BrainstormBuddy.Ai.RemoteSttService(ApiClient);
    }

    // Быстрая проверка доступности STT-сервера (TCP-коннект, короткий таймаут).
    // Нужна, чтобы не висеть на мёртвом Docker/адресе и вовремя откатиться на встроенный движок.
    private bool IsRemoteSttReachable()
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(Config.Api.SttBaseUrl) ? Config.Api.BaseUrl : Config.Api.SttBaseUrl;
            if (string.IsNullOrWhiteSpace(url)) return false;
            var uri = new Uri(url);
            int port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
            using var client = new System.Net.Sockets.TcpClient();
            bool ok = client.ConnectAsync(uri.Host, port).Wait(700);
            return ok && client.Connected;
        }
        catch { return false; }
    }

    // Пытается поднять встроенный GigaAM (ONNX). Возвращает null + текст ошибки,
    // если модель не найдена или движок не стартовал (тогда вызывающий пробует Whisper/remote).
    private BrainstormBuddy.Stt.NativeGigaamSttService? TryCreateNativeGigaam(out string? error)
    {
        error = null;
        try
        {
            var (model, labels) = ResolveGigaamModel();
            if (model == null || labels == null)
            {
                error = "модель GigaAM ONNX не найдена";
                Logger.Warn($"Native STT: {error}", "Ai");
                return null;
            }
            var accelStr = Config.Audio.SttAccel?.ToLowerInvariant() ?? "cpu";
            int gpuDevice = Config.Audio.SttGpuDevice;
            BrainstormBuddy.Stt.SttAccel wantAccel;
            if (accelStr == "directml")
                wantAccel = BrainstormBuddy.Stt.SttAccel.DirectML;
            else if (accelStr == "auto")
            {
                // Авто: дискретная видеокарта, если есть; иначе CPU (встройку не берём).
                int disc = BrainstormBuddy.Native.GpuEnumerator.BestDiscreteIndex();
                if (disc >= 0) { wantAccel = BrainstormBuddy.Stt.SttAccel.DirectML; gpuDevice = disc; Logger.Info($"Auto STT: дискретный GPU #{disc}", "Ai"); }
                else wantAccel = BrainstormBuddy.Stt.SttAccel.Cpu;
            }
            else wantAccel = BrainstormBuddy.Stt.SttAccel.Cpu;

            var svc = new BrainstormBuddy.Stt.NativeGigaamSttService(model, labels, wantAccel, gpuDevice);
            Logger.Info($"Native STT ready: {model} (provider={svc.ActiveProvider})", "Ai");

            // GPU запрошен, но не поднялся (нет DX12/драйвера) → работаем на CPU, предупреждаем
            if (wantAccel == BrainstormBuddy.Stt.SttAccel.DirectML && !svc.ActiveProvider.StartsWith("DirectML"))
                _pendingSttNotice = ("GPU-ускорение недоступно",
                    "Запрошен DirectML, но GPU не поднялся — распознавание работает на CPU (медленнее). Проверьте драйвер видеокарты или выберите CPU в настройках.");
            return svc;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Error("Native STT init failed", ex, "Ai");
            return null;
        }
    }

    // Ищет ONNX-модель и labels по приоритету:
    // конфиг → %APPDATA%\models (докачка) → рядом с exe\models (вариант C: инсталлятор) → dev artifacts/.
    private (string? model, string? labels) ResolveGigaamModel()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Config.Audio.SttModelPath)) candidates.Add(Config.Audio.SttModelPath);
        candidates.Add(Path.Combine(AppDataDir, "models", "v2_ctc.onnx"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "models", "v2_ctc.onnx")); // вариант C: рядом с exe
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "gigaam", "v2_ctc.onnx"))); // dev
        foreach (var m in candidates)
        {
            if (File.Exists(m))
            {
                var dir = Path.GetDirectoryName(m)!;
                var labels = new[]
                {
                    Path.Combine(dir, "labels.json"),
                    Path.Combine(AppDataDir, "models", "labels.json"),
                    Path.Combine(AppContext.BaseDirectory, "models", "labels.json"),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "gigaam_export", "labels.json")),
                }.FirstOrDefault(File.Exists);
                if (labels != null) return (m, labels);
            }
        }
        return (null, null);
    }

    private bool _trayHintShown;
    // Разовая подсказка при сворачивании: куда делось окно и как вернуть.
    public void NotifyMinimizedToTray()
    {
        try
        {
            if (_trayHintShown || TrayIcon == null) return;
            _trayHintShown = true;
            TrayIcon.BalloonTipTitle = "Оверлей свёрнут";
            TrayIcon.BalloonTipText = "Окно скрыто в трей. Вернуть: значок в трее → «Показать оверлей», или Ctrl+Shift+H.";
            TrayIcon.ShowBalloonTip(3000);
        }
        catch (Exception ex) { Logger.Warn($"NotifyMinimizedToTray failed: {ex.Message}", "UI"); }
    }

    // Клиент сообщил о смене доступности STT/LLM → пересобираем текст баннера
    // и показываем/прячем его в оверлее (в UI-потоке).
    private string _sttErrMsg = "", _llmErrMsg = "";

    private void OnApiHealthChanged(object? sender, ApiHealthEventArgs e)
    {
        if (e.Component == ApiComponent.Stt) { _sttOk = e.Healthy; _sttErrMsg = e.Message ?? ""; }
        else if (e.Component == ApiComponent.Llm) { _llmOk = e.Healthy; _llmErrMsg = e.Message ?? ""; }
        UpdateConnectionNotice();
    }

    // Сессионный LLM-лог (Настройки → Диагностика): форматируем событие клиента в строку
    // «[HH:mm:ss] →LLM (модель): …» / «[HH:mm:ss] ←LLM 3.2s 45тк: …» / «[HH:mm:ss] ←LLM ОШИБКА: …».
    // Событие приходит с фоновых потоков (answer-воркер, прогрев) — в VM только через Dispatcher.
    private void OnLlmExchange(object? sender, LlmExchangeEventArgs e)
    {
        var line = e.IsRequest
            ? $"[{e.Timestamp:HH:mm:ss}] →LLM ({e.Model}): {e.Text}"
            : e.IsError
                ? $"[{e.Timestamp:HH:mm:ss}] ←LLM ОШИБКА: {e.Text}"
                : $"[{e.Timestamp:HH:mm:ss}] ←LLM {e.ElapsedSeconds:F1}s {e.TotalTokens}тк: {e.Text}";
        Dispatcher.InvokeAsync(() => SettingsVm?.AddLlmLogLine(line));
    }

    // Пересборка баннера здоровья STT/LLM из последнего известного состояния.
    // Вынесено из OnApiHealthChanged: кнопка «LLM выкл» тоже дёргает пересборку —
    // в режиме транскрибатора LLM-часть глушится (запросов туда нет, ошибки неактуальны).
    private void UpdateConnectionNotice()
    {
        var llmMatters = !Config.Ui.LlmDisabled;
        string? title = null, body = null, target = null;
        // Показываем РЕАЛЬНЫЙ текст ошибки от сервера (401/429/402/таймаут), а не общую фразу —
        // чтобы юзер сразу понял, что не так с ключом/моделью/адресом.
        if (!_sttOk && !_llmOk && llmMatters)
        {
            title = "Ошибка распознавания и ИИ";
            body = $"STT: {(string.IsNullOrWhiteSpace(_sttErrMsg) ? "не отвечает" : _sttErrMsg)}\nИИ: {(string.IsNullOrWhiteSpace(_llmErrMsg) ? "не отвечает" : _llmErrMsg)}";
            target = "llm";
        }
        else if (!_sttOk)
        {
            title = "Распознавание речи недоступно";
            body = string.IsNullOrWhiteSpace(_sttErrMsg)
                ? "STT-сервер не отвечает. Проверьте адрес распознавания в настройках."
                : _sttErrMsg;
            target = "stt";
        }
        else if (!_llmOk && llmMatters)
        {
            title = "Ошибка ИИ (LLM)";
            body = string.IsNullOrWhiteSpace(_llmErrMsg)
                ? "LLM не отвечает. Проверьте адрес, ключ и модель в настройках."
                : _llmErrMsg;
            target = "llm";
        }

        Dispatcher.InvokeAsync(() => _overlay?.SetConnectionNotice(title, body, target));
    }

    public void OpenSettings()
    {
        Logger.Debug("OpenSettings() called", "UI");
        if (_settingsWindow != null)
        {
            // Окно уже открыто (возможно свёрнуто) — восстанавливаем и выносим вперёд,
            // чтобы юзер увидел, что оно уже открыто.
            Logger.Debug("Settings already open — restoring/activating", "UI");
            if (!_settingsWindow.IsVisible) _settingsWindow.Show();
            if (_settingsWindow.WindowState == System.Windows.WindowState.Minimized)
                _settingsWindow.WindowState = System.Windows.WindowState.Normal;
            _settingsWindow.Activate();
            _settingsWindow.Topmost = true;
            _settingsWindow.Topmost = false;
            return;
        }

        try
        {
            _settingsWindow = new SettingsWindow(SettingsVm);
            _settingsWindow.Closed += (s, e) =>
            {
                Logger.Debug("SettingsWindow closed", "UI");
                _settingsWindow = null;
            };
            _settingsWindow.Show();
            Logger.Info("Settings window opened", "UI");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to open settings", ex, "UI");
            ErrorHandler.Handle(ex, "OpenSettings");
        }
    }

    // Открыть настройки и подсветить нужный раздел золотой рамкой (по ссылке из плашки оверлея).
    public void OpenSettingsAndHighlight(string? target)
    {
        OpenSettings();
        var s = _settingsWindow;
        if (s == null) return;
        void DoHighlight() => Dispatcher.BeginInvoke(new Action(() =>
        {
            try { s.HighlightSection(target); }
            catch (Exception ex) { Logger.Warn($"HighlightSection failed: {ex.Message}", "UI"); }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        if (s.IsLoaded) DoHighlight();
        else s.Loaded += (o, e) => DoHighlight();
    }

    public void ShowLogWindow()
    {
        Logger.Debug("ShowLogWindow() called", "UI");
        if (LogWindow == null) return;
        if (LogWindow.IsVisible)
        {
            LogWindow.Activate();
            return;
        }
        if (LogWindow.WindowState == WindowState.Minimized)
            LogWindow.WindowState = WindowState.Normal;

        // Позиционируем рядом с оверлеем или в правом нижнем углу
        if (_overlay != null && _overlay.IsVisible)
        {
            LogWindow.Left = _overlay.Left;
            LogWindow.Top = _overlay.Top + _overlay.Height + 8;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            LogWindow.Left = workArea.Right - LogWindow.Width - 20;
            LogWindow.Top = workArea.Bottom - LogWindow.Height - 20;
        }
        LogWindow.Show();
    }

    public void HideLogWindow()
    {
        LogWindow?.Hide();
    }

    public void SetActiveSystemPrompt(NamedPrompt prompt)
    {
        var oldName = Config.Advanced.ActiveSystemPromptName;
        Config.Advanced.SystemPrompt = prompt.Content;
        Config.Advanced.ActiveSystemPromptName = prompt.Name;
        Logger.Info($"Active system prompt switched '{oldName}' → '{prompt.Name}'", "App");

        ResetConversation();
        Notifier.ShowInfo("Новый чат", $"Промпт: {prompt.Name}");
    }

    // Открыть проводник с выделенным файлом — однозначный фидбек «экспорт произошёл».
    // ВАЖНО: для explorer /select нужен UseShellExecute=false, иначе оболочка пытается
    // «выполнить» путь → ошибка «Расположение недоступно».
    private void RevealInExplorer(string path)
    {
        try
        {
            if (!File.Exists(path)) { Logger.Warn($"RevealInExplorer: file missing {path}", "UI"); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex) { Logger.Warn($"RevealInExplorer failed: {ex.Message}", "UI"); }
    }

    public void ExportTranscript()
    {
        try
        {
            var path = ResolveSavePath("transcript");
            var sb = new StringBuilder();
            sb.AppendLine("=== BrainstormBuddy — Транскрибация ===");
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();
            if (_overlay != null)
            {
                foreach (var qa in _overlay.History)
                {
                    var ts = qa.SttReceivedAt ?? qa.ChunkReadyAt ?? DateTime.Now;
                    var text = qa.Question
                        .Replace("[Динамик] ", "").Replace("[Микрофон] ", "");
                    sb.AppendLine($"{ts:HH:mm:ss.fff}  {text}");
                }
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Logger.Info($"Transcript exported to {path} ({_overlay?.History.Count ?? 0} entries)", "UI");
            Notifier.ShowInfo("Экспорт", $"Транскрибация сохранена:\n{path}");
            RevealInExplorer(path);
            _overlay?.FlashStatus($"✓ Транскрипт сохранён: {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Logger.Error("Export transcript failed", ex, "UI");
            Notifier.ShowWarning("Ошибка экспорта", ex.Message);
        }
    }

    public async Task ExportAiSummary()
    {
        try
        {
            var allText = CollectFullTranscript();
            if (string.IsNullOrWhiteSpace(allText))
            {
                Notifier.ShowInfo("Нет данных", "Транскрибация пуста. Дождитесь распознавания речи.");
                return;
            }

            _overlay?.SetStatus("Составляю протокол встречи…");
            var summaryPrompt = "Ты — секретарь на совещании. Ниже — транскрибация встречи. " +
                "Составь краткий протокол: 1) Тема встречи, 2) Участники (если упоминались), 3) Ключевые вопросы и ответы (тезисно), 4) Договорённости и дальнейшие шаги. " +
                "Пиши на русском, структурированно, без воды. " +
                "Если транскрибация обрывочная — отметь это и выдели то, что удалось понять.\n\n" + allText;

            var sw = Stopwatch.StartNew();
            var result = await ApiClient.AskAsync("Составь протокол встречи по транскрибации выше.",
                summaryPrompt, 800, new List<ChatMessage>(), CancellationToken.None);
            sw.Stop();

            if (!string.IsNullOrWhiteSpace(result.Content))
            {
                var path = ResolveSavePath("summary");
                var sb = new StringBuilder();
                sb.AppendLine("=== BrainstormBuddy — Протокол встречи ===");
                sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} (STT+LLM: {sw.Elapsed.TotalSeconds:F1}s) ===");
                sb.AppendLine();
                sb.AppendLine(result.Content);
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Logger.Info($"AI summary exported to {path} ({sw.Elapsed.TotalSeconds:F1}s)", "UI");
                Notifier.ShowInfo("Протокол встречи", $"Сохранён:\n{path}");
                RevealInExplorer(path);
                _overlay?.FlashStatus($"✓ Протокол сохранён: {System.IO.Path.GetFileName(path)}");
            }
            else
            {
                _overlay?.SetStatus("Ошибка: LLM не ответил");
                Notifier.ShowWarning("Ошибка", "LLM вернул пустой ответ. Попробуйте позже.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AI summary failed", ex, "UI");
            _overlay?.SetStatus("Ошибка протокола");
            Notifier.ShowWarning("Ошибка", ex.Message);
        }
    }

    public void FlushAndSend()
    {
        if (IsPaused) return;
        var chunk = AudioBuffer.GetChunkForTranscription();
        if (chunk.Length == 0)
        {
            Notifier.ShowInfo("Нет аудио", "Буфер пуст — нечего отправлять. Дождитесь речи.");
            return;
        }
        Logger.Info($"Manual flush: {chunk.Length} bytes → STT", "Loop");
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await SttEngine.TranscribeAsync(chunk, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var clean = TextPostProcessor.Clean(text);
                    // Как и в воркере: показываем всё И всё уходит в LLM (решение владельца).
                    var shown = string.IsNullOrWhiteSpace(clean.Text) ? text : clean.Text;
                    var label = "[Динамик] " + shown;
                    // «LLM выкл»: молния тоже работает транскрибатором — пузырь без ответа, в LLM ничего.
                    bool llmOff = Config.Ui.LlmDisabled;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _overlay?.AddSendingItem(label, (chunk.Length - 44) / 32000.0,
                            DateTime.Now, DateTime.Now, DateTime.Now, 0, transcriptOnly: llmOff);
                    });
                    if (llmOff) return;
                    List<ChatMessage> historySnapshot;
                    lock (_historyLock) { historySnapshot = new List<ChatMessage>(_conversationHistory); }
                    var result = await ApiClient.AskAsync(label, Config.Advanced.SystemPrompt,
                        Config.Advanced.MaxResponseTokens, historySnapshot, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(result.Content))
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _overlay?.MarkAnswered(result.Content, DateTime.Now, 0);
                            _overlay?.SetStatus(T("L_Listening"));
                        });
                    }
                }
            }
            catch (Exception ex) { Logger.Error("FlushAndSend failed", ex, "Loop"); }
        });
    }

    public async Task SendSecretMessage(string text)
    {
        // «LLM выкл» (чистый транскрибатор): в LLM не уходит и явное сообщение —
        // подсказываем, почему поле «не работает», вместо тихого пузыря без ответа.
        if (Config.Ui.LlmDisabled)
        {
            Notifier.ShowInfo("LLM выключен", "Включите LLM кнопкой на нижней панели, чтобы отправлять сообщения.");
            return;
        }
        var label = $"[secret] {text}";
        await Dispatcher.InvokeAsync(() =>
        {
            _overlay?.AddSendingItem(label, 0, DateTime.Now, DateTime.Now, DateTime.Now, 0);
            _overlay?.SetStatus("Думаю над [secret]…");
        });
        try
        {
            List<ChatMessage> historySnapshot;
            lock (_historyLock) { historySnapshot = new List<ChatMessage>(_conversationHistory); }
            var result = await ApiClient.AskAsync(label, Config.Advanced.SystemPrompt,
                Config.Advanced.MaxResponseTokens, historySnapshot, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(result.Content))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.MarkAnswered(result.Content, DateTime.Now, 0);
                    _overlay?.SetStatus(T("L_Listening"));
                });
                lock (_historyLock)
                {
                    _conversationHistory.Add(ChatMessage.User(label));
                    _conversationHistory.Add(ChatMessage.Assistant(result.Content));
                    TrimHistory(_conversationHistory, Config.Advanced.HistorySize);
                }
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.MarkFailed("пустой ответ от LLM", DateTime.Now, 0);
                    _overlay?.SetStatus(T("L_Listening"));
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("SendSecretMessage failed", ex, "UI");
            await Dispatcher.InvokeAsync(() => _overlay?.MarkFailed(ex.Message, DateTime.Now, 0));
        }
    }

    public async Task ExpandAnswer(QaPair pair)
    {
        // «LLM выкл»: расширение ответа — тоже запрос в LLM, глушим с подсказкой.
        if (Config.Ui.LlmDisabled)
        {
            Notifier.ShowInfo("LLM выключен", "Включите LLM кнопкой на нижней панели, чтобы расширять ответы.");
            return;
        }
        _overlay?.SetStatus("Расширяю ответ…");
        var expandTemplate = Config.Advanced.ExpandInstruction;
        var expandSection = expandTemplate
            .Replace("{PREVIOUS_QUESTION}", pair.Question ?? "")
            .Replace("{PREVIOUS_ANSWER}", pair.Answer ?? "");
        var fullSystemPrompt = Config.Advanced.SystemPrompt + "\n\n---\n" + expandSection;
        try
        {
            var result = await ApiClient.AskAsync("[EXPAND] Разверни последний ответ",
                fullSystemPrompt, 400, new List<ChatMessage>(), CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(result.Content))
            {
                pair.ExpandedAnswer = result.Content;
                _overlay?.SetStatus("Ответ расширен");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ExpandAnswer failed", ex, "UI");
            Notifier.ShowWarning("Ошибка", ex.Message);
        }
    }

    private string CollectFullTranscript()
    {
        var sb = new System.Text.StringBuilder();
        if (_overlay != null)
        {
            foreach (var qa in _overlay.History)
            {
                var ts = qa.SttReceivedAt ?? qa.ChunkReadyAt ?? DateTime.Now;
                var text = qa.Question
                    .Replace("[Динамик] ", "[Speaker] ")
                    .Replace("[Микрофон] ", "[Interviewer] ");
                sb.AppendLine($"[{ts:HH:mm:ss}] {text}");
            }
        }
        return sb.ToString().Trim();
    }

    // Папка экспорта по умолчанию: «Мои документы»\BrainstormBuddy. У каждого пользователя Windows
    // это его собственная папка документов. Создаётся при обращении (и на старте — см. OnStartup).
    public string DefaultExportDir
    {
        get
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BrainstormBuddy");
            try { System.IO.Directory.CreateDirectory(dir); }
            catch (Exception ex) { Logger.Warn($"Не удалось создать папку экспорта {dir}: {ex.Message}", "App"); }
            return dir;
        }
    }

    /// <summary>
    /// Собирает обезличенный архив с логами/настройками/системой для поддержки и
    /// кладёт его в папку экспорта (Документы\BrainstormBuddy). Возвращает путь к zip.
    /// История вопросов/ответов и транскрибации в архив НЕ попадают.
    /// </summary>
    public string CreateSupportBundle()
    {
        var extra = new System.Text.StringBuilder();
        try
        {
            extra.AppendLine($"Активный STT-движок: {SttEngine?.Name ?? "?"}");
            extra.AppendLine($"Движок STT (конфиг): {Config.Audio.SttEngine}, ускорение: {Config.Audio.SttAccel}");
            extra.AppendLine($"LLM: {Config.Api.ChatModel} @ {Config.Api.BaseUrl}");
            extra.AppendLine($"STT-сервер: {Config.Api.SttModel} @ {Config.Api.SttBaseUrl}");
            extra.AppendLine($"Режим нарезки: {Config.Audio.EndpointMode}, SampleRate: {Config.Audio.SampleRate}");
            extra.AppendLine($"Мульти-агент: {Config.MultiAgent.Enabled}, сценарий: {Config.MultiAgent.ActiveScenarioId}");
            extra.AppendLine($"Тема: {Config.Ui.Theme}, язык: {Config.Ui.Language}, инженерный режим: {Config.Ui.EngineerMode}");
            extra.AppendLine($"Логирование: файл={Logger.FileEnabled}, verbose={Logger.Verbose}");
        }
        catch (Exception ex) { extra.AppendLine($"(состояние собрать не удалось: {ex.Message})"); }

        var zip = DiagnosticsService.CreateSupportBundle(DefaultExportDir, AppDataDir, ConfigPath, extra.ToString());
        Logger.Info($"Support bundle created: {zip}", "Diag");
        return zip;
    }

    private string ResolveSavePath(string prefix)
    {
        var dir = Config.Ui.SavePath;
        if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
        {
            dir = DefaultExportDir;                             // по умолчанию — Документы\BrainstormBuddy
            if (!System.IO.Directory.Exists(dir)) dir = AppDataDir; // крайний фолбэк, если Документы недоступны
        }
        var ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        return System.IO.Path.Combine(dir, $"{prefix}_{ts}.txt");
    }

    // Список истории, используемый в MainLoop. Меняется через ResetConversation()
    // при смене активного системного промпта (= новый чат).
    private readonly object _historyLock = new();
    private List<ChatMessage> _conversationHistory = new();

    public void ResetConversation()
    {
        lock (_historyLock)
        {
            _conversationHistory = new List<ChatMessage>();
        }
        _totalPromptTokens = 0;
        _totalCompletionTokens = 0;
        try
        {
            Dispatcher.Invoke(() =>
            {
                _overlay?.ClearHistory();
                UpdateTokenDisplay();
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"ResetConversation: overlay update failed: {ex.Message}", "App");
        }
        Logger.Info("Conversation reset (new chat started)", "App");
    }

    public void ApplyTheme(string themeName)
    {
        try
        {
            if (string.IsNullOrEmpty(themeName)) themeName = "HackerTheme";
            var uri = new Uri($"Themes/{themeName}.xaml", UriKind.Relative);
            var newDict = new System.Windows.ResourceDictionary { Source = uri };

            var existing = System.Linq.Enumerable.FirstOrDefault(Current.Resources.MergedDictionaries, d => d.Source != null && d.Source.OriginalString.StartsWith("Themes/"));
            if (existing != null)
            {
                Current.Resources.MergedDictionaries.Remove(existing);
            }
            Current.Resources.MergedDictionaries.Insert(0, newDict);
            Logger?.Debug($"Theme applied: {themeName}", "UI");
        }
        catch (Exception ex)
        {
            Logger?.Error($"Failed to apply theme {themeName}", ex, "UI");
        }
    }

    /// <summary>Переключает словарь строк UI (Resources/Strings.{lang}.xaml). lang: "ru" | "en".</summary>
    public void ApplyLanguage(string lang)
    {
        try
        {
            if (lang != "en") lang = "ru";
            var uri = new Uri($"Resources/Strings.{lang}.xaml", UriKind.Relative);
            var newDict = new ResourceDictionary { Source = uri };

            var existing = Enumerable.FirstOrDefault(Current.Resources.MergedDictionaries,
                d => d.Source != null && d.Source.OriginalString.Contains("Resources/Strings."));
            if (existing != null)
                Current.Resources.MergedDictionaries.Remove(existing);
            Current.Resources.MergedDictionaries.Add(newDict);
            Logger?.Info($"Language applied: {lang}", "UI");
        }
        catch (Exception ex)
        {
            Logger?.Error($"Failed to apply language {lang}", ex, "UI");
        }
    }

    /// <summary>Локализованная строка по ключу (для статусов из кода). Фолбэк — сам ключ.</summary>
    public string T(string key) => Current.TryFindResource(key) as string ?? key;

    public void ApplyLiveConfigChanges()
    {
        Logger.Info("Applying live config changes", "Config");
        if (_overlay != null)
        {
            _overlay.ApplyUiConfig(Config.Ui);
            _overlay.SetActivePresets(SettingsVm.SystemPromptPresets, Config.Advanced.ActiveSystemPromptName);
        }
        ApplyEndpointModeLive();   // ДО UpdateConfig: в ручном режиме порог тишины применится из конфига
        AudioEngine.UpdateConfig(Config.Audio);
        Logger.Configure(Config.Logging);
        RecreateSttEngineIfChanged();   // горячая замена движка распознавания, если его сменили
        Logger.Debug("Live config applied", "Config");
    }

    private void OnHotkeyPressed(object? sender, string actionId)
    {
        Logger.Info($"Hotkey pressed: {actionId}", "UI");
        Dispatcher.Invoke(() =>
        {
            switch (actionId)
            {
                case "toggle":
                    if (_overlay == null || !_overlay.IsVisible)
                    {
                        Logger.Debug("Hotkey → show overlay", "UI");
                        ShowOverlay();
                    }
                    else
                    {
                        Logger.Debug("Hotkey → hide overlay", "UI");
                        HideOverlay();
                    }
                    break;
                case "opacity":
                    Config.Ui.WindowOpacity = Config.Ui.WindowOpacity >= 1.0
                        ? 0.4
                        : Math.Min(1.0, Config.Ui.WindowOpacity + 0.2);
                    _overlay?.ApplyUiConfig(Config.Ui);
                    Logger.Info($"Opacity changed to {Config.Ui.WindowOpacity:F2}", "UI");
                    Notifier.ShowInfo("Прозрачность", $"Окно: {Config.Ui.WindowOpacity:P0}");
                    break;
                case "settings":
                    OpenSettings();
                    break;
                case "logs":
                    if (LogWindow != null && LogWindow.IsVisible) HideLogWindow();
                    else ShowLogWindow();
                    break;
                case "pause":
                    TogglePause();
                    break;
                case "llmpause":
                    ToggleLlmPause();
                    break;
                case "screenshot":
                    _overlay?.ToggleScreenCaptureVisibility();
                    break;
            }
        });
    }

    public void TogglePause()
    {
        if (IsPaused)
        {
            IsPaused = false;
            try
            {
                AudioEngine.Start();
                Logger.Info("Audio resumed", "Audio");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to resume audio", ex, "Audio");
            }
            _overlay?.SetStatus(T("L_Listening"));
            _overlay?.RefreshAudioPauseButton();
            _overlay?.RefreshLlmPauseButton();
            Notifier.ShowInfo("Пауза снята", "Захват аудио возобновлён");
        }
        else
        {
            IsPaused = true;
            try
            {
                // Флашим открытые фразы ДО остановки захвата: иначе недоговорённая фраза
                // замерзает в буфере на всю паузу и после возобновления уезжает в STT одним
                // куском-переростком (живой лог: фраза 27с висела минуту → чанк 57.4с).
                FlushOpenUtterances();
                AudioEngine.Stop();
                Logger.Info("Audio paused", "Audio");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to pause audio", ex, "Audio");
            }
            _overlay?.RefreshAudioPauseButton();
            Notifier.ShowInfo("Пауза", "Захват аудио остановлен");
        }
        // Состояние переживает перезапуск (персист кнопок нижней панели). Пишем сразу,
        // а не только в OnExit — иначе краш/kill терял бы выбор юзера.
        Config.Ui.AudioPaused = IsPaused;
        SaveConfigSafe();
    }

    /// <summary>
    /// «LLM выкл» (кнопка на нижней панели): чистый транскрибатор. STT-тексты показываются,
    /// но задачи в LLM не ставятся, а плашка здоровья LLM глушится. Отличие от IsLlmPaused
    /// (хоткей Ctrl+Shift+Y): без пометок «на паузе» в пузырях и с персистом в конфиг.
    /// </summary>
    public void ToggleLlmDisabled()
    {
        Config.Ui.LlmDisabled = !Config.Ui.LlmDisabled;
        Logger.Info($"LLM {(Config.Ui.LlmDisabled ? "DISABLED (чистый транскрибатор)" : "enabled")}", "App");
        // Плашка «Ошибка ИИ (LLM)» в этом режиме неуместна — пересобираем баннер сразу.
        UpdateConnectionNotice();
        SaveConfigSafe();
    }

    /// <summary>«Динамик выкл»: глушит приём loopback-канала без остановки захвата (см. AudioCaptureEngine.LoopbackMuted).</summary>
    public void ToggleLoopbackMute()
    {
        Config.Ui.LoopbackMuted = !Config.Ui.LoopbackMuted;
        AudioEngine.LoopbackMuted = Config.Ui.LoopbackMuted;
        Logger.Info($"Loopback {(Config.Ui.LoopbackMuted ? "MUTED" : "unmuted")} (динамик {(Config.Ui.LoopbackMuted ? "выкл" : "вкл")})", "App");
        SaveConfigSafe();
    }

    /// <summary>Флаш открытых фраз обоих каналов в STT-очередь. Вызывать ПЕРЕД AudioEngine.Stop().</summary>
    private void FlushOpenUtterances()
    {
        var writer = _chunkWriter;
        if (writer == null) return; // MainLoop ещё не поднял канал или уже гасится
        FlushBufferToStt(writer, AudioBuffer, "loopback");
        if (MicAudioBuffer != null)
            FlushBufferToStt(writer, MicAudioBuffer, "mic");
    }

    private void FlushBufferToStt(System.Threading.Channels.ChannelWriter<ChunkItem> writer, AudioBuffer buffer, string source)
    {
        try
        {
            // Flush отдаёт wav только если фраза реально открыта и длиннее MinSpeechMs.
            if (!buffer.Flush(out var wav) || wav.Length == 0) return;
            // Тот же путь, что у чанков VAD в MainLoop: очередь + пульс маркера в оверлее.
            if (writer.TryWrite(new ChunkItem(wav, source)))
            {
                Logger.Info($"Pause flush: {wav.Length} bytes [{source}]", "Loop");
                _overlay?.PulseChunkMarker();
            }
            else
            {
                Logger.Warn($"Pause flush dropped (queue full): {wav.Length} bytes [{source}]", "Loop");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Pause flush [{source}] failed: {ex.Message}", "Loop");
        }
    }

    public void ToggleLlmPause()
    {
        if (IsLlmPaused)
        {
            IsLlmPaused = false;
            Logger.Info("LLM resumed", "App");
            _overlay?.SetStatus(T("L_Listening"));
            _overlay?.RefreshLlmPauseButton();
            Notifier.ShowInfo("LLM возобновлён", "Вопросы снова отправляются в LLM");
        }
        else
        {
            IsLlmPaused = true;
            Logger.Info("LLM paused (audio still recording)", "App");
            _overlay?.RefreshLlmPauseButton();
            Notifier.ShowInfo("Пауза LLM", "Вопросы не отправляются в LLM, аудио продолжает записываться");
        }
    }

    private void UpdateTokenDisplay()
    {
        _overlay?.SetTokens(TotalTokens, _totalPromptTokens, _totalCompletionTokens);
    }

    // Иконка трея из встроенного ресурса (лого дизайнера). tray.ico объявлена как <Resource>,
    // поэтому физического файла в output нет — грузим через pack-URI, а не File.Exists.
    private System.Drawing.Icon LoadTrayIcon()
    {
        foreach (var name in new[] { "tray.ico", "app.ico" })
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/Resources/Icons/{name}");
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info != null)
                {
                    using var s = info.Stream;
                    return new System.Drawing.Icon(s, new System.Drawing.Size(32, 32));
                }
            }
            catch (Exception ex) { Logger.Warn($"Tray icon '{name}' load failed: {ex.Message}", "UI"); }
        }
        Logger.Warn("Tray icon resources not found — using system fallback", "UI");
        return System.Drawing.SystemIcons.Application;
    }

    private void SetupTray()
    {
        TrayIcon = new NotifyIcon
        {
            Text = "BrainstormBuddy",
            Visible = true
        };
        TrayIcon.Icon = LoadTrayIcon();

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Показать оверлей");
        showItem.Click += (s, e) => { Logger.Debug("Tray: Show overlay", "UI"); ShowOverlay(); };
        var hideItem = new ToolStripMenuItem("Скрыть оверлей");
        hideItem.Click += (s, e) => { Logger.Debug("Tray: Hide overlay", "UI"); HideOverlay(); };
        var settingsItem = new ToolStripMenuItem("Настройки");
        settingsItem.Click += (s, e) => { Logger.Debug("Tray: Open settings", "UI"); OpenSettings(); };
        var logsItem = new ToolStripMenuItem("Показать логи");
        logsItem.Click += (s, e) => { Logger.Debug("Tray: Show logs", "UI"); ShowLogWindow(); };
        var pauseAudioItem = new ToolStripMenuItem("Пауза захвата аудио");
        pauseAudioItem.Click += (s, e) =>
        {
            Logger.Debug("Tray: Toggle audio pause", "UI");
            Dispatcher.Invoke(TogglePause);
        };
        var pauseLlmItem = new ToolStripMenuItem("Пауза отправки в LLM");
        pauseLlmItem.Click += (s, e) =>
        {
            Logger.Debug("Tray: Toggle LLM pause", "UI");
            Dispatcher.Invoke(ToggleLlmPause);
        };
        var clearHistoryItem = new ToolStripMenuItem("Очистить историю");
        clearHistoryItem.Click += (s, e) =>
        {
            Logger.Debug("Tray: Clear history", "UI");
            _overlay?.ClearHistory();
        };
        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (s, e) => { Logger.Info("Tray: Exit requested", "UI"); Shutdown(); };

        menu.Items.Add(showItem);
        menu.Items.Add(hideItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pauseAudioItem);
        menu.Items.Add(pauseLlmItem);
        menu.Items.Add(clearHistoryItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        TrayIcon.ContextMenuStrip = menu;
        TrayIcon.DoubleClick += (s, e) => { Logger.Debug("Tray: Double-click → show", "UI"); ShowOverlay(); };
    }

    private async Task MainLoop(CancellationToken ct)
    {
        Logger.Info("MainLoop: started (producer: AudioBuffer → Channel; consumer: WorkerLoop)", "Loop");
        var startedAt = DateTime.Now;

        try
        {
            // Восстановленная пауза (Ui.AudioPaused): захват не стартуем — его поднимет TogglePause.
            if (!IsPaused)
            {
                AudioEngine.Start();
                Logger.Info("Audio engine started", "Audio");
            }
            else
            {
                Logger.Info("Audio engine NOT started: restored paused state", "Audio");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start audio engine", ex, "Audio");
        }

        // Bounded queue: 64 чанка в полёте (≈12 мин аудио при 12s чанках).
        // TryWrite (non-blocking) — если очередь полна, чанк дропается.
        // Это критично чтобы VAD не копил 40+ секунд пока воркер занят.
        var chunkChannel = System.Threading.Channels.Channel.CreateBounded<ChunkItem>(new System.Threading.Channels.BoundedChannelOptions(64)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            // НЕ SingleWriter: кроме MainLoop сюда пишет флаш при паузе (UI-поток, TogglePause).
            SingleWriter = false
        });
        _chunkWriter = chunkChannel.Writer;
        _chunkReader = chunkChannel.Reader;

        // Очередь ОТВЕТОВ LLM: STT-воркер кладёт сюда задачу и НЕ блокируется, отдельный
        // answer-воркер отвечает и подставляет ответ в свой пузырь. Так транскрипция «течёт рекой»,
        // а медленный/зависший LLM не стопорит показ следующих реплик (развязка STT↔LLM).
        // FullMode=Wait + TryWrite: если ответы копятся (LLM не успевает) — вопрос показан без ответа,
        // а не теряется молча (STT-река важнее ответа на конкретную старую реплику).
        var answerChannel = System.Threading.Channels.Channel.CreateBounded<AnswerJob>(new System.Threading.Channels.BoundedChannelOptions(32)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workerTask = Task.Run(() => WorkerLoopAsync(chunkChannel.Reader, answerChannel.Writer, workerCts.Token), workerCts.Token);
        var answerTask = Task.Run(() => AnswerWorkerLoopAsync(answerChannel.Reader, workerCts.Token), workerCts.Token);

        var totalEnqueued = 0;
        var totalDropped = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool gotChunk = false;
                if (AudioEngine.TryGetChunk(out var wav))
                {
                    var item = new ChunkItem(wav, "loopback");
                    gotChunk = chunkChannel.Writer.TryWrite(item);
                    if (gotChunk) { totalEnqueued++; Logger.Info($"Chunk #{totalEnqueued} [loopback] enqueued: {wav.Length} bytes", "Loop"); _overlay?.PulseChunkMarker(); }
                    else { totalDropped++; Logger.Warn($"Chunk dropped (queue full): {wav.Length} bytes", "Loop"); }
                }
                if (!gotChunk && MicAudioBuffer != null && AudioEngine.TryGetMicChunk(out var micWav))
                {
                    var item = new ChunkItem(micWav, "mic");
                    gotChunk = chunkChannel.Writer.TryWrite(item);
                    if (gotChunk) { totalEnqueued++; Logger.Info($"Chunk #{totalEnqueued} [mic] enqueued: {micWav.Length} bytes", "Loop"); _overlay?.PulseChunkMarker(); }
                    else { totalDropped++; Logger.Warn($"Mic chunk dropped: {micWav.Length} bytes", "Loop"); }
                }
                if (!gotChunk)
                    await Task.Delay(50, ct);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("MainLoop: cancellation requested", "Loop");
                break;
            }
            catch (Exception ex)
            {
                ErrorHandler.Handle(ex, "MainLoop");
                await Task.Delay(1000, ct);
            }
        }

        // Сигнал воркерам: данных больше не будет. STT-воркер допишет остатки в очередь ответов,
        // затем закрываем её и ждём answer-воркер.
        _chunkWriter = null; // флаш при паузе больше не должен писать в закрываемый канал
        _chunkReader = null; // индикатор глубины очереди возвращается к нулю
        chunkChannel.Writer.TryComplete();
        try { await workerTask; } catch { /* ignore */ }
        answerChannel.Writer.TryComplete();
        try { await answerTask; } catch { /* ignore */ }
        workerCts.Dispose();

        // Тёплый старт: запоминаем выученные пороги для следующей сессии (прогрев адаптива — минуты).
        if (_loopbackAdaptive != null && _loopbackAdaptive.IsWarm)
            Config.Audio.AdaptiveLastLoopbackSeconds = Math.Round(_loopbackAdaptive.AppliedSeconds, 2);
        if (_micAdaptive != null && _micAdaptive.IsWarm)
            Config.Audio.AdaptiveLastMicSeconds = Math.Round(_micAdaptive.AppliedSeconds, 2);

        var uptime = DateTime.Now - startedAt;
        Logger.Info($"MainLoop: stopped. Uptime: {uptime:hh\\:mm\\:ss}, chunks enqueued: {totalEnqueued}", "Loop");
    }

    private async Task WorkerLoopAsync(System.Threading.Channels.ChannelReader<ChunkItem> reader,
        System.Threading.Channels.ChannelWriter<AnswerJob> answerWriter, CancellationToken ct)
    {
        Logger.Info("WorkerLoop (STT): started", "Worker");
        var lastConnectionError = DateTime.MinValue;
        var totalProcessed = 0;
        var startedAt = DateTime.Now;

        try
        {
            await foreach (var chunkItem in reader.ReadAllAsync(ct))
            {
                try
                {
                    totalProcessed++;
                    var chunkStart = DateTime.Now;
                    var wav = chunkItem.Wav;
                    var source = chunkItem.Source;

                    var byteRate = Config.Audio.SampleRate * 2;
                    var audioSeconds = (wav.Length - 44) / (double)byteRate;
                    if (audioSeconds < 0) audioSeconds = 0;

                    var sttStart = DateTime.Now;
                    string? text = null;
                    bool sttError = false;
                    try
                    {
                        await Dispatcher.InvokeAsync(() => _overlay?.SetTimelineState(TimelineState.Sending));
                        text = await SttEngine.TranscribeAsync(wav, ct);
                        // После STT — Idle (не зелёный): зелёный (Received) теперь означает ОТВЕТ LLM,
                        // его ставит answer-воркер. Иначе синий(STT)→зелёный мигали бы даже без ответа.
                        await Dispatcher.InvokeAsync(() => _overlay?.SetTimelineState(TimelineState.Idle));
                    }
                    catch (Exception ex)
                    {
                        sttError = true;
                        Logger.Warn($"STT error: {ex.Message}", "Worker");
                    }
                    var sttEnd = DateTime.Now;
                    var sttMs = (sttEnd - sttStart).TotalMilliseconds;
                    if (text != null)
                    {
                        var preview = text.Length > 80 ? text.Substring(0, 80) + "…" : text;
                        Logger.Info($"STT [{source}] done in {sttMs:F0}ms: '{preview}'", "Worker");
                        AudioDiagnostics.SttResponseReceived(text);
                    }
                    else if (!sttError)
                    {
                        Logger.Debug($"STT returned null/empty in {sttMs:F0}ms", "Worker");
                    }

                    if (string.IsNullOrWhiteSpace(text) || sttError)
                    {
                        await Dispatcher.InvokeAsync(() => _overlay?.SetTimelineState(TimelineState.Idle));
                        continue;
                    }

                    var clean = TextPostProcessor.Clean(text);
                    // Фильтр текста больше НИЧЕГО не гейтит (решение владельца): показываем всё
                    // И всё отправляем в LLM — реакцией на короткие реплики управляет системный
                    // промпт. Clean остался только как чистка (повторы/паразиты).
                    if (!string.IsNullOrWhiteSpace(clean.Text))
                    {
                        if (clean.Text != text) Logger.Debug($"STT cleaned: '{text}' → '{clean.Text}'", "Worker");
                        text = clean.Text;
                    }

                    // Вторичный сигнал темпа для адаптивного эндпойнтинга (симв/с завершённого чанка).
                    (source == "mic" ? _micAdaptive : _loopbackAdaptive)?.NoteTranscript(text.Length, audioSeconds);

                    // Семантическая склейка (semantic-режим): придержать оборванную на союзе/предлоге
                    // мысль до следующего фрагмента того же канала. Ограничено maxHold — не залипает.
                    var agg = source == "mic" ? _micAgg : _loopbackAgg;
                    if (agg != null)
                    {
                        var ready = agg.Push(text);
                        if (ready == null)
                        {
                            Logger.Debug($"Semantic hold [{source}]: '{text}' — ждём продолжения", "Worker");
                            await Dispatcher.InvokeAsync(() => _overlay?.SetTimelineState(TimelineState.Idle));
                            continue;
                        }
                        text = ready;
                    }

                    // Label text by source: [Динамик] for loopback, [Микрофон] for mic
                    var label = source == "mic" ? "[Микрофон]" : "[Динамик]";
                    var labeledText = $"{label} {text}";

                    // === STT-РЕКА: показываем вопрос СРАЗУ, ответ LLM догонит асинхронно ===
                    // AddSendingItem создаёт пузырь с «ожидание» и сам маршалит в UI-поток, возвращая QaPair.
                    // «LLM выкл» (чистый транскрибатор): пузырь сразу без блока ответа — ни строки
                    // «ожидание», ни пометок, и задача в LLM-очередь не ставится вовсе.
                    bool llmOff = Config.Ui.LlmDisabled;
                    var pair = _overlay?.AddSendingItem(labeledText, audioSeconds, chunkStart, sttStart, sttEnd, sttMs, transcriptOnly: llmOff);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _overlay?.SetStatus(T("L_Listening"));
                        _overlay?.SetTimelineState(TimelineState.Idle);
                    });

                    if (pair == null) continue; // оверлей закрыт
                    Logger.Info($"Overlay emitted [{source}] chunk #{totalProcessed}", "Worker");

                    if (llmOff)
                    {
                        Logger.Info($"LLM disabled — chunk #{totalProcessed} показан как чистый транскрипт", "Worker");
                        continue;
                    }

                    if (IsLlmPaused)
                    {
                        _overlay?.MarkAnswered(pair, "⏸ LLM на паузе", DateTime.Now, 0);
                        Logger.Info($"LLM paused — chunk #{totalProcessed} показан без ответа", "Worker");
                        continue;
                    }

                    // Развязка STT↔LLM: задача ответа уходит в ОТДЕЛЬНУЮ очередь; STT-воркер НЕ ждёт LLM
                    // и сразу берёт следующий чанк. Ответ подставит answer-воркер в этот же пузырь.
                    var job = new AnswerJob(pair, labeledText, text, source, sttMs, audioSeconds, chunkStart);
                    if (!answerWriter.TryWrite(job))
                    {
                        _overlay?.MarkFailed(pair, "LLM не успевает (очередь ответов переполнена)", DateTime.Now, 0);
                        Logger.Warn($"Answer queue full — фраза показана без ответа (text='{labeledText.Substring(0, Math.Min(60, labeledText.Length))}…')", "Worker");
                    }
                }
                catch (HttpRequestException ex)
                {
                    if (DateTime.Now - lastConnectionError > TimeSpan.FromSeconds(30))
                    {
                        Logger.Warn($"Network error: {ex.Message}", "Worker");
                        lastConnectionError = DateTime.Now;
                        await Dispatcher.InvokeAsync(() => _overlay?.SetStatus(T("L_St_NoConnection")));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ErrorHandler.Handle(ex, "WorkerLoop");
                }
            }
        }
        catch (OperationCanceledException) { Logger.Info("WorkerLoop: cancelled", "Worker"); }

        var uptime = DateTime.Now - startedAt;
        Logger.Info($"WorkerLoop (STT): stopped. Uptime: {uptime:hh\\:mm\\:ss}, processed: {totalProcessed}", "Worker");
    }

    // ── Answer-воркер: отвечает LLM'ом на задачи из очереди и подставляет ответ в свой пузырь. ──
    // По ОДНОЙ задаче за раз (FIFO) — так порядок истории диалога сохраняется, а STT-воркер тем
    // временем свободно продолжает распознавать (развязка STT↔LLM).
    private async Task AnswerWorkerLoopAsync(System.Threading.Channels.ChannelReader<AnswerJob> reader, CancellationToken ct)
    {
        Logger.Info("AnswerWorker (LLM): started", "Answer");
        var lastConnectionError = DateTime.MinValue;
        var totalAnswered = 0;
        var startedAt = DateTime.Now;
        try
        {
            await foreach (var job in reader.ReadAllAsync(ct))
            {
                var pair = job.Pair;
                var labeledText = job.LabeledText;
                var text = job.Text;
                var source = job.Source;
                var askStart = DateTime.Now;
                try
                {
                    // «LLM выкл» щёлкнули, пока задача уже стояла в очереди: закрываем пузырь
                    // пустым ответом БЕЗ пометок (пустой Answer + Answered прячет блок ответа).
                    if (Config.Ui.LlmDisabled) { _overlay?.MarkAnswered(pair, "", DateTime.Now, 0); continue; }
                    if (IsLlmPaused) { _overlay?.MarkAnswered(pair, "⏸ LLM на паузе", DateTime.Now, 0); continue; }
                    _overlay?.MarkLlmSent(pair, askStart);

                    // ── Мульти-агентный режим ──
                    if (Config.MultiAgent.Enabled && Orchestrator != null)
                    {
                        // Гейт «это вопрос интервьюера?»: агентов зовём только на реплику из динамика,
                        // похожую на вопрос. Речь кандидата ([Микрофон]), филлер, обрывки — в контекст без ответа.
                        bool isInterviewerQuestion = source != "mic" && LooksLikeQuestion(text);
                        if (!isInterviewerQuestion)
                        {
                            // Гейт: агентов НЕ звали — помечаем нейтрально «в контексте», а НЕ «промолчали»
                            // (ping-pong не было). Реплика при этом остаётся в контексте диалога для агентов.
                            Orchestrator.NoteContext(labeledText);
                            _overlay?.MarkAnswered(pair, T("L_MicNoted"), DateTime.Now, 0);
                            continue;
                        }

                        var responses = await Orchestrator.ProcessAsync(labeledText, Config.Api.ChatModel, ct);
                        var maEnd = DateTime.Now;
                        var maMs = (maEnd - askStart).TotalMilliseconds;

                        foreach (var r in responses)
                            SettingsVm.AddLlmLog(new LlmLogEntry
                            {
                                Timestamp = askStart, ScenarioName = Orchestrator.ActiveScenario?.Name ?? "", AgentName = r.AgentName,
                                Color = r.Color, UserText = labeledText, Response = r.Text, LatencyMs = r.LatencyMs, IsSilent = r.IsSilent, IsError = r.Error != null
                            });
                        Interlocked.Add(ref _totalPromptTokens, responses.Sum(r => r.PromptTokens));
                        Interlocked.Add(ref _totalCompletionTokens, responses.Sum(r => r.CompletionTokens));

                        var visible = responses.Where(r => !r.IsSilent).ToList();
                        if (visible.Count > 0)
                        {
                            _overlay?.ShowMultiAgent(pair, visible);
                            totalAnswered++;
                            await Dispatcher.InvokeAsync(() => _overlay?.SetTimelineState(TimelineState.Received)); // зелёный: агенты ответили
                        }
                        else
                        {
                            // Агенты РЕАЛЬНО получили вопрос и решили промолчать — вот здесь «промолчали» уместно.
                            _overlay?.MarkAnswered(pair, T("L_AgentsSilent"), maEnd, maMs);
                        }
                        await Dispatcher.InvokeAsync(() => { _overlay?.SetTokens(TotalTokens, TotalPromptTokens, TotalCompletionTokens); UpdateTokenDisplay(); });
                        continue;
                    }

                    // ── Одиночный LLM ── (снимок истории берём СЕЙЧАС — предыдущие ответы уже в истории, FIFO)
                    List<ChatMessage> historySnapshot;
                    lock (_historyLock) { historySnapshot = new List<ChatMessage>(_conversationHistory); }
                    var askResult = await ApiClient.AskAsync(labeledText, Config.Advanced.SystemPrompt, Config.Advanced.MaxResponseTokens, historySnapshot, ct);
                    var askEnd = DateTime.Now;
                    var askMs = (askEnd - askStart).TotalMilliseconds;

                    var answer = askResult?.Content;
                    if (!string.IsNullOrEmpty(answer))
                    {
                        answer = answer.TrimStart();
                        int _c;
                        while (answer.StartsWith("[") && (_c = answer.IndexOf(']')) > 0)
                            answer = answer.Substring(_c + 1).TrimStart();
                    }
                    Logger.Info($"Ask done in {askMs:F0}ms: '{(answer ?? "<null>").Substring(0, Math.Min(80, (answer ?? "<null>").Length))}'", "Answer");

                    if (!string.IsNullOrWhiteSpace(answer))
                    {
                        totalAnswered++;
                        _totalPromptTokens += askResult!.PromptTokens;
                        _totalCompletionTokens += askResult.CompletionTokens;
                        lock (_historyLock)
                        {
                            _conversationHistory.Add(ChatMessage.User(labeledText));
                            _conversationHistory.Add(ChatMessage.Assistant(answer));
                            TrimHistory(_conversationHistory, Config.Advanced.HistorySize);
                        }
                        QaLogger?.Log(labeledText, answer!, "OK", job.SttMs, askMs, job.AudioSeconds, Config.Api.ChatModel);
                        _overlay?.MarkAnswered(pair, answer!, askEnd, askMs);
                        await Dispatcher.InvokeAsync(() => { _overlay?.SetTimelineState(TimelineState.Received); UpdateTokenDisplay(); }); // зелёный: LLM ответил
                    }
                    else if (!string.IsNullOrEmpty(askResult?.Error))
                    {
                        // Реальная ошибка провайдера (401/402/404/429/сеть) — показываем КОД+текст, не generic.
                        Logger.Warn($"LLM error: {askResult!.Error}", "Answer");
                        QaLogger?.Log(labeledText, $"[ERROR: {askResult.Error}]", "FAIL", job.SttMs, askMs, job.AudioSeconds, Config.Api.ChatModel);
                        _overlay?.MarkFailed(pair, askResult.Error, askEnd, askMs);
                    }
                    else
                    {
                        // 200, но контент пуст (частый случай reasoning-моделей: всё ушло в «мысли»,
                        // мало токенов). Объясняем причину, а не просто «пустой ответ».
                        Logger.Warn("Empty answer from LLM (200, no content)", "Answer");
                        QaLogger?.Log(labeledText, "[пустой ответ]", "EMPTY", job.SttMs, askMs, job.AudioSeconds, Config.Api.ChatModel);
                        _overlay?.MarkFailed(pair, "модель вернула пустой ответ — увеличьте лимит токенов или смените модель (reasoning-модели «съедают» ответ)", askEnd, askMs);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex)
                {
                    var failMs = (DateTime.Now - askStart).TotalMilliseconds;
                    QaLogger?.Log(labeledText, $"[ERROR: {ex.Message}]", "FAIL", job.SttMs, failMs, job.AudioSeconds, Config.Api.ChatModel);
                    _overlay?.MarkFailed(pair, ex.Message, DateTime.Now, failMs);
                    if (DateTime.Now - lastConnectionError > TimeSpan.FromSeconds(30))
                    {
                        Logger.Warn($"Network error: {ex.Message}", "Answer");
                        lastConnectionError = DateTime.Now;
                        await Dispatcher.InvokeAsync(() => _overlay?.SetStatus(T("L_St_NoConnection")));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"AskAsync failed: {ex.Message}", ex, "Answer");
                    var failMs = (DateTime.Now - askStart).TotalMilliseconds;
                    QaLogger?.Log(labeledText, $"[ERROR: {ex.Message}]", "FAIL", job.SttMs, failMs, job.AudioSeconds, Config.Api.ChatModel);
                    _overlay?.MarkFailed(pair, ex.Message, DateTime.Now, failMs);
                }
            }
        }
        catch (OperationCanceledException) { Logger.Info("AnswerWorker: cancelled", "Answer"); }
        var uptime = DateTime.Now - startedAt;
        Logger.Info($"AnswerWorker (LLM): stopped. Uptime: {uptime:hh\\:mm\\:ss}, answered: {totalAnswered}", "Answer");
    }

    private static void TrimHistory(List<ChatMessage> history, int maxMessages)
    {
        while (history.Count > maxMessages)
            history.RemoveAt(0);
    }

    private int CountHistory()
    {
        lock (_historyLock) { return _conversationHistory.Count; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            Logger?.Info("=== BrainstormBuddy exiting ===", "App");
            Logger?.Info($"Exit code: {e.ApplicationExitCode}", "App");

            _watchdog?.Dispose();
            _mainLoopCts?.Cancel();
            Logger?.Debug("Main loop cancel requested", "App");
            try { _mainLoopTask?.Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { Logger?.Warn($"Main loop wait failed: {ex.Message}", "App"); }

            HotkeyManager?.Dispose();
            Logger?.Debug("HotkeyManager disposed", "App");

            AudioEngine?.Dispose();
            Logger?.Debug("AudioEngine disposed", "Audio");

            // Контейнер намеренно не останавливаем — быстрый старт при следующем запуске
            LocalStt?.Dispose();
            Logger?.Debug("LocalSttService disposed", "LocalStt");

            try
            {
                new ConfigLoader(ConfigPath, Logger!).Save(Config);
                Logger?.Info("Config saved on exit", "Config");
            }
            catch (Exception ex)
            {
                Logger?.Error("Failed to save config on exit", ex, "Config");
            }

            // Null-чек обязателен: при выходе «второй экземпляр» (mutex занят) трей ещё не создан.
            if (TrayIcon != null)
            {
                TrayIcon.Visible = false;
                TrayIcon.Dispose();
            }
            Notifier?.Dispose();
            LogWindow?.Close();

            Logger?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during exit: {ex}");
        }
        finally
        {
            // Отпускаем single-instance mutex последним: пока он занят, новый экземпляр не стартует.
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* не владеем (гонка выхода) */ }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            base.OnExit(e);
        }
    }
}

internal readonly record struct ChunkItem(byte[] Wav, string Source);

// Задача ответа LLM: STT-воркер кладёт сюда после показа вопроса, answer-воркер отвечает
// и подставляет ответ в Pair (тот самый пузырь). Развязка STT↔LLM (транскрипция «рекой»).
internal sealed record AnswerJob(
    QaPair Pair, string LabeledText, string Text, string Source,
    double SttMs, double AudioSeconds, DateTime ChunkStart);
