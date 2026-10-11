using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Audio;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Ocr;
using BrainstormBuddy.Core.Speaker;
using BrainstormBuddy.Native;
using BrainstormBuddy.Services;
using BrainstormBuddy.Transcription;

using BrainstormBuddy.Memory;
using BrainstormBuddy.Memory.Embeddings;
using BrainstormBuddy.Memory.Extraction;
using BrainstormBuddy.Memory.Jobs;
using BrainstormBuddy.Memory.Recall;
using BrainstormBuddy.Memory.Storage;
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
    // Диаризация спикеров (opt-in): lazy-создаётся SyncSpeakerDiarizer при Enabled=true,
    // пересоздаётся/гасится при смене ключа настроек (как STT-движок). null = фича выключена
    // или модель не найдена — pipeline работает как раньше. Движок один — NemotronDiarizer
    // (стрим: живёт на AudioEngine.LoopbackSamples, IdentifyRange по абсолютным индексам чанка).
    private IStreamingSpeakerDiarizer? _speakerDiarizer;
    // Подписка Nemotron-стрима на loopback-фреймы движка; нужна для отписки при hot-swap.
    private Action<float[]>? _loopbackFeedHandler;
    // Baseline diar-стрима в абсолютной шкале AudioBuffer (сэмплов до первого fed-фрейма):
    // NemotronDiarizer считает индексы от первого PushLoopback, а ChunkInfo — от старта буфера.
    // -1 = ещё ни одного фрейма в стрим не ушло. Пишет mixer-поток (первый Invoke), читает MainLoop.
    private long _diarFeedBaseline = -1;
    // Последний опознанный спикер loopback-канала в текущем звонке: нужен меткой, когда
    // identify текущего чанка вернул null (ошибка внутри фасада). Поле, а не локальная
    // переменная воркера — ResetConversation должен его обнулять (S-метки живут внутри звонка).
    private string? _lastLoopbackSpeakerId;
    // Эпоха diar-стрима, на которой опознан _lastLoopbackSpeakerId: Reset()/авто-ресинк
    // после переполнения очереди пересоздают стрим и нумерация S<n> начинается заново —
    // кэшированная метка прошлой эпохи не должна маркировать новый звук.
    private int _lastLoopbackSpeakerEpoch = -1;
    public AudioDiagnostics AudioDiagnostics { get; private set; } = null!;
    public SettingsViewModel SettingsVm { get; private set; } = null!;
    public HotkeyManager? HotkeyManager { get; private set; }
    public ToastNotifier Notifier { get; private set; } = null!;
    public NotifyIcon TrayIcon { get; private set; } = null!;
    public LogWindow? LogWindow { get; private set; }
    public QaHistoryLogger? QaLogger { get; private set; }
    // Краш-безопасный журнал живого транскрипта (meetings/*.jsonl) — дописывает
    // каждую завершённую реплику на диск сразу; читает его MeetingHistoryWindow.
    public MeetingTranscriptStore? MeetingJournal { get; private set; }

    // ===== Память (спека memory.txt): SQLite-стор, recall, worker джоб =====
    // Всё создаётся только при Config.Memory.Enabled; сбой инициализации логируется
    // и НЕ ломает приложение (MemoryStore остаётся null → память молча выключена).
    public IMemoryStore? MemoryStore => _memoryStore;
    // Публикуется ПОСЛЕДНИМ шагом EnsureMemoryServices, когда весь memory-стек уже готов:
    // читатели (MemoryStore != null → память активна) не должны видеть стор без
    // recall/embedder/воркера — volatile для видимости между потоками.
    private volatile SqliteMemoryStore? _memoryStore;
    public IMemoryRecallService? MemoryRecall { get; private set; }
    public IMemoryExtractionService? MemoryExtractor { get; private set; }
    public IMemoryConsolidator? MemoryConsolidator { get; private set; }
    public IMemoryEmbeddingProvider? MemoryEmbedder { get; private set; }
    private MemoryJobWorker? _memoryWorker;
    private Task? _memoryWorkerTask;
    private CancellationTokenSource? _memoryCts;
    /// <summary>Id текущей встречи в mem_meetings; null — журнал ещё не открыт / память выкл.</summary>
    private long? _currentMemoryMeetingId;
    /// <summary>Id текущей memory-встречи (публично для MemoryWriter и окон); null — память выкл/не начата.</summary>
    public long? CurrentMeetingId => _currentMemoryMeetingId;
    /// <summary>Уже писали JournalPath в текущую встречу (файл jsonl создаётся лениво в Append).</summary>
    private bool _memoryJournalPathWritten;
    /// <summary>Pre-call brief показываем один раз на встречу (при DefaultUse).</summary>
    private bool _preCallBriefShown;
    /// <summary>Открытое немодальное окно брифа: новая встреча закрывает старое.</summary>
    private PreCallBriefWindow? _preCallBrief;
    /// <summary>MemoryReviewWindow уже на экране (одна копия, немодальное).</summary>
    private bool _reviewWindowOpen;
    /// <summary>Review показываем один раз на встречу (id последней показанной).</summary>
    private long _reviewShownForMeetingId;
    /// <summary>Ручные алиасы спикеров (S1→«Иван») — живут до конца встречи (спека PERSON).</summary>
    private readonly Dictionary<string, string> _speakerAliases = new();
    public IReadOnlyDictionary<string, string> SpeakerAliases => _speakerAliases;
    private AskMemoryWindow? _askMemoryWindow;
    private MemoryCenterWindow? _memoryCenterWindow;
    public LocalSttService LocalStt { get; private set; } = null!;
    private UiWatchdog? _watchdog;
    // Crash-hardening: heartbeat + маркер последнего креша + WER-дампы (см. InstallCrashHandlers).
    private CrashGuard? _crashGuard;
    // Признак «прошлая сессия умерла» — запоминаем на старте, тост показываем после создания Notifier.
    private bool _prevSessionAbnormal;

    public string ConfigPath { get; private set; } = string.Empty;
    public string AppDataDir { get; private set; } = string.Empty;
    public bool ApiKeyMissing { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsLlmPaused { get; private set; }
    // Чанков сейчас внутри SttEngine.TranscribeAsync (STT-воркер MainLoop).
    // Пишется Interlocked из фонового потока, читается при резолве статуса.
    private int _sttInFlight;
    /// <summary>Идёт ли сейчас распознавание чанка (влёт в OverlayStatusInput.Transcribing).</summary>
    public bool IsTranscribing => _sttInFlight > 0;

    private int _totalPromptTokens;
    private int _totalCompletionTokens;
    public int TotalPromptTokens => _totalPromptTokens;
    public int TotalCompletionTokens => _totalCompletionTokens;
    public int TotalTokens => _totalPromptTokens + _totalCompletionTokens;

    private OverlayWindow? _overlay;
    private KaraokeWindow? _karaoke;
    // Экран OCR: наблюдатель и его движки. Создаются лениво, при первом включении опции;
    // Tesseract держит нативную память - освобождается в OnExit.
    private ScreenTextWatcher? _screenWatcher;
    // Режим подписки наблюдателя (CandidateDetected vs PreviewRequested) на момент
    // старта — переподнимаем watcher только при реальной смене AutoAnswer, иначе
    // каждый Save настроек дёргал бы рестарт. null = watcher не запущен.
    private bool? _screenWatcherAutoAnswerMode;
    private IScreenCapturer? _screenCapturer;
    private PreprocessingRecognizer? _screenRecognizer;
    private int _screenWarmupStarted;
    // Windows OCR может не подняться на этой машине (нет языковых пакетов/WinRT) —
    // флаг откатывает движок на Tesseract до смены ScreenOcr.Engine или рестарта.
    private bool _winOcrUnavailable;
    // Защита хоткея «распознать сейчас»: повторное срабатывание во время прохода игнорируем.
    private int _screenRecognizeOnceActive;
    // Шар состояния экранного OCR (правый нижний угол, exclude-from-capture).
    // Создаётся лениво при первом показе; живёт между Show/Hide, закрывается в OnExit.
    private OcrStatusIndicatorWindow? _ocrIndicator;
    // Таймер возврата шара из Error в Idle (~3 с, только UI-поток).
    private DispatcherTimer? _ocrErrorTimer;
    private SettingsWindow? _settingsWindow;
    private FileTranscriptionWindow? _fileTranscriptionWindow;
    private MeetingHistoryWindow? _meetingHistoryWindow;
    private BrainstormBuddy.Stt.WhisperSttEngine? _whisperEngine; // ленивый кэш (модель ~600МБ)
    private string _whisperEngineKey = "";  // сигнатура закэшированного движка (модель|accel|vkDev|lang)
    private CancellationTokenSource? _mainLoopCts;
    private Task? _mainLoopTask;
    private bool _shuttingDown;

    /// <summary>Истинный выход приложения (OnExit пройден). Окна, прячущиеся вместо
    /// закрытия (SettingsWindow), должны пропускать реальный Close только по этому флагу.</summary>
    internal bool IsShuttingDown => _shuttingDown;
    // Маркер живой сессии (logs/session.active): удаляется ТОЛЬКО на штатном выходе.
    // Остался после смерти процесса → следующий старт видит «прошлая сессия умерла
    // молча» и оставляет след в crash.log. Пусто, пока логгер не создан.
    private string _sessionSentinelPath = "";
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


    protected override void OnStartup(StartupEventArgs e)
    {
        // Стелс-режим (Ui.StealthProcessName): при заданном alias перезапускаемся
        // копией %APPDATA%\BrainstormBuddy\stealth\<alias>.exe и сразу выходим —
        // в Диспетчере задач процесс виден как <alias>, а не BrainstormBuddy.
        // ВАЖНО: проверка ДО single-instance mutex — иначе копия стартует, пока
        // оригинал ещё держит mutex, и получает «уже запущен» → relaunch-цикл.
        if (StealthModeService.TryRelaunchAsAlias(e.Args))
        {
            Shutdown();
            return;
        }

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
            // Нативный MessageBox намеренно: на этом этапе тема/ресурсы ещё не гарантированы
            // (AppMessageBox упал бы на отсутствующих DynamicResource) — бэклог №59.
            System.Windows.MessageBox.Show(
                UiText.Tr(
                    "BrainstormBuddy уже запущен — ищите его в трее (иконка у часов), или нажмите Ctrl+Shift+T (если значок скрыт).",
                    "BrainstormBuddy is already running — look for it in the tray (near the clock), or press Ctrl+Shift+T (if the icon is hidden)."),
                StealthModeService.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
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

            // Наблюдаемость тихой смерти: до этого падения без managed-исключения
            // (native crash, kill, FailFast сторожа) не оставляли в логе НИЧЕГО —
            // сессия просто обрывалась без '=== exiting ==='. Хендлеры ставим сразу
            // после логгера и ДО тяжёлой инициализации.
            InstallCrashHandlers();

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

            // Язык выставляем СРАЗУ после загрузки конфига: CreateSttEngine (ниже, до
            // ApplyLanguage) уже формирует тексты плашек/фолбэка через UiText.Tr.
            UiText.Lang = Config.Ui.Language == "en" ? "en" : "ru";

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
            _overlay?.ShowDeviceToast(TT("L_Toast_DeviceChanged_Title", "Устройство вывода изменилось"),
                string.Format(TT("L_Toast_DeviceChanged_BodyFmt", "Теперь слушаю: {0}. Захват звука с динамика перезапущен."), name)));
            AudioEngine.DeviceArrived += (s, name) => Dispatcher.InvokeAsync(() =>
            {
                // Троттлинг: BT-гарнитура рождает серию событий (динамик + микрофон + профили).
                if (Environment.TickCount64 - _lastDeviceToastTicks < 5000) return;
                _lastDeviceToastTicks = Environment.TickCount64;
            _overlay?.ShowDeviceToast(TT("L_Toast_NewDevice_Title", "Новое аудио-устройство"),
                string.Format(TT("L_Toast_NewDevice_BodyFmt", "{0} — если хотите использовать его микрофон, выберите в Настройки → Аудио."), name));
            });

            // Диаризация спикеров (opt-in, выкл по умолчанию): создаётся в фоне —
            // загрузка модели не должна задерживать старт и тем более STT-путь.
            // Должна идти ПОСЛЕ создания AudioEngine: Nemotron-движок подписывается на
            // AudioEngine.LoopbackSamples (baseline стрима = шкала буфера с нуля).
            SyncSpeakerDiarizer();
            // Прокидка настроек, которых нет в конструкторе буфера (авто-калибровка порога и пр.):
            // без этого до первого «Сохранить» буферы жили с дефолтами полей, а не с конфигом юзера.
            AudioEngine.UpdateConfig(Config.Audio);
            // Восстановление кнопок нижней панели (персист в Ui.*): мьют динамика/микрофона —
            // сразу в движок; пауза аудио — ДО MainLoop, чтобы он вообще не стартовал захват
            // (без старт/стоп-дёрганья).
            AudioEngine.LoopbackMuted = Config.Ui.LoopbackMuted;
            AudioEngine.MicMuted = Config.Ui.MicMuted;
            IsPaused = Config.Ui.AudioPaused;
            if (IsPaused || Config.Ui.LlmDisabled || Config.Ui.LoopbackMuted || Config.Ui.MicMuted)
                Logger.Info($"Restored overlay toggles: audioPaused={IsPaused}, llmDisabled={Config.Ui.LlmDisabled}, loopbackMuted={Config.Ui.LoopbackMuted}, micMuted={Config.Ui.MicMuted}", "App");
            Logger.Info($"Audio pipeline created (VAD={AudioBuffer.VadAvailable}, DualBuffer={MicAudioBuffer != null}, endpoint={Config.Audio.EndpointMode}, autoCal={Config.Audio.AutoCalibrateThreshold}, rms={Config.Audio.RmsThreshold:F4}, minSpeech={Config.Audio.MinSpeechMs}ms, vadMode={Config.Audio.VadMode})", "Audio");

            SettingsVm = new SettingsViewModel(Config, ApiClient);
            SettingsVm.ThemeChanged += (s, theme) => ApplyTheme(theme);
            ApplyTheme(Config.Ui.Theme);
            ApplyLanguage(Config.Ui.Language);

            // Уважаем LLM-настройки из конфига — без принудительного перетирания на локальный адрес.
            // Дефолт чистой установки: локальный Ollama (AppConfig: BaseUrl 127.0.0.1:11434, ChatModel qwen2.5vl:7b).
            Orchestrator = new AgentOrchestrator(Config.MultiAgent, Config.Api)
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
            // Тост про аварийное завершение прошлой сессии — здесь, потому что Notifier
            // создаётся только сейчас (InstallCrashHandlers работал раньше, без UI).
            if (_prevSessionAbnormal)
                Notifier.ShowWarning(StealthModeService.DisplayName,
                    UiText.Tr(
                        "Предыдущая сессия завершилась аварийно. Подробности — в логах (%AppData%\\BrainstormBuddy\\logs).",
                        "The previous session ended abnormally. Details are in the logs (%AppData%\\BrainstormBuddy\\logs)."));

            QaLogger = new QaHistoryLogger(AppDataDir);
            Logger.Info($"QaHistoryLogger initialized at {QaLogger.FilePath}", "UI");
            MeetingJournal = new MeetingTranscriptStore(AppDataDir);
            Logger.Info($"MeetingJournal initialized at {MeetingJournal.Directory}", "UI");
            Logger.Debug("UI helpers created", "UI");
            // Память: первичная инициализация (если выключена — внутри пропустит).
            // Повторные вызовы из ApplyLiveConfigChanges включают её без рестарта.
            // ВАЖНО: вызов после ShowOverlay — стартовый pre-call brief тоже Topmost и
            // должен подняться выше оверлея, иначе тот перехватит первый клик по кнопкам.


            SetupTray();
            Logger.Info("Tray icon initialized", "UI");

            // Создаём LogWindow сразу, но не показываем
            LogWindow = new LogWindow(Logger);

            // Прогрев окна настроек: оно тяжёлое (много контролов) и раньше пересоздавалось
            // на каждое открытие → 3-5с фриз. Создаём один раз на idle UI-потока и прячем;
            // само окно теперь Hide() вместо Close (см. SettingsWindow.OnClosing).
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_settingsWindow != null || SettingsVm is not { } vm) return;
                    _settingsWindow = new SettingsWindow(vm);
                    _settingsWindow.Closed += (s, e2) =>
                    {
                        Logger.Debug("SettingsWindow closed", "UI");
                        _settingsWindow = null;
                    };
                    Logger.Debug("SettingsWindow prewarmed (hidden)", "UI");
                }
                catch (Exception ex) { Logger.Warn($"Settings prewarm failed: {ex.Message}", "UI"); }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            ShowOverlay();
            EnsureMemoryServices();
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
            // Нативный MessageBox намеренно: крах мог случиться до загрузки темы/ресурсов,
            // AppMessageBox здесь ненадёжен — бэклог №59.
            MessageBox.Show(UiText.Tr(
                    $"Не удалось запустить приложение:\n{ex.Message}",
                    $"Failed to start the application:\n{ex.Message}"),
                StealthModeService.DisplayName + UiText.Tr(" — критическая ошибка", " — critical error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Хендлеры «тихой смерти» + маркер живой сессии. Вызывать сразу после создания
    /// Logger. При следующем старте остаток logs/session.active говорит, что прошлая
    /// сессия завершилась НЕ штатно (crash/kill/повисший UI убит сторожем/power loss).
    /// </summary>
    private void InstallCrashHandlers()
    {
        _sessionSentinelPath = Path.Combine(Logger.LogDirectory, "session.active");
        try
        {
            if (File.Exists(_sessionSentinelPath))
            {
                var prev = File.ReadAllText(_sessionSentinelPath).Trim();
                Logger.Warn($"Previous session ended WITHOUT clean-exit marker (abnormal shutdown/crash/kill). It was: {prev}", "App");
                WriteCrashLog($"[startup marker] previous session died without '=== exiting ===': {prev}");
            }
            File.WriteAllText(_sessionSentinelPath,
                $"started={DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId}");
        }
        catch (Exception ex) { Logger.Warn($"Session sentinel failed: {ex.Message}", "App"); }

        // Heartbeat + crash-last.txt + WER LocalDumps. Проверка следов прошлой сессии
        // идёт здесь — до тяжёлой инициализации, пока ещё можем умереть молча.
        _crashGuard = new CrashGuard(Logger);
        _crashGuard.CheckPreviousSession();
        _prevSessionAbnormal = _crashGuard.PreviousSessionAbnormal;
        _crashGuard.TryRegisterWerLocalDumps();

        // Managed-исключение на UI-потоке: логируем и НЕ глотаем (Handled=false —
        // пусть падает, но с записью в app.log).
        DispatcherUnhandledException += (s, e) =>
        {
            try { Logger.Error("FATAL: unhandled exception on UI thread", e.Exception, "Crash"); } catch { }
            WriteCrashLog($"FATAL unhandled UI exception:{Environment.NewLine}{e.Exception}");
            _crashGuard?.ReportManagedCrash("DispatcherUnhandledException", e.Exception, abnormalTermination: true);
            e.Handled = false;
        };

        // Не проглоченные исключения тасков: на .NET 4.5+ процесс всё равно не падает —
        // логируем, чтобы не потерять; SetObserved() не трогаем (он не спасает от падения,
        // а глотнуть исключение молча — хуже). Процесс живёт дальше → abnormalTermination=false.
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try { Logger.Error("Unobserved task exception", e.Exception, "Crash"); } catch { }
            _crashGuard?.ReportManagedCrash("UnobservedTaskException", e.Exception, abnormalTermination: false);
        };

        // Последний рубеж: фатал вне UI-потока. app.log может не успеть/не писаться —
        // пишем напрямую в crash.log, логгер — best-effort.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            WriteCrashLog($"FATAL unhandled domain exception (IsTerminating={e.IsTerminating}):{Environment.NewLine}{e.ExceptionObject}");
            try { Logger.Error("FATAL unhandled domain exception", e.ExceptionObject as Exception, "Crash"); } catch { }
            _crashGuard?.ReportManagedCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception, abnormalTermination: true);
        };

        // Выключение/разлогинивание Windows: помечаем в логе, что выход начался именно так.
        SessionEnding += (s, e) =>
        {
            try { Logger.Info($"=== exiting (via SessionEnding: {e.ReasonSessionEnding}) ===", "App"); } catch { }
        };
        // Exit приходит ПОСЛЕ OnExit: _shuttingDown уже true на штатном пути и это
        // не сработает. Сработало → дошли до выхода, минуя OnExit — аномалия.
        Exit += (s, e) =>
        {
            if (_shuttingDown) return;
            WriteCrashLog("Application.Exit fired WITHOUT OnExit path — abnormal shutdown");
            try { Logger.Warn("Exit without OnExit — abnormal shutdown path", "App"); } catch { }
        };
    }

    /// <summary>Прямая запись в logs/crash.log — последний артефакт, когда логгеру уже нельзя верить.</summary>
    private void WriteCrashLog(string message)
    {
        try
        {
            var dir = string.IsNullOrEmpty(AppDataDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrainstormBuddy", "logs")
                : Path.Combine(AppDataDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            // Страховка от бесконечного роста (краши редки, но файл один на всё время жизни).
            if (File.Exists(path) && new FileInfo(path).Length > 262_144) File.Delete(path);
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] pid={Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch { /* краш-лог пишем из последних сил — падать здесь нельзя */ }
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
                Notifier.ShowWarning(TT("L_Toast_Mic_Title", "Микрофон недоступен"),
                    TT("L_Toast_Mic_Body", "Не удалось запустить микрофон. Откройте настройки (Ctrl+Shift+S)."));
            }
            else if (device == "loopback")
            {
                Notifier.ShowInfo(TT("L_Toast_MicOnly_Title", "Только микрофон"),
                    TT("L_Toast_MicOnly_Body", "Loopback-устройство недоступно. Работаем только с микрофоном."));
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
            // Караоке-оверлей (телесуфлёр): создаётся вместе с главным оверлеем.
            // Click-through/стелс окно разруливает само по своему конфигу.
            _karaoke = new KaraokeWindow();
            _karaoke.Initialize(Config.Karaoke, WindowHelper.ApplyClickThrough, WindowHelper.ApplyExcludeFromCapture);
            if (Config.Karaoke.Enabled || Environment.GetEnvironmentVariable("BUDDY_KARAOKE_DEMO") == "1")
                _karaoke.Show();
            // Экран OCR: наблюдатель поднимается только при включённой опции; движки и
            // Tesseract прогреваются в фоне, поэтому старт оверлея не задерживается.
            if (Config.ScreenOcr.Enabled) StartScreenOcr();
            _overlay.Show();
            _overlay.SetActivePresets(SettingsVm.SystemPromptPresets, Config.Advanced.ActiveSystemPromptName);
            _overlay.RefreshMicMuteButton();
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
            // для «прогрева» НЕ надо: тексты рисует OverlayStatusResolver («Подготовка…»/«Готов…»).
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
                var k = HotkeyManager.Register(Config.Hotkeys.ToggleKaraoke, "karaoke");
                var x = HotkeyManager.Register(Config.Hotkeys.ToggleScreenOcr, "screenocr");
                var n = HotkeyManager.Register(Config.Hotkeys.ScreenOcrRecognizeNow, "screenocr_once");
                var tt = HotkeyManager.Register(Config.Hotkeys.ShowTrayIcon, "showtray");
                var am = HotkeyManager.Register(Config.Hotkeys.AskMemory, "askmemory");
                var mt = HotkeyManager.Register(Config.Hotkeys.MemoryToggle, "memtoggle");

                Logger.Info($"Hotkeys: toggle={t}, opacity={o}, settings={s}, logs={l}, pause={p}, llmpause={y}, screenshot={c}, karaoke={k}, screenocr={x}, ocrnow={n}, showtray={tt}, askmemory={am}, memtoggle={mt}", "UI");
                if (HotkeyManager.Ids.Count > 0)
                    Logger.Info("HotkeyMap: " + string.Join(' ', HotkeyManager.Ids.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")), "UI");
                if (!(t && o && s && l && p && y && c && k && x && n && tt && am && mt))
                    Notifier.ShowWarning(TT("L_Toast_Hotkeys_Title", "Горячие клавиши"),
                        TT("L_Toast_Hotkeys_Body", "Не удалось зарегистрировать некоторые горячие клавиши. Возможно, они заняты."));

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

    /// <summary>
    /// Предпросмотр караоке-оверлея из настроек (кнопка «Предпросмотр» → reflection).
    /// Создаёт окно, если оверлей ещё не показывался, и пушит три демо-строки,
    /// чтобы юзер сразу увидел ленту (PushLine фильтрует по Show*-флагам конфига).
    /// </summary>
    public void PreviewKaraoke()
    {
        if (_karaoke == null)
        {
            _karaoke = new KaraokeWindow();
            _karaoke.Initialize(Config.Karaoke, WindowHelper.ApplyClickThrough, WindowHelper.ApplyExcludeFromCapture);
        }
        _karaoke.Show();
        _karaoke.PushLine("question", TT("L_Kar_PrevQ", "Расскажите о вашем опыте работы с асинхронными системами?"));
        _karaoke.PushLine("answer", TT("L_Kar_PrevA1", "Упомяните очереди сообщений, паттерны retry/backoff и конкретный проект с Kafka/RabbitMQ."));
        _karaoke.PushLine("answer", TT("L_Kar_PrevA2", "Хорошо ляжет цифра: «обработка N сообщений/сек с гарантией at-least-once»."));
    }

    /// <summary>
    /// Хоткей «karaoke» (Ctrl+Shift+K): включает/выключает караоке-оверлей.
    /// Состояние сохраняется в конфиг (Enabled) — переживает перезапуск;
    /// чекбокс на вкладке «Караоке» обновляется через VM (биндинг к Config).
    /// </summary>
    public void ToggleKaraoke()
    {
        // Тумблим по факту видимости, а не по флагу: PreviewKaraoke()/demo могут
        // держать окно открытым при Enabled=false — иначе первое нажатие хоткея
        // шло по ветке «включить» (стирало строки, окно оставалось на месте).
        Config.Karaoke.Enabled = !(_karaoke?.IsShown == true);
        if (_karaoke == null)
        {
            _karaoke = new KaraokeWindow();
            _karaoke.Initialize(Config.Karaoke, WindowHelper.ApplyClickThrough, WindowHelper.ApplyExcludeFromCapture);
        }
        if (Config.Karaoke.Enabled)
        {
            _karaoke.ApplyConfig(Config.Karaoke);
            _karaoke.Clear(); // свежая сессия — строки, накопленные пока окно было скрыто, не показываем
            _karaoke.Show();
            Notifier.ShowInfo(TT("L_Toast_Karaoke_Title", "Караоке"), TT("L_Toast_Karaoke_On", "Телесуфлёр включён"));
            Logger.Info("Karaoke enabled via hotkey", "UI");
        }
        else
        {
            _karaoke.Hide();
            Notifier.ShowInfo(TT("L_Toast_Karaoke_Title", "Караоке"), TT("L_Toast_Karaoke_Off", "Телесуфлёр выключен"));
            Logger.Info("Karaoke disabled via hotkey", "UI");
        }
        SettingsVm.NotifyKaraokeEnabledChanged();
        SaveConfigSafe();
    }

    /// <summary>
    /// Хоткей «screenocr» (Ctrl+Shift+X): включает/выключает фичу «экран OCR»
    /// (шар-индикатор + хоткей «распознать сейчас»; авто-слежение — отдельный
    /// флаг ScreenOcr.AutoWatchEnabled). Состояние сохраняется в конфиг
    /// (ScreenOcr.Enabled) — переживает перезапуск; чекбокс на вкладке
    /// «Экран OCR» обновляется через VM (биндинг к Config).
    /// </summary>
    public void ToggleScreenOcr()
    {
        Config.ScreenOcr.Enabled = !Config.ScreenOcr.Enabled;
        if (Config.ScreenOcr.Enabled)
        {
            StartScreenOcr();
            Notifier.ShowInfo(TT("L_Toast_Ocr_Title", "Экран OCR"), TT("L_Toast_Ocr_On", "Распознавание экрана включено"));
            Logger.Info("ScreenOcr enabled via hotkey", "ScreenOcr");
        }
        else
        {
            StopScreenOcr();
            Notifier.ShowInfo(TT("L_Toast_Ocr_Title", "Экран OCR"), TT("L_Toast_Ocr_Off", "Распознавание экрана выключено"));
            Logger.Info("ScreenOcr disabled via hotkey", "ScreenOcr");
        }
        SettingsVm.NotifyScreenOcrEnabledChanged();
        SaveConfigSafe();
    }

    /// <summary>
    /// Движки OCR создаются один раз и переиспользуются наблюдателем и разовым распознаванием.
    /// Смена ScreenOcr.Engine в настройках пересобирает движок при следующем запуске наблюдателя.
    /// </summary>
    private void EnsureScreenOcrEngines()
    {
        _screenCapturer ??= new GdiScreenCapturer();

        // При смене движка старый освобождаем: прогрев и флаги начинают с чистого листа,
        // иначе _winOcrUnavailable от прошлой сессии глушил бы свежий выбор.
        if (_screenRecognizer != null && _screenRecognizer.Name != SelectedScreenOcrEngineName())
        {
            Logger.Info($"ScreenOcr: движок сменился на {SelectedScreenOcrEngineName()}, пересоздаю", "ScreenOcr");
            _screenRecognizer.Dispose();
            _screenRecognizer = null;
            _screenWarmupStarted = 0;
            _winOcrUnavailable = false;
        }

        _screenRecognizer ??= CreateScreenRecognizer();

        // Инициализация Tesseract синхронная и занимает около секунды: на UI-потоке это фриз
        // оверлея, поэтому прогрев уходит в фон; наблюдатель до готовности просто простаивает.
        if (Interlocked.Exchange(ref _screenWarmupStarted, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            var recognizer = await EnsureScreenRecognizerReadyAsync();
            if (recognizer != null)
                Logger.Info($"ScreenOcr: движок готов ({recognizer.Name})", "ScreenOcr");
        });
    }

    /// <summary>Имя движка из конфига с учётом отката на Tesseract (windows недоступен).</summary>
    private string SelectedScreenOcrEngineName()
    {
        var engine = Config.ScreenOcr.Engine?.Trim().ToLowerInvariant();
        return engine == "windows" && !_winOcrUnavailable ? "Windows OCR" : "Tesseract";
    }

    /// <summary>
    /// Собрать движок по ScreenOcr.Engine: "windows" → WinOcrRecognizer (WinRT, живёт в этом
    /// проекте — в Core нет WinRT-проекций), всё остальное → Tesseract. Оба идут через
    /// RecognizerFactory.Create(inner) — препроцессинг одинаков для обоих движков.
    /// </summary>
    private PreprocessingRecognizer CreateScreenRecognizer()
    {
        var engine = Config.ScreenOcr.Engine?.Trim().ToLowerInvariant();
        if (engine == "windows" && !_winOcrUnavailable)
        {
            Logger.Info("ScreenOcr: движок Windows OCR (WinRT)", "ScreenOcr");
            return RecognizerFactory.Create(new Ocr.WinOcrRecognizer(), trace: msg => Logger.Debug(msg, "ScreenOcr"));
        }

        if (engine is not ("tesseract" or "windows"))
            Logger.Warn($"ScreenOcr: неизвестный движок '{Config.ScreenOcr.Engine}' — использую Tesseract", "ScreenOcr");

        return RecognizerFactory.Create(trace: msg => Logger.Debug(msg, "ScreenOcr"));
    }

    /// <summary>
    /// Довести движок до рабочего состояния: InitializeAsync, а при недоступности Windows OCR —
    /// один откат на Tesseract за жизнь процесса. Возвращает null, если движок не поднялся.
    /// </summary>
    private async Task<PreprocessingRecognizer?> EnsureScreenRecognizerReadyAsync()
    {
        var recognizer = _screenRecognizer;
        if (recognizer is null) return null;

        try
        {
            if (recognizer.Availability != RecognizerAvailability.Available)
                await recognizer.InitializeAsync(RecognitionLanguages.RussianAndEnglish);

            if (recognizer.Availability == RecognizerAvailability.Available)
                return recognizer;

            // Windows OCR недоступен на этой машине: откатываемся на Tesseract один раз —
            // молча оставаться без OCR хуже, чем смена выбранного движка.
            if (!_winOcrUnavailable &&
                string.Equals(Config.ScreenOcr.Engine, "windows", StringComparison.OrdinalIgnoreCase))
            {
                _winOcrUnavailable = true;
                Logger.Warn($"ScreenOcr: Windows OCR недоступен ({recognizer.UnavailabilityReason}) — фолбэк на Tesseract", "ScreenOcr");

                recognizer.Dispose();
                _screenRecognizer = RecognizerFactory.Create(trace: msg => Logger.Debug(msg, "ScreenOcr"));
                recognizer = _screenRecognizer;
                await recognizer.InitializeAsync(RecognitionLanguages.RussianAndEnglish);

                if (recognizer.Availability == RecognizerAvailability.Available)
                    return recognizer;
            }

            Logger.Warn($"ScreenOcr: движок недоступен — {recognizer.UnavailabilityReason}", "ScreenOcr");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"ScreenOcr: инициализация движка не удалась — {ex.Message}", "ScreenOcr");
            return null;
        }
    }

    /// <summary>
    /// Включить фичу «экран OCR»: показать шар-индикатор и поднять наблюдателя,
    /// если включено авто-слежение (AutoWatchEnabled). Без авто-слежения фича живёт
    /// только на хоткее «распознать сейчас» — наблюдатель и движки не прогреваются.
    /// Повторный вызов при живом наблюдателе — no-op.
    /// </summary>
    private void StartScreenOcr()
    {
        SyncOcrIndicator();
        if (!Config.ScreenOcr.AutoWatchEnabled) return;

        EnsureScreenOcrEngines();
        if (_screenWatcher != null) return;

        _screenWatcher = new ScreenTextWatcher(_screenCapturer!, _screenRecognizer!, Config.ScreenOcr,
            log: msg => Logger.Info(msg, "ScreenOcr"),
            warn: msg => Logger.Warn(msg, "ScreenOcr"));
        // Подписка зависит от AutoAnswer: с автоответом кандидат идёт в LLM,
        // без — показывается пользователю как превью, чтобы наблюдение не выглядело мёртвым.
        if (Config.ScreenOcr.AutoAnswer)
            _screenWatcher.CandidateDetected += OnScreenCandidateDetected;
        else
            _screenWatcher.PreviewRequested += OnScreenPreviewRequested;
        _screenWatcherAutoAnswerMode = Config.ScreenOcr.AutoAnswer;
        _screenWatcher.Start();
        _overlay?.SetStatus(TT("L_Ocr_Watching", "OCR: слежу за экраном"));
    }

    /// <summary>Выключить фичу: остановить наблюдателя и скрыть шар-индикатор.</summary>
    private void StopScreenOcr()
    {
        _ocrIndicator?.Hide();

        var watcher = _screenWatcher;
        _screenWatcher = null;
        if (watcher == null) return;

        watcher.CandidateDetected -= OnScreenCandidateDetected;
        watcher.PreviewRequested -= OnScreenPreviewRequested;
        watcher.Dispose();
        _screenWatcherAutoAnswerMode = null;
    }

    /// <summary>
    /// Горячее применение конфига (сохранение настроек/хоткей): фича включена — шар виден
    /// (и наблюдатель, если включено авто-слежение), выключена — стоим. Пороги наблюдатель
    /// читает из живого объекта Config.ScreenOcr; смена движка, режима AutoAnswer или
    /// флага AutoWatch переподнимает наблюдателя.
    /// </summary>
    private void SyncScreenOcrWatcher()
    {
        var engineChanged = _screenRecognizer != null
            && _screenRecognizer.Name != SelectedScreenOcrEngineName();

        if (Config.ScreenOcr.Enabled)
        {
            // Подписка зависит от AutoAnswer — переподнимаем только при реальной смене режима.
            var modeChanged = _screenWatcher != null
                && _screenWatcherAutoAnswerMode != Config.ScreenOcr.AutoAnswer;
            var needWatcher = Config.ScreenOcr.AutoWatchEnabled;
            if ((_screenWatcher == null) == needWatcher || engineChanged || modeChanged)
            {
                // Шар переживает рестарт наблюдателя: Hide/Show идут в одном
                // проходе диспетчера, рендер-кадр между ними не успевает мигнуть.
                StopScreenOcr();
                StartScreenOcr();
            }
            else
            {
                SyncOcrIndicator(); // рестарт не нужен, но флаг ShowStatusIndicator мог смениться
            }
        }
        else
        {
            StopScreenOcr();
        }
    }

    /// <summary>
    /// Видимость шара по конфигу: показываем, когда фича включена и индикатор разрешён.
    /// Шар — сигнал «фича жива»: виден и без авто-слежения (состояние Idle),
    /// хоткей «распознать сейчас» работает независимо от наблюдателя.
    /// </summary>
    private void SyncOcrIndicator()
    {
        if (Config.ScreenOcr.Enabled && Config.ScreenOcr.ShowStatusIndicator)
        {
            _ocrIndicator ??= new OcrStatusIndicatorWindow();
            _ocrIndicator.Show();
        }
        else
        {
            _ocrIndicator?.Hide();
        }
    }

    /// <summary>Состояние шара из любого потока; тикающий таймер ошибки сбрасываем,
    /// иначе устаревший тик вернул бы шар в Idle посреди нового прохода.</summary>
    private void SetOcrState(OcrIndicatorState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _ocrErrorTimer?.Stop();
            _ocrIndicator?.SetState(state);
        });
    }

    /// <summary>Красный шар на ~3 с с возвратом в Idle (UI-поток; фоновые — через InvokeAsync).</summary>
    private void ShowOcrError()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowOcrError);
            return;
        }
        _ocrIndicator?.SetState(OcrIndicatorState.Error);
        if (_ocrErrorTimer == null)
        {
            _ocrErrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _ocrErrorTimer.Tick += (_, _) =>
            {
                _ocrErrorTimer.Stop();
                _ocrIndicator?.SetState(OcrIndicatorState.Idle);
            };
        }
        _ocrErrorTimer.Stop(); // повторная ошибка перезапускает отсчёт
        _ocrErrorTimer.Start();
    }

    /// <summary>
    /// Разовое распознавание экрана для настроек («Распознать сейчас») и одноимённого хоткея:
    /// снимок активного монитора → даунскейл до MaxOcrWidth → OCR → чистка.
    /// Выполняется в фоне, чтобы не морозить окно настроек. Пишет в журнал встреч
    /// как question с меткой [screen] — текст с экрана должен сохраняться (бэклог 2.8.x п.4).
    /// </summary>
    public Task<string> RecognizeScreenOnceAsync() => Task.Run(async () =>
    {
        EnsureScreenOcrEngines();
        var recognizer = await EnsureScreenRecognizerReadyAsync();

        if (recognizer is null || recognizer.Availability != RecognizerAvailability.Available)
            throw new InvalidOperationException(recognizer?.UnavailabilityReason ?? UiText.Tr("OCR недоступен", "OCR unavailable"));

        var sw = Stopwatch.StartNew();
        var region = MonitorEnumerator.CaptureBoundsFor(Config.ScreenOcr.MonitorIndex);
        var frame = _screenCapturer!.CaptureRegion(region);
        var captureMs = sw.Elapsed.TotalMilliseconds;

        var pixels = ImagePreprocessor.LimitWidth(frame.Pixels, Config.ScreenOcr.MaxOcrWidth);
        var result = await recognizer.RecognizeAsync(pixels, RecognitionOptions.Default);
        var totalMs = sw.Elapsed.TotalMilliseconds;
        var preprocessMs = totalMs - captureMs - result.Elapsed.TotalMilliseconds;

        // Тайминги этапов разового прохода — по ним видно, где теряется бюджет (п.7 бэклога).
        Logger.Info(
            $"ScreenOcr one-shot: {region.Width}×{region.Height} → {pixels.Width}×{pixels.Height}, " +
            $"захват {captureMs:F0} мс, препроц {preprocessMs:F0} мс, " +
            $"движок {result.Elapsed.TotalMilliseconds:F0} мс, итого {totalMs:F0} мс, " +
            $"{result.WordCount} слов ({result.EngineName})", "ScreenOcr");

        var text = ScreenTextWatcher.CleanupText(result.RawText);
        return text;
    });

    /// <summary>
    /// Хоткей «распознать сейчас» (по умолчанию Ctrl+Shift+N): принудительный одиночный
    /// проход с видимым фидбеком. Повторное нажатие во время прохода игнорируется —
    /// OCR ещё не завершён, второй проход только грузит CPU.
    /// </summary>
    public void RecognizeScreenNow()
    {
        // Флаг LLM читаем один раз: ветки ниже должны опираться на одно значение,
        // а не на живой конфиг (юзер мог его переключить, пока шёл OCR-проход).
        var llmDisabled = Config.Ui.LlmDisabled;
        if (Interlocked.Exchange(ref _screenRecognizeOnceActive, 1) != 0)
        {
            _overlay?.SetStatus(TT("L_Ocr_Busy", "OCR: распознавание уже идёт…"));
            return;
        }

        _overlay?.SetStatus(TT("L_Ocr_Recognizing", "OCR: распознаю экран…"));
        SetOcrState(OcrIndicatorState.Scanning);
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var text = await RecognizeScreenOnceAsync();
                var ocrMs = sw.Elapsed.TotalMilliseconds;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _overlay?.SetStatus(TT("L_Ocr_NoText", "OCR: текст на экране не найден"));
                        _ocrIndicator?.SetState(OcrIndicatorState.Idle);
                        return;
                    }

                    if (llmDisabled)
                    {
                        // Показываем распознанное как item транскрибата (LLM выкл) и журналируем
                        // с меткой [screen]. STT-времена null — STT тут не участвовал; длительность
                        // OCR выводим отдельной меткой «OCR Xs» под пузырём (бэклог 2.9.x).
                        _overlay?.AddSendingItem($"[screen] {text}", 0, DateTime.Now, null, null, 0,
                            transcriptOnly: true, ocrLatencyMs: ocrMs);
                        AppendJournalWithMemory("question", $"[screen] {text}");
                        _overlay?.SetStatus(string.Format(TT("L_Ocr_DoneLlmOffFmt", "OCR: распознано {0} симв. за {1:F1}s — LLM выключен"), text.Length, ocrMs / 1000));
                        _ocrIndicator?.SetState(OcrIndicatorState.Idle);
                    }
                    else
                    {
                        // LLM включён → тот же путь, что у авто-режима: пузырь вопроса,
                        // строка в караоке и запись в журнал — внутри SendScreenQuestion.
                        _overlay?.SetStatus(TT("L_Ocr_SendingQ", "OCR: вижу вопрос, отправляю…"));
                    }
                });

                if (!string.IsNullOrWhiteSpace(text) && !llmDisabled)
                    await SendScreenQuestion(text, ocrMs);
            }
            catch (Exception ex)
            {
                Logger.Warn($"ScreenOcr recognize-now failed: {ex.Message}", "ScreenOcr");
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.SetStatus(string.Format(TT("L_Ocr_ErrorFmt", "OCR: ошибка — {0}"), ex.Message));
                    ShowOcrError();
                });
            }
            finally
            {
                Interlocked.Exchange(ref _screenRecognizeOnceActive, 0);
            }
        });
    }

    /// <summary>
    /// Наблюдатель нашёл устойчивый кандидат при выключенном автоответе: показываем
    /// текст пользователю (оверлей) и пишем в журнал встреч с меткой [screen].
    /// </summary>
    private void OnScreenPreviewRequested(string text, double ocrMs)
    {
        Logger.Info($"ScreenOcr: превью кандидата ({text.Length} симв., OCR {ocrMs:F0} мс), автоответ выключен", "ScreenOcr");
        Dispatcher.InvokeAsync(() =>
        {
            // STT-времена null — реплика с экрана, не с микрофона; под пузырём «OCR Xs».
            _overlay?.AddSendingItem($"[screen] {text}", 0, DateTime.Now, null, null, 0,
                transcriptOnly: true, ocrLatencyMs: ocrMs);
            AppendJournalWithMemory("question", $"[screen] {text}");
            _overlay?.SetStatus(string.Format(TT("L_Ocr_PreviewFmt", "OCR: распознан текст ({0} симв., {1:F1}s), автоответ выключен"), text.Length, ocrMs / 1000));
            _ocrIndicator?.SetState(OcrIndicatorState.Idle);
        });
    }

    /// <summary>
    /// Наблюдатель нашёл на экране устойчивый новый вопрос. Фидбек: статус «вижу вопрос,
    /// отправляю…». В журнал — только метрики: текст пишет SendScreenQuestion через Append.
    /// </summary>
    private async Task OnScreenCandidateDetected(string text, double ocrMs)
    {
        Logger.Info($"ScreenOcr: вопрос на экране ({text.Length} симв., OCR {ocrMs:F0} мс)", "ScreenOcr");

        if (Config.Ui.LlmDisabled)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _overlay?.SetStatus(TT("L_Ocr_LlmOff", "OCR: вопрос найден, но LLM выключен"));
                _ocrIndicator?.SetState(OcrIndicatorState.Idle);
            });
            return;
        }

        await Dispatcher.InvokeAsync(() => _overlay?.SetStatus(TT("L_Ocr_SendingQ", "OCR: вижу вопрос, отправляю…")));
        SetOcrState(OcrIndicatorState.Sending);
        await SendScreenQuestion(text, ocrMs);
    }

    /// <summary>
    /// Распознанный с экрана вопрос уходит в LLM тем же путём, что и «секретное сообщение»:
    /// пузырь в оверлее + строка в караоке + запись в журнал встреч. Метка источника — [screen];
    /// под пузырём — «OCR Xs» (длительность распознавания), STT-полей нет.
    /// </summary>
    private async Task SendScreenQuestion(string text, double ocrMs)
    {
        var label = $"[screen] {text}";
        var systemPrompt = Config.Advanced.SystemPrompt +
            "\n[screen] = вопрос, распознанный с экрана пользователя (OCR). " +
            "Отвечай на него так же, как на вопрос собеседника. " +
            "Ответь структурой: первая строка — прямой короткий ответ " +
            "(например вариант «б» или одно предложение), затем одна строка «Зачем: …» " +
            "с кратким обоснованием в 1-2 предложения. Не пересказывай вопрос. " +
            "Отвечай СТРОГО на русском языке. " +
            "Если решил молчать — верни ПУСТОЙ ответ (без единого символа). НЕ пиши слова вроде (empty), «пустой ответ», «silent», скобок или иероглифов.";

        // Ответ пришиваем к СВОЕМУ пузырю (pair), а не к _sendingItem — последний
        // в это время может быть чужим пузырём голосового потока (перегрузки с QaPair
        // в OverlayWindow — тот же механизм, что и у answerWorker).
        QaPair? pair = null;
        await Dispatcher.InvokeAsync(() =>
        {
            pair = _overlay?.AddSendingItem(label, 0, DateTime.Now, null, null, 0, ocrLatencyMs: ocrMs);
            _overlay?.SetStatus(TT("L_Ocr_SendingQ", "OCR: вижу вопрос, отправляю…"));
            _ocrIndicator?.SetState(OcrIndicatorState.Sending);
            // Караоке: вопрос с экрана — реплика собеседника (роль question), без метки [screen].
            _karaoke?.PushLine("question", text);
            // Журнал пишет метку [screen]: источник с экрана отличим от реплик динамика (п.4).
            AppendJournalWithMemory("question", label);
        });

        try
        {
            List<ChatMessage> historySnapshot;
            lock (_historyLock) { historySnapshot = new List<ChatMessage>(_conversationHistory); }
            var stream = Config.Advanced.StreamLlmAnswers;
            var gotDelta = false;
            if (stream) _karaoke?.BeginStreamLine("answer");
            var sw = Stopwatch.StartNew();
            // Memory recall + правила директив памяти — общий билдер для всех LLM-путей.
            systemPrompt = await BuildSystemPromptWithMemoryAsync(systemPrompt, text, CancellationToken.None);
            var result = await ApiClient.AskAsync(label, systemPrompt,
                Config.Advanced.MaxResponseTokens, historySnapshot, CancellationToken.None,
                stream ? (Action<string>)(d =>
                {
                    gotDelta = true;
                    if (pair != null) _overlay?.AppendAnswerDelta(pair, d);
                    _karaoke?.AppendToStreamLine(d);
                }) : null);
            sw.Stop();
            // Memory-директивы в ответе: применить (зелёный тост) и вырезать из видимого
            // текста ДО финала стрим-строки — в стрим уже ушёл сырой текст с тегами.
            var ocrMem = await ProcessMemoryDirectivesAsync(result.Content);
            var screenAnswer = ocrMem.Clean;
            var ocrMemStripped = !string.Equals(screenAnswer, result.Content, StringComparison.Ordinal);
            // Маркер молчания («(empty)», «(空)»…) уже отстримился в ленту —
            // выкидываем стрим-строку целиком, иначе маркер висит на экране.
            // То же при стрипленых директивах: сырые [[MEMORY_*]] в ленте не оставляем.
            FinalizeKaraokeStreamLine(stream, gotDelta, result.IsSilent, ocrMemStripped, screenAnswer);
            Logger.Info($"ScreenOcr ask done in {sw.ElapsedMilliseconds}ms, {screenAnswer.Length} симв.", "Answer");

            if (!string.IsNullOrWhiteSpace(screenAnswer))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (pair != null) _overlay?.MarkAnswered(pair, screenAnswer, DateTime.Now, 0);
                    if (Config.ScreenOcr.AutoWatchEnabled) _overlay?.SetStatus(TT("L_Ocr_Watching", "OCR: слежу за экраном"));
                    else RefreshOverlayStatus();
                    _ocrIndicator?.SetState(OcrIndicatorState.Idle);
                    _overlay?.FlashStatus(TT("L_Ocr_Done", "OCR: готово"));
                    // Экранные ответы обходят AnswerWorkerLoop — пушим ответ в караоке здесь.
                    if (!gotDelta) _karaoke?.PushLine("answer", screenAnswer);
                });
                AppendJournalWithMemory("answer", screenAnswer);
                lock (_historyLock)
                {
                    _conversationHistory.Add(ChatMessage.User(label));
                    _conversationHistory.Add(ChatMessage.Assistant(screenAnswer));
                    TrimHistory(_conversationHistory, Config.Advanced.HistorySize);
                }
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    var why = result.Error
                        ?? ((result.IsSilent || ocrMemStripped)
                            ? TT("L_Err_ModelSilent", "Модель промолчала (пустой ответ). Попробуйте переформулировать или увеличить лимит токенов.")
                            : TT("L_Err_LlmEmpty", "пустой ответ от LLM"));
                    if (pair != null) _overlay?.MarkFailed(pair, why, DateTime.Now, 0);
                    ShowOcrError();
                    RefreshOverlayStatus();
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("SendScreenQuestion failed", ex, "UI");
            await Dispatcher.InvokeAsync(() =>
            {
                if (pair != null) _overlay?.MarkFailed(pair, ex.Message, DateTime.Now, 0);
                ShowOcrError();
            });
        }
    }

    /// <summary>
    /// Хоткей «showtray» (Ctrl+Shift+T): возвращает значок в трей, когда HideTrayIcon скрыл его.
    /// Сбрасывает флаг HideTrayIcon в конфиге — иконка остаётся видимой после рестарта;
    /// чекбокс в настройках обновляется через VM (биндинг к Config).
    /// </summary>
    public void ShowTrayIcon()
    {
        TrayIcon.Visible = true;
        if (Config.Ui.HideTrayIcon)
        {
            Config.Ui.HideTrayIcon = false;
            SettingsVm.NotifyHideTrayIconChanged();
            SaveConfigSafe();
        }
        Logger.Info("Tray icon restored via hotkey", "UI");
        Notifier.ShowInfo(TT("L_Toast_Tray_Title", "Трей"), TT("L_Toast_Tray_Body", "Значок BrainstormBuddy снова в трее"));
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
            Title = TT("L_Tour_Set_Stt_Title", "Движок распознавания речи"),
            Body = TT("L_Tour_Set_Stt_Body", "С чего всё начинается — распознавание речи (STT). Варианты: «Встроенный GigaAM» — быстро и офлайн, прямо на ПК; " +
                   "«Whisper» — точнее и с пунктуацией (лучше на видеокарте); «Локальный Docker» — свой контейнер; «Внешний сервер» — свой адрес. " +
                   "Для старта подойдёт встроенный GigaAM."),
            Target = () => s.SttEngineCombo
        },
        new CoachStep
        {
            Title = TT("L_Tour_Set_Speed_Title", "Проверить скорость"),
            Body = TT("L_Tour_Set_Speed_Body", "Кнопкой «Замерить скорость» проверишь, как быстро распознаётся речь на твоём железе (RTF меньше — лучше). " +
                   "Если сервер недоступен — покажет честно, а не будет висеть."),
            Target = () => s.SttSpeedTestBtn
        },
        new CoachStep
        {
            Title = TT("L_Tour_Set_Accel_Title", "Ускорение (CPU/GPU)"),
            Body = TT("L_Tour_Set_Accel_Body", "CPU работает у всех из коробки. GPU быстрее в разы, но только с дискретной видеокартой (NVIDIA/AMD); на встройке лучше CPU. " +
                   "Блок виден, когда выбран встроенный GigaAM."),
            Target = () => s.SttAccelCombo.IsVisible ? s.SttAccelCombo : null
        },
        new CoachStep
        {
            Title = TT("L_Tour_Set_Resume_Title", "Твоё резюме"),
            Body = TT("L_Tour_Set_Resume_Body", "Сюда вставь своё резюме — факты, проекты, цифры. Ассистент берёт данные строго отсюда и не выдумывает. " +
                   "Чем подробнее — тем точнее подсказки на собеседовании."),
            Target = () => s.ResumeSummaryBox
        },
        new CoachStep
        {
            Title = TT("L_Tour_Set_Engineer_Title", "Инженерный режим"),
            Body = TT("L_Tour_Set_Engineer_Body", "Выключен — главный экран чистый. Включи, если хочешь видеть на оверлее тонкие настройки: " +
                   "ползунки паузы/чанка, пресеты скорости речи и эквалайзер."),
            Target = () => s.EngineerModeCheck
        },
        new CoachStep
        {
            Title = TT("L_Tour_Set_Transcribe_Title", "Транскрибация файлов"),
            Body = TT("L_Tour_Set_Transcribe_Body", "Отсюда открывается окно распознавания готовых записей (mp4, webm и др.): " +
                   "транскрипт с тайм-кодами, AI-саммари, история и экспорт."),
            Target = () => s.OpenFileTranscriptionBtn
        },
        new CoachStep
        {
            Title = TT("L_Tour_Set_Stream_Title", "Стриминг ответа"),
            Body = TT("L_Tour_Set_Stream_Body", "Ответ ИИ печатается побуквенно, прямо по мере генерации — видно, что ассистент уже отвечает. " +
                   "По умолчанию выключено: часть шлюзов в режиме стрима отвечает заметно медленнее."),
            Target = () => s.StreamLlmCheck
        },
        new CoachStep
        {
            Title = TT("L_Tour_Set_Karaoke_Title", "Караоке-лента"),
            Body = TT("L_Tour_Set_Karaoke_Body", "Полоса-телесуфлёр поверх всех окон: вопросы и подсказки крупно и всегда на виду. " +
                   "Включается чекбоксом на этой вкладке или горячей клавишей Ctrl+Shift+K; " +
                   "цвета, шрифт и положение полосы настраиваются здесь же. Кнопкой «Предпросмотр» посмотришь ленту вживую."),
            Target = () => s.KaraokePreviewButton
        },
        new CoachStep
        {
            Title = TT("L_Tour_Set_Save_Title", "Сохрани изменения"),
            Body = TT("L_Tour_Set_Save_Body", "Поменял настройки — жми «Сохранить». Это обучение всегда можно открыть заново: " +
                   "кнопка «Запустить обучение» внизу слева. Дальше покажу главное окно."),
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
            Title = TT("L_Tour_Ov_Hello_Title", "Это твой ассистент"),
            Body = TT("L_Tour_Ov_Hello_Body", "Главное окно — оверлей. Слева реплики собеседника, справа готовые ответы ИИ. " +
                   "Окно можно двигать за шапку и тянуть за края."),
            Target = () => null
        },
        new CoachStep
        {
            Title = TT("L_Tour_Ov_Screenshot_Title", "Фото-режим: скрыть от записи"),
            Body = TT("L_Tour_Ov_Screenshot_Body", "Эта кнопка прячет оверлей от записи и демонстрации экрана: ты видишь подсказки, а на созвоне и скриншоте собеседника их нет. " +
                   "Нажми ещё раз — снова показать (например, чтобы самому заскринить). Горячая клавиша Ctrl+Shift+C."),
            Target = () => o.ScreenShotButton
        },
        new CoachStep
        {
            Title = TT("L_Tour_Ov_Mic_Title", "Микрофон вкл/выкл"),
            Body = TT("L_Tour_Ov_Mic_Body", "Глушит только ТВОЙ микрофон — собеседника слышно и транскрибируем дальше. Полная пауза всего звука — горячая клавиша Ctrl+Shift+P. Рядом живая волна показывает уровень."),
            Target = () => o.MicMuteButton
        },
        new CoachStep
        {
            Title = TT("L_Tour_Ov_Send_Title", "Отправить сейчас"),
            Body = TT("L_Tour_Ov_Send_Body", "Молния отправляет реплику в ИИ немедленно, не дожидаясь паузы. Готово — удачи на собеседовании!"),
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

    // Окно истории встреч (краш-безопасные live-журналы meetings/*.jsonl) —
    // single-instance, как ShowFileTranscription. Вызывается из трея и сайдбара настроек.
    public void ShowMeetingHistory()
    {
        try
        {
            if (_meetingHistoryWindow != null)
            {
                if (_meetingHistoryWindow.WindowState == WindowState.Minimized)
                    _meetingHistoryWindow.WindowState = WindowState.Normal;
                _meetingHistoryWindow.Show();
                _meetingHistoryWindow.Activate();
                return;
            }
            _meetingHistoryWindow = new MeetingHistoryWindow();
            _meetingHistoryWindow.Closed += (s, e) => _meetingHistoryWindow = null;
            _meetingHistoryWindow.Show();
        }
        catch (Exception ex) { Logger?.Error("ShowMeetingHistory failed", ex, "UI"); }
    }

    // Встроенный (native) STT-движок для транскрибации файлов. Переиспользует уже
    // загруженный App.SttEngine, если он native (без повторной загрузки модели ~900МБ);
    // иначе поднимает свой (owns=true → вызывающий обязан Dispose). MelFrontend и
    // ONNX-сессия потокобезопасны, поэтому совместное использование с живым STT безопасно.
    public BrainstormBuddy.Stt.IGigaamSttEngine? ResolveNativeSttEngine(out string? error, out bool owns)
    {
        error = null;
        owns = false;
        if (SttEngine is BrainstormBuddy.Stt.IGigaamSttEngine nat) return nat;
        try
        {
            var gig = CreateGigaamEngineForVersion(Config.Audio.SttEngine, BrainstormBuddy.Stt.SttAccel.Cpu, 0, out var merr);
            if (gig == null)
            {
                error = merr ?? UiText.Tr("Модель GigaAM не найдена. Скачайте её в настройках → Локальный STT, либо переустановите полную версию.", "GigaAM model not found. Download it in Settings → Local STT, or reinstall the full version.");
                return null;
            }
            owns = true;
            return gig;
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
    // Ключ: модель+язык+accel+Vulkan-устройство — смена карты/режима пересоздаёт движок.
    public BrainstormBuddy.Stt.WhisperSttEngine? GetWhisperEngine(out string? error)
    {
        error = null;
        var model = ResolveWhisperModel();
        if (model == null)
        {
            error = UiText.Tr("Модель Whisper (turbo) не скачана. Скачайте её в Настройки → Локальный STT.", "Whisper model (turbo) not downloaded. Download it in Settings → Local STT.");
            return null;
        }
        // auto: если в системе ЕСТЬ GPU (любой) → Vulkan; иначе CPU. Дискретную предпочитаем.
        string accel = (Config.Audio.WhisperAccel ?? "auto").ToLowerInvariant();
        var gpus = BrainstormBuddy.Native.GpuEnumerator.List();
        if (accel == "auto") accel = gpus.Count > 0 ? "gpu" : "cpu";
        // Выбор карты: явный WhisperGpuDevice (>=0) из настроек, иначе дискретная/первая (старое).
        int wmiDev = -1;
        if (accel == "gpu")
        {
            int disc = BrainstormBuddy.Native.GpuEnumerator.BestDiscreteIndex();
            wmiDev = Config.Audio.WhisperGpuDevice >= 0
                ? Config.Audio.WhisperGpuDevice
                : (disc >= 0 ? disc : (gpus.Count > 0 ? gpus[0].Index : 0));
        }
        // WhisperFactoryOptions.GpuDevice ждёт Vulkan-индекс (vkEnumeratePhysicalDevices), а не DXGI/WMI.
        int gpuDev = wmiDev >= 0 ? WmiGpuToVulkanIndex(wmiDev, gpus) : -1;
        var key = $"{model}|{Config.Audio.WhisperLanguage}|{accel}|{gpuDev}";
        if (_whisperEngine != null && _whisperEngineKey == key) return _whisperEngine;
        try
        {
            var fresh = new BrainstormBuddy.Stt.WhisperSttEngine(model, Config.Audio.WhisperLanguage, accel, gpuDev);
            try { _whisperEngine?.Dispose(); } catch { /* уже освобождён */ }
            _whisperEngine = fresh;
            _whisperEngineKey = key;
            Logger.Info($"Whisper ready: {model} (accel={accel}, wmiDevice={wmiDev}, vkDevice={gpuDev})", "Ai");
            return _whisperEngine;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    // DXGI/WMI-индекс (GpuEnumerator) → Vulkan-индекс (vkEnumeratePhysicalDevices, whisper.cpp).
    // Матч по имени адаптера; не сошлось — passthrough индекса (best-effort).
    private static int WmiGpuToVulkanIndex(int wmiIndex, List<BrainstormBuddy.Native.GpuEnumerator.GpuInfo>? gpus = null)
    {
        try
        {
            gpus ??= BrainstormBuddy.Native.GpuEnumerator.List();
            var name = wmiIndex >= 0 ? gpus.FirstOrDefault(g => g.Index == wmiIndex)?.Name : null;
            return BrainstormBuddy.Stt.VulkanDeviceEnumerator.MapSystemIndex(wmiIndex, name);
        }
        catch { return wmiIndex; }
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
        _whisperEngineKey = "";
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
                if (wm == null) { error = UiText.Tr("Модель Whisper (turbo) не скачана. Скачайте её в Настройки → Локальный STT.", "Whisper model (turbo) not downloaded. Download it in Settings → Local STT."); return null; }
                try
                {
                    // Whisper (Vulkan): DXGI-индекс из окна маппим на Vulkan-индекс.
                    int dev = fileAccel == "gpu" ? WmiGpuToVulkanIndex(ResolveFileGpuIndex()) : -1;
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
                var accel = fileAccel == "gpu" ? BrainstormBuddy.Stt.SttAccel.DirectML : BrainstormBuddy.Stt.SttAccel.Cpu;
                int dev = fileAccel == "gpu" ? ResolveFileGpuIndex() : 0;
                var owned = CreateGigaamEngineForVersion(Config.Audio.SttEngine, accel, dev, out var merr);
                if (owned == null)
                {
                    error = merr ?? UiText.Tr("Модель GigaAM не найдена. Скачайте её в настройках → Локальный STT, либо переустановите полную версию.", "GigaAM model not found. Download it in Settings → Local STT, or reinstall the full version.");
                    return null;
                }
                Logger.Info($"File GigaAM {owned.VersionTag}: явное железо accel={fileAccel}, device={dev}, provider={owned.ActiveProvider}", "Ai");
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

    // Причина, по которой движок STT ушёл в фолбэк (например, не найдена локальная
    // GigaAM/Whisper модель → поднялся remote). Используется в UpdateConnectionNotice,
    // чтобы баннер показывал коренную причину, а не только последнюю ошибку сервера.
    private string? _sttFallbackReason;

    // Сигнатура текущего STT-движка — чтобы пересоздавать его только при реальной смене настроек.
    private string _sttEngineKey = "";
    private string ComputeSttEngineKey() =>
        $"{Config.Audio.SttEngine}|{Config.Audio.SttAccel}|{Config.Audio.SttGpuDevice}|{Config.Audio.WhisperAccel}|{Config.Audio.WhisperGpuDevice}|{Config.Audio.WhisperLanguage}|{Config.LocalStt.Enabled}";

    // Горячая замена движка распознавания при смене настроек — БЕЗ перезапуска приложения.
    // Модель грузится в фоне; воркеры читают App.SttEngine на каждом чанке и подхватят новый.
    public void RecreateSttEngineIfChanged()
    {
        var newKey = ComputeSttEngineKey();
        if (newKey == _sttEngineKey) return;
        _sttEngineKey = newKey;
        Logger.Info($"STT-настройки изменились → пересоздаю движок ({newKey})", "Ai");
        Dispatcher.InvokeAsync(() => Notifier?.ShowInfo(UiText.Tr("Переключаю распознавание", "Switching recognition"), UiText.Tr("загружаю и прогреваю движок…", "loading and warming up the engine…")));
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
                    else Notifier?.ShowInfo(UiText.Tr("Распознавание готово", "Recognition ready"), fresh.Name);
                });
            }
            catch (Exception ex) { Logger.Error("STT hot-swap failed", ex, "Ai"); }
        });
    }

    // Сигнатура настроек диаризации — пересоздаём diarizer только при реальной смене
    // (Enabled/бэкенд/путь модели/пресет — всё, что читает NemotronDiarizer.TryCreate).
    private string _speakerDiarizerKey = "";
    private string ComputeSpeakerDiarizerKey()
    {
        var c = Config.SpeakerDiarization;
        return $"{c.Enabled}|{c.NemotronBackend}|{c.NemotronModelPath}|{c.NemotronPreset}";
    }

    // Подписка Nemotron-стрима на реальный поток сэмплов loopback-буфера. baseline фиксируем
    // на ПЕРВОМ фрейме — это сэмплы, ушедшие в буфер до старта стрима: Core считает индексы
    // кумулятивно от attach, а ChunkInfo живёт в абсолютной шкале буфера.
    private void AttachLoopbackFeed(IStreamingSpeakerDiarizer sd)
    {
        var rate = Config.Audio.SampleRate;
        _diarFeedBaseline = -1;
        _loopbackFeedHandler = samples =>
        {
            // Снимок ДО push: к этому моменту AddSamples уже отработал, значит PushedSamples
            // включает этот фрейм → baseline = позиция начала фрейма в абсолютной шкале.
            Interlocked.CompareExchange(
                ref _diarFeedBaseline, AudioBuffer.PushedSamples - samples.Length, -1);
            sd.PushLoopback(samples, rate);
        };
        AudioEngine.LoopbackSamples += _loopbackFeedHandler;
    }

    private void DetachLoopbackFeed()
    {
        var h = _loopbackFeedHandler;
        if (h == null) return;
        _loopbackFeedHandler = null;
        if (AudioEngine != null)
            AudioEngine.LoopbackSamples -= h;
    }

    // Создание/пересоздание/выключение diarizer по ключу настроек — вызывается из OnStartup
    // и из ApplyLiveConfigChanges. Модель грузится в фоне; воркер читает поле на каждом
    // loopback-чанке и подхватывает/теряет diarizer атомарной заменой ссылки.
    private void SyncSpeakerDiarizer()
    {
        var key = ComputeSpeakerDiarizerKey();
        if (key == _speakerDiarizerKey) return;
        _speakerDiarizerKey = key;
        var cfg = Config.SpeakerDiarization;
        // Снимок значений в новый объект: фоновая задача не должна читать живой конфиг
        // (Save настроек может заменить объект, пока diarizer ещё грузится).
        var snapshot = new SpeakerDiarizationConfig
        {
            Enabled = cfg.Enabled,
            NemotronBackend = cfg.NemotronBackend,
            NemotronModelPath = cfg.NemotronModelPath,
            NemotronPreset = cfg.NemotronPreset,
        };
        // Fast-path: выключено и выключать нечего — вообще ничего не делаем (дефолт Enabled=false
        // не должен стоить даже одного Task.Run на старте/сохранении настроек).
        if (!snapshot.Enabled && _speakerDiarizer == null) return;
        _ = Task.Run(() =>
        {
            // Выкл/смена → старый отпускаем сразу; при выключении новый не создаётся.
            // Подписку стрима снимаем ДО dispose: пока handler висит, в fed-поток течёт
            // аудио буфера, и baseline продолжал бы уезжать от нуля.
            var old = _speakerDiarizer;
            _speakerDiarizer = null;
            if (!snapshot.Enabled)
            {
                DetachLoopbackFeed();
                old?.Dispose();
                _lastLoopbackSpeakerId = null;
                _lastLoopbackSpeakerEpoch = -1;
                return;
            }
            // Nemotron резолвит свой нативный бандл (nemo_speech_asr_c.dll) и GGUF сам —
            // TryCreate вернёт null с WARN, если dll/модель не найдены или бэкенд не поднялся.
            IStreamingSpeakerDiarizer? fresh = NemotronDiarizer.TryCreate(snapshot, Logger);
            // Гонка с более поздним Sync (двойной Save настроек): ключ уже сменился —
            // этот экземпляр устарел, не записываем его в поле.
            if (key != _speakerDiarizerKey)
            {
                fresh?.Dispose();
                old?.Dispose();
                return;
            }
            _speakerDiarizer = fresh; // TryCreate=null (нет модели/dll) → фича тихо выкл
            if (fresh != null)
            {
                // Swap-подписка: старый стрим продолжал бы есть фреймы и путать шкалу
                // нового attach, поэтому снимаем его handler до подключения свежего.
                DetachLoopbackFeed();
                if (fresh.IsReady)
                    AttachLoopbackFeed(fresh);
                // Эффективный бэкенд после подключения: «auto» мог уйти на cpu —
                // в логе должен быть реальный выбор, не только запрошенный.
                Logger.Info($"Speaker diarization backend: requested={snapshot.NemotronBackend}, active={NemotronDiarizer.LastActiveBackend ?? "?"}", "Speaker");
                Logger.Info($"Speaker diarization enabled (key={key})", "Speaker");
            }
            old?.Dispose();
        });
    }

    // Переводит абсолютные индексы ChunkInfo (шкала AudioBuffer) в шкалу diar-стрима
    // (0 = первый fed-фрейм). Без baseline (ни одного фрейма ещё не было) индексы уже
    // совпадают со стримом — возвращаем как есть. Смещение запекается в ChunkItem при
    // enqueue — воркер потом работает с item.Info без привязки к жизни подписки.
    private ChunkInfo ToDiarStreamInfo(ChunkInfo info)
    {
        var b = Interlocked.Read(ref _diarFeedBaseline);
        if (b <= 0) return info;
        return info with
        {
            OnsetSample = Math.Max(0, info.OnsetSample - b),
            SpeechEndSample = Math.Max(0, info.SpeechEndSample - b),
            EmitSample = Math.Max(0, info.EmitSample - b),
            // База WAV тоже сдвигается в шкалу diar-стрима; -1 (нет базы)
            // остаётся -1, чтобы TryBuildSpeakerMarkup корректно ушёл в фолбэк.
            WavStartSample = info.WavStartSample < 0 ? -1 : Math.Max(0, info.WavStartSample - b),
        };
    }

    /// <summary>Строка начинается с метки спикера вида «[S1]», «[S12]» — но
    /// НЕ «[Динамик]»/«[музыка]». Проверка первых трёх символов: '[', 'S', цифра.</summary>
    private static bool HasLeadingSpeakerTag(string text)
        => text.Length > 3 && text[0] == '[' && text[1] == 'S' && char.IsDigit(text[2]);

    // Внутричанковая разметка «[S1] … [S2] …»: раскладка текста чанка по
    // хронологическим спанам IdentifyRange. Спаны приходят в шкале diar-стрима,
    // wav — свой файл: всё приводится к секундам от начала wav (база =
    // info.WavStartSample в той же шкале; -1 → аудио-сплит недоступен).
    // Точность по убыванию: пословные CTC-таймкоды → аудио-сплит по спану +
    // STT каждого куска → грубая пропорция по длительности. null — разметку не
    // строим (моно-спикер, нет спанов/базы, срезы пустые): воркер оставляет
    // обычную метку доминанта. Не бросает.
    private async Task<string?> TryBuildSpeakerMarkup(
        IReadOnlyList<SpeakerTurnSpan> rawSpans, ChunkInfo info,
        IReadOnlyList<BrainstormBuddy.Stt.SttWordSpan>? timedWords,
        byte[] wav, string source, int sampleRate, string cleanText, CancellationToken ct)
    {
        try
        {
            if (rawSpans.Count < 2)
                return null; // один поворот — доминирующей метки хватает
            var wavBase = info.WavStartSample;
            // Без базы wav шкалу привязываем к первому спану (≈ onset речи;
            // сдвиг на длину preRoll ~300мс — локальная ошибка у границ, но
            // разметка остаётся возможной и без ChunkInfo.WavStartSample).
            var relBase = wavBase >= 0 ? wavBase : rawSpans.Min(s => s.StartSample);
            var timed = new List<(string SpeakerId, double StartSec, double EndSec)>(rawSpans.Count);
            foreach (var s in rawSpans.OrderBy(s => s.StartSample))
            {
                var a = (s.StartSample - relBase) / (double)sampleRate;
                var b = (s.EndSample - relBase) / (double)sampleRate;
                timed.Add((s.SpeakerId, Math.Max(0, a), Math.Max(0, b)));
            }

            var runs = timedWords is { Count: > 0 }
                ? SpeakerMarkupAssembler.Assign(timedWords, timed)
                : null;
            if (runs != null && runs.Count > 1)
                return SpeakerMarkupAssembler.Render(runs);

            // Аудио-сплит: wav по спанам + STT кусков (точная граница без таймкодов).
            if (wavBase >= 0 && source == "loopback")
            {
                var pieces = new List<(string SpeakerId, string Text)>(timed.Count);
                var engine = SttEngine;
                foreach (var (sid, a, b) in timed)
                {
                    var slice = SpeakerMarkupAssembler.SlicePcm16Wav(wav, a, b);
                    if (slice == null)
                        continue;
                    var t = await engine.TranscribeAsync(slice, ct);
                    if (!string.IsNullOrWhiteSpace(t))
                        pieces.Add((sid, t.Trim()));
                }
                var sliced = SpeakerMarkupAssembler.Render(pieces);
                if (sliced != null)
                    return sliced;
            }

            // Грубый фолбэк (помечено в коде): слова чистого текста делим по
            // спанам пропорционально длительности спана. Применяется, когда нет
            // ни таймкодов, ни результата аудио-сплита (мик-канал, ошибки STT).
            var rough = SpeakerMarkupAssembler.AssignProportional(cleanText, timed);
            return SpeakerMarkupAssembler.Render(rough);
        }
        catch (Exception ex)
        {
            Logger.Warn($"speaker markup failed: {ex.Message}", "Speaker");
            return null;
        }
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
            Dispatcher.InvokeAsync(ShowSttWarmupStatus); // статус «Подготовка распознавания…», если оверлей уже есть
            var t0 = DateTime.UtcNow;
            engine.TranscribeAsync(BuildWarmupWav()).GetAwaiter().GetResult();
            Logger.Info($"STT warmup [{engine.Name}] done in {(DateTime.UtcNow - t0).TotalMilliseconds:F0}ms", "Ai");
        }
        catch (Exception ex) { Logger.Warn($"STT warmup failed: {ex.Message}", "Ai"); }
        finally
        {
            _sttWarmedUp = true;
            Dispatcher.InvokeAsync(ShowSttWarmupStatus); // статус «Готов к распознаванию»
        }
    }

    // Видимая индикация прогрева STT: теперь просто пересчёт резолвленного статуса —
    // «Подготовка распознавания…»/«Готов к распознаванию» рисует OverlayStatusResolver.
    private void ShowSttWarmupStatus() => RefreshOverlayStatus();

    /// <summary>
    /// Единая пересборка статуса оверлея (строка StatusText + плашка ColdStartHint):
    /// состояние → OverlayStatusResolver → ApplyStatus. Вызывать при любом изменении
    /// IsPaused/IsLlmPaused/_sttOk/_sttWarmedUp/IsTranscribing — из любого потока.
    /// </summary>
    public void RefreshOverlayStatus()
    {
        var input = new BrainstormBuddy.Services.OverlayStatusInput(
            IsPaused, IsLlmPaused, _sttOk, _sttErrMsg, _sttWarmedUp, IsTranscribing, _sttHttpStatus, _sttErrKind);
        // T(key) возвращает сам ключ, если ресурса нет — тогда берём RU-фолбэк из резолвера.
        var status = BrainstormBuddy.Services.OverlayStatusResolver.Resolve(input,
            (k, fb) => T(k) is { } s && s != k && !s.StartsWith("[") ? s : fb);
        try { Dispatcher.InvokeAsync(() => _overlay?.ApplyStatus(status)); }
        catch { /* приложение уже уходит — статус некому показывать */ }
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
        if (string.IsNullOrWhiteSpace(eng)) eng = "native3"; // пусто → встроенный GigaAM v3, НЕ remote

        // Явно выбран Whisper: Whisper → (фолбэк) встроенный GigaAM → remote.
        if (eng == "whisper")
        {
            var w = GetWhisperEngine(out var werr);
            if (w != null) { _sttFallbackReason = null; Logger.Info("STT engine: Whisper", "Ai"); return w; }
            Logger.Warn($"Whisper недоступен ({werr}) → пробую встроенный GigaAM", "Ai");
            var fallbackGig = TryCreateNativeGigaam(out _);
            if (fallbackGig != null)
            {
                _pendingSttNotice = (UiText.Tr("Whisper не готов", "Whisper not ready"),
                    UiText.Tr("Выбран Whisper, но модель не скачана — работаю на встроенном GigaAM. Скачайте Whisper в Настройки → Распознавание речи (STT).", "Whisper is selected but the model is not downloaded — falling back to built-in GigaAM. Download Whisper in Settings → Speech Recognition (STT)."));
                Logger.Info("STT engine: native (фолбэк с Whisper)", "Ai");
                return fallbackGig;
            }
            _sttFallbackReason = UiText.Tr("не найдены локальные модели (Whisper и GigaAM)", "local models not found (Whisper and GigaAM)");
            _pendingSttNotice = (UiText.Tr("Модель распознавания не скачана", "Recognition model not downloaded"),
                UiText.Tr("Встроенные движки недоступны — временно работаю через внешний сервер. Скачайте модель в Настройки → Распознавание речи (STT).", "Built-in engines are unavailable — temporarily using the external server. Download a model in Settings → Speech Recognition (STT)."));
            return new BrainstormBuddy.Ai.RemoteSttService(ApiClient);
        }

        // Встроенный GigaAM (по умолчанию): v3 ("native3") или v2 ("native") → Whisper → remote.
        // ВАЖНО: никогда не проваливаемся молча в remote, если локальная модель есть.
        if (eng == "native" || eng == "native3")
        {
            var gig = TryCreateNativeGigaam(out var gerr);
            if (gig != null) { _sttFallbackReason = null; return gig; }

            Logger.Warn($"Встроенный GigaAM недоступен ({gerr}) → пробую Whisper", "Ai");
            var w = GetWhisperEngine(out _);
            if (w != null)
            {
                _pendingSttNotice = (UiText.Tr("Встроенный GigaAM не готов", "Built-in GigaAM not ready"),
                    UiText.Tr("Использую Whisper. Проверьте модель GigaAM в Настройки → Распознавание речи (STT).", "Using Whisper. Check the GigaAM model in Settings → Speech Recognition (STT)."));
                Logger.Info("STT engine: Whisper (фолбэк с GigaAM)", "Ai");
                return w;
            }
            _sttFallbackReason = UiText.Tr("не найдены локальные модели", "local models not found") + $" ({gerr})";
            _pendingSttNotice = (UiText.Tr("Модель распознавания не скачана", "Recognition model not downloaded"),
                UiText.Tr("Встроенные движки недоступны — временно работаю через внешний сервер. Скачайте модель в Настройки → Распознавание речи (STT).", "Built-in engines are unavailable — temporarily using the external server. Download a model in Settings → Speech Recognition (STT)."));
            return new BrainstormBuddy.Ai.RemoteSttService(ApiClient);
        }

        // Явно выбран внешний сервер (remote / Docker / LAN).
        // Если сервер отвечает — используем его. Если молчит (напр. Docker не запущен),
        // а локальная модель есть — падаем на встроенный движок, чтобы STT не был мёртв.
        if (IsRemoteSttReachable())
        {
            Logger.Info("STT engine: remote (сервер доступен)", "Ai");
            _sttFallbackReason = null; // явный выбор remote — без фолбэк-префикса
            return new BrainstormBuddy.Ai.RemoteSttService(ApiClient);
        }
        Logger.Warn("STT: внешний сервер не отвечает → пробую встроенные движки", "Ai");
        var gigRemoteFb = TryCreateNativeGigaam(out _);
        if (gigRemoteFb != null)
        {
            _pendingSttNotice = (UiText.Tr("Внешний сервер распознавания недоступен", "External recognition server unavailable"),
                UiText.Tr("Сервер STT не отвечает — работаю на встроенном GigaAM. Проверьте Docker/адрес в Настройки → Распознавание речи (STT).", "The STT server is not responding — using built-in GigaAM. Check Docker/address in Settings → Speech Recognition (STT)."));
            Logger.Info("STT engine: native (фолбэк с remote)", "Ai");
            return gigRemoteFb;
        }
        var wRemoteFb = GetWhisperEngine(out _);
        if (wRemoteFb != null)
        {
            _pendingSttNotice = (UiText.Tr("Внешний сервер распознавания недоступен", "External recognition server unavailable"),
                UiText.Tr("Сервер STT не отвечает — работаю на Whisper. Проверьте Docker/адрес в Настройки → Распознавание речи (STT).", "The STT server is not responding — using Whisper. Check Docker/address in Settings → Speech Recognition (STT)."));
            Logger.Info("STT engine: Whisper (фолбэк с remote)", "Ai");
            return wRemoteFb;
        }
        Logger.Info("STT engine: remote (встроенных движков нет)", "Ai");
        _sttFallbackReason = UiText.Tr("внешний сервер не отвечает и локальные модели не найдены", "external server unreachable and no local models found");
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
    private BrainstormBuddy.Stt.IGigaamSttEngine? TryCreateNativeGigaam(out string? error)
    {
        error = null;
        try
        {
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

            var svc = CreateGigaamEngineForVersion(Config.Audio.SttEngine, wantAccel, gpuDevice, out var merr);
            if (svc == null)
            {
                error = merr ?? UiText.Tr("модель GigaAM ONNX не найдена", "GigaAM ONNX model not found");
                Logger.Warn($"Native STT: {error}", "Ai");
                return null;
            }

            // GPU запрошен, но не поднялся (нет DX12/драйвера) → работаем на CPU, предупреждаем
            if (wantAccel == BrainstormBuddy.Stt.SttAccel.DirectML && !svc.ActiveProvider.StartsWith("DirectML"))
                _pendingSttNotice = (UiText.Tr("GPU-ускорение недоступно", "GPU acceleration unavailable"),
                    UiText.Tr("Запрошен DirectML, но GPU не поднялся — распознавание работает на CPU (медленнее). Проверьте драйвер видеокарты или выберите CPU в настройках.", "DirectML was requested, but the GPU did not come up — recognition runs on CPU (slower). Check the GPU driver or select CPU in settings."));
            return svc;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Error("Native STT init failed", ex, "Ai");
            return null;
        }
    }

    // Создаёт встроенный GigaAM выбранной версии. "native3" (и любой id кроме явного "native"
    // — напр. файловая транскрибация при движке whisper) → сначала v3; если файлов v3 нет —
    // WARN и откат на v2. Явный "native" → v2. null + error, если не найдена ни одна модель.
    private BrainstormBuddy.Stt.IGigaamSttEngine? CreateGigaamEngineForVersion(
        string? engineId, BrainstormBuddy.Stt.SttAccel accel, int gpuDevice, out string? error)
    {
        error = null;
        bool preferV3 = !string.Equals(engineId, "native", StringComparison.OrdinalIgnoreCase);
        if (preferV3)
        {
            var (v3onnx, v3vocab) = ResolveGigaamV3Model();
            if (v3onnx != null && v3vocab != null)
            {
                var v3 = new BrainstormBuddy.Stt.NativeGigaamV3SttService(v3onnx, v3vocab, accel, gpuDevice, logger: Logger);
                Logger.Info($"Native STT ready (v3): {v3onnx} (provider={v3.ActiveProvider})", "Ai");
                return v3;
            }
            Logger.Warn("Модель GigaAM v3 не найдена — откат на v2 (если она есть)", "Ai");
        }

        var (model, labels) = ResolveGigaamModel();
        if (model == null || labels == null)
        {
            error = preferV3 ? UiText.Tr("модели GigaAM v3 и v2 не найдены", "GigaAM v3 and v2 models not found") : UiText.Tr("модель GigaAM ONNX не найдена", "GigaAM ONNX model not found");
            return null;
        }
        var v2 = new BrainstormBuddy.Stt.NativeGigaamSttService(model, labels, accel, gpuDevice, logger: Logger);
        Logger.Info($"Native STT ready (v2): {model} (provider={v2.ActiveProvider})", "Ai");
        return v2;
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

    // Ищет модель GigaAM v3 (onnx + vocab в одной папке) по тем же приоритетам, что v2:
    // конфиг (файл или каталог) → %APPDATA%\models → рядом с exe\models → dev artifacts/gigaam-v3.
    private (string? onnx, string? vocab) ResolveGigaamV3Model()
        => BrainstormBuddy.Stt.GigaamV3ModelResolver.Resolve(Config.Audio.SttV3ModelPath, new[]
        {
            Path.Combine(AppDataDir, "models"),
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts")),
        });

    /// <summary>Есть ли на диске модель GigaAM v3 — для статуса в настройках.</summary>
    public bool HasGigaamV3Model()
        => ResolveGigaamV3Model().onnx != null;

    private bool _trayHintShown;
    // Разовая подсказка при сворачивании: куда делось окно и как вернуть.
    public void NotifyMinimizedToTray()
    {
        try
        {
            if (_trayHintShown || TrayIcon == null) return;
            _trayHintShown = true;
            TrayIcon.BalloonTipTitle = TT("L_Tray_BalloonTitle", "Оверлей свёрнут");
            TrayIcon.BalloonTipText = TT("L_Tray_BalloonBody", "Окно скрыто в трей. Вернуть: значок в трее → «Показать оверлей», или Ctrl+Shift+H.");
            TrayIcon.ShowBalloonTip(3000);
        }
        catch (Exception ex) { Logger.Warn($"NotifyMinimizedToTray failed: {ex.Message}", "UI"); }
    }

    // Клиент сообщил о смене доступности STT/LLM → пересобираем текст баннера
    // и показываем/прячем его в оверлее (в UI-потоке).
    private string _sttErrMsg = "", _llmErrMsg = "";
    // HTTP-код последней ошибки STT (401/403 → подсказка про ключ в резолвере);
    // null при таймауте/отсутствии соединения и после здорового ответа.
    private int? _sttHttpStatus;
    // Структурная причина последней STT-ошибки (ServerError/Timeout/Unreachable) —
    // резолвер локализует деталь по ней; None после здорового ответа.
    private SttHealthReason _sttErrKind = SttHealthReason.None;

    private void OnApiHealthChanged(object? sender, ApiHealthEventArgs e)
    {
        if (e.Component == ApiComponent.Stt) { _sttOk = e.Healthy; _sttErrMsg = e.Message ?? ""; _sttHttpStatus = e.HttpStatus; _sttErrKind = e.Reason; RefreshOverlayStatus(); }
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
                ? $"[{e.Timestamp:HH:mm:ss}] ←LLM {UiText.Tr("ОШИБКА", "ERROR")}: {e.Text}"
                : $"[{e.Timestamp:HH:mm:ss}] ←LLM {e.ElapsedSeconds:F1}s {e.TotalTokens}{UiText.Tr("тк", "tok")}: {e.Text}";
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
            title = TT("L_Banner_BothErrTitle", "Ошибка распознавания и ИИ");
            body = string.Format(TT("L_Banner_BothErrBodyFmt", "STT: {0}\nИИ: {1}"),
                string.IsNullOrWhiteSpace(_sttErrMsg) ? TT("L_Banner_NotResponding", "не отвечает") : _sttErrMsg,
                string.IsNullOrWhiteSpace(_llmErrMsg) ? TT("L_Banner_NotResponding", "не отвечает") : _llmErrMsg);
            target = "llm";
        }
        else if (!_sttOk)
        {
            title = TT("L_Banner_SttTitle", "Распознавание речи недоступно");
            var err = string.IsNullOrWhiteSpace(_sttErrMsg)
                ? TT("L_Banner_SttNoResponse", "STT-сервер не отвечает. Проверьте адрес распознавания в настройках.")
                : _sttErrMsg;
            // Если движок выбран в фолбэке (нет локальной модели → remote), объясняем
            // причину смены — иначе юзер видит только голый 401 от внешнего сервера.
            body = _sttFallbackReason is { } fb
                ? string.Format(TT("L_Banner_SttFallbackFmt", "Локальное распознавание недоступно: {0}.\nВнешний сервер: {1}"), fb, err)
                : err;
            target = "stt";
        }
        else if (!_llmOk && llmMatters)
        {
            title = TT("L_Banner_LlmTitle", "Ошибка ИИ (LLM)");
            body = string.IsNullOrWhiteSpace(_llmErrMsg)
                ? TT("L_Banner_LlmNoResponse", "LLM не отвечает. Проверьте адрес, ключ и модель в настройках.")
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
            try
            {
                if (!_settingsWindow.IsVisible) _settingsWindow.Show();
                if (_settingsWindow.WindowState == System.Windows.WindowState.Minimized)
                    _settingsWindow.WindowState = System.Windows.WindowState.Normal;
                _settingsWindow.Activate();
                _settingsWindow.Topmost = true;
                _settingsWindow.Topmost = false;
                return;
            }
            catch (Exception ex)
            {
                // Мёртвая ссылка на окно (Closed не отстрелил / диспетчер окна умер) —
                // раньше это кончалось NRE. Забываем ссылку и пересоздаём окно ниже.
                Logger.Warn($"Settings window restore failed ({ex.Message}) — recreating", "UI");
                _settingsWindow = null;
            }
        }

        // NRE-гард (боевой лог ~19:33): VM ещё не создана (ранний вызов или старт
        // упал до инициализации) — new SettingsWindow(null) падал бы в конструкторе
        // на vm.AudioPresets. Логируем факт вместо молчаливого NullReferenceException.
        if (SettingsVm is not { } vm)
        {
            Logger.Warn("OpenSettings: SettingsViewModel is null — app not fully initialized?", "UI");
            return;
        }

        try
        {
            _settingsWindow = new SettingsWindow(vm);
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
        Notifier.ShowInfo(TT("L_Toast_NewChat", "Новый чат"), string.Format(TT("L_Toast_NewChatPromptFmt", "Промпт: {0}"), prompt.Name));
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
            sb.AppendLine(TT("L_Export_TranscriptHeader", "=== BrainstormBuddy — Транскрибация ==="));
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();
            if (_overlay != null)
            {
                foreach (var qa in _overlay.History)
                {
                    var ts = qa.SttReceivedAt ?? qa.ChunkReadyAt ?? DateTime.Now;
                    // Метки → говорящий: [S1] → "S1: ", [Микрофон] → "Вы: ", [Динамик] — раньше резался целиком (invariant: выключенная диаризация = тот же файл).
                    var text = System.Text.RegularExpressions.Regex.Replace(qa.Question,
                            @"^\[S(\d+)\] ", "S$1: ")
                        .Replace("[Микрофон] ", TT("L_Export_You", "Вы: "))
                        .Replace("[You] ", TT("L_Export_You", "Вы: "))
                        .Replace("[Динамик] ", "")
                        .Replace("[Speaker] ", "");
                    sb.AppendLine($"{ts:HH:mm:ss.fff}  {text}");
                }
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Logger.Info($"Transcript exported to {path} ({_overlay?.History.Count ?? 0} entries)", "UI");
            Notifier.ShowInfo(TT("L_Toast_ExportTitle", "Экспорт"), string.Format(TT("L_Toast_ExportSavedFmt", "Транскрибация сохранена:\n{0}"), path));
            RevealInExplorer(path);
            _overlay?.FlashStatus(string.Format(TT("L_Status_TranscriptSavedFmt", "✓ Транскрипт сохранён: {0}"), System.IO.Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            Logger.Error("Export transcript failed", ex, "UI");
            Notifier.ShowWarning(TT("L_Toast_ExportErrTitle", "Ошибка экспорта"), ex.Message);
        }
    }

    public async Task ExportAiSummary()
    {
        try
        {
            var allText = CollectFullTranscript();
            if (string.IsNullOrWhiteSpace(allText))
            {
                Notifier.ShowInfo(TT("L_Toast_NoData", "Нет данных"), TT("L_Toast_NoDataBody", "Транскрибация пуста. Дождитесь распознавания речи."));
                return;
            }

            _overlay?.SetStatus(TT("L_Status_SummaryBuilding", "Составляю протокол встречи…"));
            var summaryPrompt = "Ты — секретарь на совещании. Ниже — транскрибация встречи. " +
                "Составь краткий протокол: 1) Тема встречи, 2) Участники (если упоминались), 3) Ключевые вопросы и ответы (тезисно), 4) Договорённости и дальнейшие шаги. " +
                "Пиши на русском, структурированно, без воды. " +
                "Если транскрибация обрывочная — отметь это и выдели то, что удалось понять.\n\n" + allText;

            var sw = Stopwatch.StartNew();
            var result = await ApiClient.AskAsync("Составь протокол встречи по транскрибации выше.",
                summaryPrompt, 800, new List<ChatMessage>(), CancellationToken.None);
            sw.Stop();
            // Возможные memory-директивы и из «мёртвого» промпта вырезаем — в файл протокола
            // сырые [[MEMORY_*]] не должны попадать; применяются, если память активна.
            var summaryText = (await ProcessMemoryDirectivesAsync(result.Content)).Clean;

            if (!string.IsNullOrWhiteSpace(summaryText))
            {
                var path = ResolveSavePath("summary");
                var sb = new StringBuilder();
                sb.AppendLine(TT("L_Export_SummaryHeader", "=== BrainstormBuddy — Протокол встречи ==="));
                sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} (STT+LLM: {sw.Elapsed.TotalSeconds:F1}s) ===");
                sb.AppendLine();
                sb.AppendLine(summaryText);
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Logger.Info($"AI summary exported to {path} ({sw.Elapsed.TotalSeconds:F1}s)", "UI");
                Notifier.ShowInfo(TT("L_Toast_SummaryTitle", "Протокол встречи"), string.Format(TT("L_Toast_SavedFmt", "Сохранён:\n{0}"), path));
                RevealInExplorer(path);
                _overlay?.FlashStatus(string.Format(TT("L_Status_SummarySavedFmt", "✓ Протокол сохранён: {0}"), System.IO.Path.GetFileName(path)));
            }
            else
            {
                // Причина (429/5xx/401) уже humanized в result.Error — показываем её,
                // а не общее «LLM не ответил», чтобы юзер видел лимит/сбой провайдера.
                var why = result.Error ?? TT("L_Err_LlmEmpty", "пустой ответ от LLM");
                _overlay?.SetStatus(string.Format(TT("L_Status_ErrorFmt", "Ошибка: {0}"), why));
                Notifier.ShowWarning(TT("L_Toast_SummaryTitle", "Протокол встречи"), why);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AI summary failed", ex, "UI");
            _overlay?.SetStatus(TT("L_Status_SummaryError", "Ошибка протокола"));
            Notifier.ShowWarning(TT("L_Toast_Error", "Ошибка"), ex.Message);
        }
    }

    public void FlushAndSend()
    {
        if (IsPaused) return;
        var chunk = AudioBuffer.GetChunkForTranscription();
        if (chunk.Length == 0)
        {
            Notifier.ShowInfo(TT("L_Toast_NoAudio", "Нет аудио"), TT("L_Toast_NoAudioBody", "Буфер пуст — нечего отправлять. Дождитесь речи."));
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
                    var label = TT("L_Label_Speaker", "[Динамик]") + " " + shown;
                    // «LLM выкл»: молния тоже работает транскрибатором — пузырь без ответа, в LLM ничего.
                    bool llmOff = Config.Ui.LlmDisabled;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _overlay?.AddSendingItem(label, (chunk.Length - 44) / 32000.0,
                            DateTime.Now, DateTime.Now, DateTime.Now, 0, transcriptOnly: llmOff);
                    });
                    AppendJournalWithMemory("question", shown); // журнал пишет реплику и в режиме «LLM выкл»
                    if (llmOff) return;
                    List<ChatMessage> historySnapshot;
                    lock (_historyLock) { historySnapshot = new List<ChatMessage>(_conversationHistory); }
                    // Memory recall + правила директив памяти — общий билдер для всех LLM-путей.
                    var sysPrompt = await BuildSystemPromptWithMemoryAsync(Config.Advanced.SystemPrompt, text, CancellationToken.None);
                    var result = await ApiClient.AskAsync(label, sysPrompt,
                        Config.Advanced.MaxResponseTokens, historySnapshot, CancellationToken.None);
                    var flushMem = await ProcessMemoryDirectivesAsync(result.Content);
                    var flushAnswer = flushMem.Clean;
                    if (!string.IsNullOrWhiteSpace(flushAnswer))
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _overlay?.MarkAnswered(flushAnswer, DateTime.Now, 0);
                            RefreshOverlayStatus();
                        });
                        AppendJournalWithMemory("answer", flushAnswer);
                    }
                    else
                    {
                        // Тот же контракт, что в WorkerLoop/SendSecretMessage: ошибка
                        // транспорта (429/5xx) выводится пользователю, а не глотается.
                        var why = result.Error
                            ?? ((result.IsSilent || !string.Equals(flushAnswer, result.Content, StringComparison.Ordinal))
                                ? TT("L_Err_ModelSilent", "Модель промолчала (пустой ответ). Попробуйте переформулировать или увеличить лимит токенов.")
                                : TT("L_Err_LlmEmpty", "пустой ответ от LLM"));
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _overlay?.MarkFailed(why, DateTime.Now, 0);
                            RefreshOverlayStatus();
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
            Notifier.ShowInfo(TT("L_Toast_LlmOff", "LLM выключен"), TT("L_Toast_LlmOffSend", "Включите LLM кнопкой на нижней панели, чтобы отправлять сообщения."));
            return;
        }
        var label = $"[secret] {text}";
        await Dispatcher.InvokeAsync(() =>
        {
            _overlay?.AddSendingItem(label, 0, DateTime.Now, null, null, 0);
            _overlay?.SetStatus(TT("L_Status_ThinkingSecret", "Думаю над [secret]…"));
            // Караоке: секретное сообщение — реплика юзера без метки [secret].
            _karaoke?.PushLine("user", text);
            AppendJournalWithMemory("user", text); // журнал — всегда, без фильтров Show*
        });
        try
        {
            List<ChatMessage> historySnapshot;
            lock (_historyLock) { historySnapshot = new List<ChatMessage>(_conversationHistory); }
            var stream = Config.Advanced.StreamLlmAnswers;
            var gotDelta = false;
            if (stream) _karaoke?.BeginStreamLine("answer");
            // Memory recall + правила директив памяти — общий билдер для всех LLM-путей.
            var sysPrompt = await BuildSystemPromptWithMemoryAsync(Config.Advanced.SystemPrompt, text, CancellationToken.None);
            var sw = Stopwatch.StartNew();
            var result = await ApiClient.AskAsync(label, sysPrompt,
                Config.Advanced.MaxResponseTokens, historySnapshot, CancellationToken.None,
                stream ? (Action<string>)(d =>
                {
                    gotDelta = true;
                    _overlay?.AppendAnswerDelta(d);
                    _karaoke?.AppendToStreamLine(d);
                }) : null);
            sw.Stop();
            // Memory-директивы: применить (тост) и вырезать до финала стрим-строки —
            // сырой текст с [[MEMORY_*]] уже мог отстримиться в ленту.
            var secMem = await ProcessMemoryDirectivesAsync(result.Content);
            var secretAnswer = secMem.Clean;
            // Фикс бага «записать в память → ошибка "промолчала"»: модель вернула
            // ТОЛЬКО [[MEMORY_*]]-директиву → Clean пуст, но факт записан (Applied>0).
            // Это УСПЕХ, не молчание: подставляем подтверждение вместо красной ошибки.
            if (string.IsNullOrWhiteSpace(secretAnswer) && secMem.Applied.Count > 0)
                secretAnswer = TT("L_Answer_MemorySaved", "✔ записано в память");
            var secMemStripped = !string.Equals(secretAnswer, result.Content, StringComparison.Ordinal);
            FinalizeKaraokeStreamLine(stream, gotDelta, result.IsSilent, secMemStripped, secretAnswer);
            Logger.Info($"Secret ask done in {sw.ElapsedMilliseconds}ms: '{(secretAnswer ?? "<null>").Substring(0, Math.Min(80, (secretAnswer ?? "<null>").Length))}'", "Answer");
            if (!string.IsNullOrWhiteSpace(secretAnswer))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.MarkAnswered(secretAnswer, DateTime.Now, 0);
                    RefreshOverlayStatus();
                    // Секретные ответы обходят AnswerWorkerLoop — пушим ответ в караоке здесь.
                    if (!gotDelta) _karaoke?.PushLine("answer", secretAnswer);
                });
                AppendJournalWithMemory("answer", secretAnswer); // и стрим-, и не-стрим-ответы
                lock (_historyLock)
                {
                    _conversationHistory.Add(ChatMessage.User(label));
                    _conversationHistory.Add(ChatMessage.Assistant(secretAnswer));
                    TrimHistory(_conversationHistory, Config.Advanced.HistorySize);
                }
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    // Приоритет — человеческая ошибка транспорта/API (429 rate limit,
                    // 5xx, 401): AskResult.Error уже приведён в читаемый вид через
                    // HumanizeApiError. Без неё юзер видел «пустой ответ от LLM»,
                    // хотя причина — лимит провайдера (живой кейс 2026-10-05: Groq
                    // TPM 429 + 500 подряд после ~6 минут созвона).
                    var why = result.Error
                        ?? ((result.IsSilent || secMemStripped)
                            ? TT("L_Err_ModelSilent", "Модель промолчала (пустой ответ). Попробуйте переформулировать или увеличить лимит токенов.")
                            : TT("L_Err_LlmEmpty", "пустой ответ от LLM"));
                    _overlay?.MarkFailed(why, DateTime.Now, 0);
                    RefreshOverlayStatus();
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
            Notifier.ShowInfo(TT("L_Toast_LlmOff", "LLM выключен"), TT("L_Toast_LlmOffExpand", "Включите LLM кнопкой на нижней панели, чтобы расширять ответы."));
            return;
        }
        _overlay?.SetStatus(TT("L_Status_Expanding", "Расширяю ответ…"));
        var expandTemplate = Config.Advanced.ExpandInstruction;
        var expandSection = expandTemplate
            .Replace("{PREVIOUS_QUESTION}", pair.Question ?? "")
            .Replace("{PREVIOUS_ANSWER}", pair.Answer ?? "");
        var fullSystemPrompt = Config.Advanced.SystemPrompt + "\n\n---\n" + expandSection;
        try
        {
            // Memory recall + правила директив памяти — общий билдер для всех LLM-путей.
            fullSystemPrompt = await BuildSystemPromptWithMemoryAsync(
                fullSystemPrompt, pair.Question ?? "", CancellationToken.None);
            var result = await ApiClient.AskAsync("[EXPAND] Разверни последний ответ",
                fullSystemPrompt, 400, new List<ChatMessage>(), CancellationToken.None);
            var expandText = (await ProcessMemoryDirectivesAsync(result.Content)).Clean;
            if (!string.IsNullOrWhiteSpace(expandText))
            {
                pair.ExpandedAnswer = expandText;
                _overlay?.SetStatus(TT("L_Status_Expanded", "Ответ расширен"));
            }
            else
            {
                // 429/5xx/401 — показываем причину, а не молчим: AskResult.Error уже
                // приведён в читаемый вид через HumanizeApiError (как в SendSecretMessage).
                var why = result.Error ?? TT("L_Err_LlmEmpty", "пустой ответ от LLM");
                _overlay?.SetStatus(string.Format(TT("L_Status_ExpandFailedFmt", "Расширение не удалось: {0}"), why));
                Notifier.ShowWarning(TT("L_Toast_ExpandTitle", "Расширение ответа"), why);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ExpandAnswer failed", ex, "UI");
            Notifier.ShowWarning(TT("L_Toast_Error", "Ошибка"), ex.Message);
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
                    .Replace("[Микрофон] ", "[Interviewer] ")
                    .Replace("[You] ", "[Interviewer] ");
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
            // Состояние локальной E5-модели (спека memory2 §42): режим, статус
            // loader'а, vector-space тег — ключ к диагностике «почему recall FTS-only».
            if (MemoryEmbedder is ILocalEmbeddingControl embCtl)
            {
                var st = embCtl.GetStatus();
                extra.AppendLine($"E5-эмбеддинги: tag={MemoryEmbedder.EmbeddingTag}, " +
                    $"dir={embCtl.ModelDir}, state={st.State}, leases={st.ActiveLeases}, " +
                    $"failed={st.FailedLoads}, nextUnload={st.NextUnloadUtc:HH:mm:ss}");
            }
            else
            {
                extra.AppendLine($"E5-эмбеддинги: mode={Config.Memory.EmbeddingsMode}, " +
                    $"provider={MemoryEmbedder?.Name ?? "-"}");
            }
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
        // Память: текущая встреча кончилась → extract-джоба (если write+mode!=off), дальше — новая.
        FinalizeMemoryMeeting();
        // Следующая реплика начнёт новый файл-журнал встречи (старая остаётся в истории).
        MeetingJournal?.BeginNewMeeting();
        // И новая запись mem_meetings — флаги из Config.Memory.Default*.
        BeginMemoryMeeting();
        // Speaker-идентификаторы (S1..S<n>) валидны только внутри звонка — новый чат = новый набор.
        // Nemotron: finish текущего стрима + новый — fed-поток НЕ прерываем:
        // Core считает индексы кумулятивно от attach, шкала буфера не разъезжается.
        _speakerDiarizer?.Reset();
        _lastLoopbackSpeakerId = null;
        _lastLoopbackSpeakerEpoch = -1;
    }

    // ================= ПАМЯТЬ (спека memory.txt) =================

    /// <summary>
    /// Подсистема памяти: SQLite-стор + embeddings + recall + extractor/consolidator +
    /// фоновый воркер джоб. Создаётся ТОЛЬКО при Config.Memory.Enabled; любая ошибка
    /// инициализации → Error-лог и память выключается молча (audio/STT/assistant не тронуты).
    /// Идемпотентно: при живом MemoryStore — no-op. Поэтому вызывается и из OnStartup,
    /// и после сохранения настроек — память включается без рестарта.
    /// Может выполняться на фоновом потоке (инициализация из ApplyLiveConfigChanges —
    /// в Task.Run, чтобы не морозить UI сохранения).
    /// </summary>
    private void EnsureMemoryServices()
    {
        if (MemoryStore != null) return;
        if (!Config.Memory.Enabled)
        {
            Logger.Info("Memory disabled in config — skipping init", "Memory");
            return;
        }
        // Параллельная инициализация запрещена: второй стор залочил бы SQLite-файл
        // первого и два MemoryJobWorker дрались бы за одни джобы.
        if (Interlocked.Exchange(ref _memoryInitInFlight, 1) != 0) return;
        // Все сервисы собираются в ЛОКАЛЬНЫЕ переменные и публикуются в поля одним
        // блоком в конце: UI читает MemoryStore != null как «память активна» и не
        // должен словить стор без уже готовых recall/embedder/воркера (NRE).
        SqliteMemoryStore? store = null;
        CancellationTokenSource? cts = null;
        try
        {
            var dbPath = Path.Combine(AppDataDir, "memory.db");
            store = new SqliteMemoryStore(dbPath, Logger);
            store.Migrate();
            Logger.Info($"Memory store opened: {dbPath} (fts={store.FtsAvailable})", "Memory");

            var embedder = EmbeddingFactory.Create(Config.Memory, Config.Api, AppDataDir, Logger);
            var recall = new MemoryRecallService(store, embedder, Config.Memory, Logger);
            var memApi = ResolveMemoryApiClient.Resolve(Config, ApiClient, Logger);
            var extractor = memApi != null ? new MemoryExtractor(memApi, Logger) : null;
            var consolidator = new MemoryConsolidator(memApi, embedder, Config.Memory, Logger);

            cts = new CancellationTokenSource();
            var handlers = new MemoryJobHandlers(store, Config.Memory,
                extractor, consolidator, embedder, Logger, cts.Token);
            // Review-режим: после extract-джоба показать окно проверки Pending-фактов.
            handlers.ExtractionApplied = OnMemoryExtractionApplied;
            var worker = new MemoryJobWorker(store, handlers.CreateHandlers(), Config.Memory, Logger);
            var workerTask = Task.Run(() => worker.RunAsync(cts.Token));

            // Пережившие рестарт джобы: воркер тикает RequeueStale в своём цикле,
            // но делаем это и здесь явно — extract-джоба, поставленная в момент выхода,
            // не должна ждать JobStaleSec до первого подхвата.
            store.RequeueStale(TimeSpan.FromMinutes(5));
            var carriedPending = store.ListJobs("pending").Count;
            if (carriedPending > 0)
                Logger.Info($"Memory: {carriedPending} pending job(s) carried over from previous session", "Memory");

            // Раз в запуск — снос сегментов и устаревших фактов по retention (спека RETENTION).
            store.EnqueueJob("retention");
            // Реальный бэкфилл (спека §34/§36): до-эмбед активных фактов без
            // embedding текущего vector-space — покрывает и старт с пустой базы,
            // и смену модели (тег сменился → все items «неэбеджены»). Нет
            // работы → джоба no-op.
            store.EnqueueJob("embed");

            // Публикация — последний шаг: стор в самом конце, он же «сигнальный» флаг.
            _memoryCts = cts;
            _memoryWorker = worker;
            _memoryWorkerTask = workerTask;
            MemoryRecall = recall;
            MemoryExtractor = extractor;
            MemoryConsolidator = consolidator;
            MemoryEmbedder = embedder;
            _memoryApi = memApi;
            _memoryStore = store;
            Logger.Info("Memory worker started; retention+embed jobs enqueued", "Memory");

            // Встреча живёт от старта сессии — иначе сегменты до первого
            // «нового чата» писались бы в никуда (meeting row создавался только в Reset).
            // BeginMemoryMeeting открывает окна (PreCallBrief) и дёргает оверлей → только
            // UI-поток; при фоновой инициализации из настроек маршаллим на Dispatcher.
            if (Dispatcher.CheckAccess())
                BeginMemoryMeeting();
            else
                Dispatcher.InvokeAsync(() => { if (!_shuttingDown) BeginMemoryMeeting(); });
        }
        catch (Exception ex)
        {
            // Память — опция: сбой БД/моделей не должен ронять основной функционал.
            // Поля здесь НЕ трогаем — они ещё не публиковались (null); dispose локальных.
            Logger.Error("Memory init failed — memory disabled for this session", ex, "Memory");
            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }
            try { store?.Dispose(); } catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _memoryInitInFlight, 0);
        }
    }

    /// <summary>
    /// Полная остановка подсистемы памяти: финализация встречи (extract-джоба), отмена
    /// воркера, dispose стора. Идемпотентно (MemoryStore == null → no-op). Dispose
    /// потенциально блокирующий (закрытие SQLite), поэтому выполняется в фоне — поля
    /// обнуляются на вызывающем потоке ДО dispose: читатели сразу видят «памяти нет»,
    /// а стор живёт ещё в локальной ссылке до конца Task.Run. Вызывается из
    /// ApplyLiveConfigChanges (выключили память в настройках) и из OnExit.
    /// </summary>
    private void TeardownMemoryServices()
    {
        if (MemoryStore == null) return;
        Logger?.Info("Memory teardown: stopping services", "Memory");
        try
        {
            FinalizeMemoryMeeting();
        }
        catch (Exception ex) { Logger?.Warn($"Memory finalize failed: {ex.Message}", "Memory"); }

        // Снапшот ссылок → обнуление полей → отложенный dispose в фоне.
        var store = _memoryStore;
        var cts = _memoryCts;
        var workerTask = _memoryWorkerTask;
        _memoryStore = null;
        MemoryRecall = null;
        MemoryExtractor = null;
        MemoryConsolidator = null;
        MemoryEmbedder = null;
        _memoryApi = null;
        _memoryWorker = null;
        _memoryWorkerTask = null;
        _memoryCts = null;
        _overlay?.RefreshMemoryButton();

        try { cts?.Cancel(); } catch { }
        _ = Task.Run(() =>
        {
            try
            {
                try { workerTask?.Wait(TimeSpan.FromSeconds(3)); }
                catch { /* worker не успел — джобы подхватятся на следующем запуске */ }
                store?.Dispose();
                Logger?.Debug("Memory store disposed", "Memory");
            }
            catch (Exception ex) { try { Logger?.Warn($"Memory shutdown failed: {ex.Message}", "Memory"); } catch { /* логгер уже dispose (OnExit) */ } }
            try { cts?.Dispose(); } catch { }
        });
    }
    // Флаг «инициализация памяти в полёте» — защита от двойного стора при быстрых
    // повторных сохранениях настроек (см. EnsureMemoryServices).
    private int _memoryInitInFlight;

    /// <summary>LLM-клиент для memory-экстракции/окон (null при MemoryModelMode=disabled).</summary>
    private IApiClient? _memoryApi;

    /// <summary>
    /// Журнальная реплика + зеркало в mem_segments, когда активна встреча с WriteMemory.
    /// Тут же лениво прописываем JournalPath (jsonl-файл появляется в первом Append).
    /// </summary>
    private void AppendJournalWithMemory(string role, string text, string? speaker = null)
    {
        MeetingJournal?.Append(role, text, speaker);

        var store = MemoryStore;
        var mid = _currentMemoryMeetingId;
        if (store == null || mid == null) return;
        try
        {
            var m = store.GetMeeting(mid.Value);
            if (m == null || !m.WriteMemory) return;

            if (!_memoryJournalPathWritten)
            {
                var jp = MeetingJournal?.CurrentPath;
                if (!string.IsNullOrEmpty(jp))
                {
                    m.JournalPath = jp;
                    store.UpsertMeeting(m);
                    _memoryJournalPathWritten = true;
                    Logger.Debug($"Memory: journal_path set → {jp}", "Memory");
                }
            }

            store.AppendSegment(new MemorySegment
            {
                MeetingId = mid.Value,
                Role = role,
                Text = text,
                Speaker = speaker,
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: segment append failed: {ex.Message}", "Memory");
        }
    }

    /// <summary>
    /// Начало новой встречи в памяти (вместе с новым журнальным файлом). Вызывается из
    /// ResetConversation ПОСЛЕ MeetingJournal.BeginNewMeeting. Флаги берутся из дефолтов
    /// конфига (спека DEFAULT UX: «запоминать встречи по умолчанию» + Use/Write).
    /// </summary>
    private void BeginMemoryMeeting()
    {
        // Ручные алиасы спикеров живут только внутри встречи.
        lock (_speakerAliases) _speakerAliases.Clear();
        _currentMemoryMeetingId = null;
        _memoryJournalPathWritten = false;
        _preCallBriefShown = false;

        var store = MemoryStore;
        if (store == null) { _overlay?.RefreshMemoryButton(); return; }

        // Orphaned ActiveProjectId (проект удалён/профиль новый) валил бы встречу
        // на FK mem_meeting.project_id → mem_project.id. Санируем один раз:
        // несуществующий id обнуляем в конфиге — встреча уходит в Global-scope.
        try
        {
            var pid = Config.Memory.ActiveProjectId;
            if (pid is { } p && store.ListProjects(includeArchived: true).All(x => x.Id != p))
            {
                Logger.Warn($"Memory: ActiveProjectId={p} указывает на удалённый проект — сброшен в Global", "Memory");
                Config.Memory.ActiveProjectId = null;
                SaveConfigSafe();
            }
        }
        catch (Exception ex) { Logger.Warn($"Memory: project-id sanitize failed: {ex.Message}", "Memory"); }

        try
        {
            var m = new MemoryMeeting
            {
                Title = string.Format(UiText.Tr("Встреча {0}", "Meeting {0}"), DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
                ProjectId = Config.Memory.ActiveProjectId,
                MemoryMode = Config.Memory.SaveMode,
                UseMemory = Config.Memory.DefaultUseMemory,
                // SaveMode=off — встреча существует (протокол), но память не пишется.
                WriteMemory = Config.Memory.DefaultWriteMemory &&
                    !string.Equals(Config.Memory.SaveMode, "off", StringComparison.OrdinalIgnoreCase),
                StartedAtUtc = DateTime.UtcNow,
                JournalPath = MeetingJournal?.CurrentPath ?? "",
            };
            _currentMemoryMeetingId = store.UpsertMeeting(m);
            Logger.Info($"Memory: meeting created id={_currentMemoryMeetingId} mode={m.MemoryMode} use={m.UseMemory} write={m.WriteMemory}", "Memory");
            MaybeShowPreCallBrief(m);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: begin meeting failed: {ex.Message}", "Memory");
        }
        _overlay?.RefreshMemoryButton();
    }

    /// <summary>
    /// Финализация текущей memory-встречи: EndedAtUtc + extract-джоба (если встреча
    /// записывалась и SaveMode != off). Вызывается на «новый чат» и на выходе.
    /// </summary>
    private void FinalizeMemoryMeeting()
    {
        var store = MemoryStore;
        var mid = _currentMemoryMeetingId;
        if (store == null || mid == null) return;
        try
        {
            var m = store.GetMeeting(mid.Value);
            if (m != null && m.EndedAtUtc == null)
            {
                m.EndedAtUtc = DateTime.UtcNow;
                store.UpsertMeeting(m);
            }
            var segments = m != null ? store.GetSegments(mid.Value).Count : 0;
            if (m != null && m.WriteMemory && m.MemoryMode != "off" && segments > 0 &&
                !string.Equals(Config.Memory.ReviewMode, "manual", StringComparison.OrdinalIgnoreCase))
            {
                store.EnqueueJob("extract", MemoryJson.Serialize(new { meetingId = mid.Value }));
                Logger.Info($"Memory: extraction job enqueued for meeting {mid.Value}", "Memory");
            }
            else
            {
                Logger.Info($"Memory: meeting {mid.Value} ended without extraction " +
                    $"(write={m?.WriteMemory}, mode={m?.MemoryMode}, segments={segments}, " +
                    $"review={Config.Memory.ReviewMode})", "Memory");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: finalize meeting failed: {ex.Message}", "Memory");
        }
        _currentMemoryMeetingId = null;
        _overlay?.RefreshMemoryButton();
    }

    /// <summary>
    /// Колбэк MemoryJobHandlers после успешного extract-джоба (worker-поток).
    /// В review-режиме маршаллим на UI и показываем MemoryReviewWindow.
    /// </summary>
    private void OnMemoryExtractionApplied(long meetingId)
    {
        if (_shuttingDown) return;
        try
        {
            Dispatcher.InvokeAsync(() => ShowMemoryReviewWindow(meetingId));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: review dispatch failed: {ex.Message}", "Memory");
        }
    }

    /// <summary>
    /// Review-режим (спека MEMORY REVIEW MODES): Pending-факты встречи — в немодальном
    /// MemoryReviewWindow (одна копия, один показ на встречу). Нет Pending → не показываем.
    /// </summary>
    private void ShowMemoryReviewWindow(long meetingId)
    {
        var store = MemoryStore;
        if (store == null || _reviewWindowOpen || _reviewShownForMeetingId == meetingId)
            return;
        if (!string.Equals(Config.Memory.ReviewMode, "review", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var hasPending = store
                .ListItems(status: MemoryItemStatus.Pending, limit: 1000)
                .Any(i => i.SourceMeetingId == meetingId);
            if (!hasPending) return;

            _reviewShownForMeetingId = meetingId;
            var w = new MemoryReviewWindow();
            w.Initialize(store, meetingId);
            if (_overlay is { IsVisible: true })
                w.Owner = _overlay;
            _reviewWindowOpen = true;
            w.Closed += (_, _) => _reviewWindowOpen = false;
            w.Show();
            Logger.Info($"Memory: review window shown for meeting {meetingId}", "Memory");
        }
        catch (Exception ex)
        {
            _reviewWindowOpen = false;
            Logger.Warn($"Memory: review window failed: {ex.Message}", "Memory");
        }
    }

    /// <summary>
    /// Pre-call brief (спека PRE-CALL BRIEF): при старте встречи с UseMemory показываем
    /// контекст из памяти ОДИН раз. Окно — класс U3 PreCallBriefWindow.
    /// </summary>
    private void MaybeShowPreCallBrief(MemoryMeeting m)
    {
        if (_preCallBriefShown || !m.UseMemory || MemoryStore == null || MemoryRecall == null)
            return;
        _preCallBriefShown = true;
        try
        {
            var w = new PreCallBriefWindow();
            w.Initialize(MemoryStore, MemoryRecall, m.ProjectId);
            // Owner=overlay держит бриф выше Topmost-оверлея: иначе показанный
            // позже оверлей перекрывает кнопки и первый клик «не закрывает».
            if (_overlay != null) w.Owner = _overlay;
            // Старый бриф прошлой встречи мог остаться открытым — иначе окна
            // стекуются и закрытие верхнего «открывает» такое же ниже.
            _preCallBrief?.Close();
            _preCallBrief = w;
            // Тоглы стартуют из флагов встречи (дефолты уже проставлены в BeginMemoryMeeting).
            w.UseMemoryChecked = m.UseMemory;
            w.WriteMemoryChecked = m.WriteMemory;
            var meetingId = m.Id;
            w.Closed += (_, _) =>
            {
                SavePreCallBriefChoices(w, meetingId);
                if (ReferenceEquals(_preCallBrief, w)) _preCallBrief = null;
            };
            w.Show();
            w.Activate(); // фокус сразу — иначе первый клик уходит на активацию окна
            Logger.Info("Memory: pre-call brief shown", "Memory");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: pre-call brief failed: {ex.Message}", "Memory");
        }
    }

    /// <summary>
    /// Закрытие Pre-call Brief: тоглы и повестка сохраняются во встречу только при
    /// «Начать встречу» (w.Started). «Пропустить»/Esc — отказ: флаги встречи
    /// остаются дефолтными из BeginMemoryMeeting. Встречу перечитываем из стора,
    /// чтобы не затереть JournalPath/Summary стейл-объектом.
    /// </summary>
    private void SavePreCallBriefChoices(PreCallBriefWindow w, long meetingId)
    {
        var store = MemoryStore;
        // «Пропустить»/Esc (Started=false) — отказ от брифа: тоглы встречи не трогаем,
        // они уже проставлены дефолтами в BeginMemoryMeeting.
        if (_shuttingDown || store == null || !w.Started) return;
        try
        {
            var m = store.GetMeeting(meetingId);
            if (m == null) return;
            m.UseMemory = w.UseMemoryChecked;
            m.WriteMemory = w.WriteMemoryChecked;
            m.Agenda = w.AgendaText;
            store.UpsertMeeting(m);
            Logger.Info($"Memory: pre-call brief saved for meeting {meetingId} " +
                $"(use={m.UseMemory}, write={m.WriteMemory}, agenda={m.Agenda.Length} chars)", "Memory");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: pre-call brief save failed: {ex.Message}", "Memory");
        }
    }

    /// <summary>
    /// Финальный системный промпт: baseSystemPrompt + префикс &lt;memory_context&gt;
    /// (hybrid recall, спека AUTO RECALL, «RECALL LATENCY»: bounded 300ms, ошибки →
    /// fallback «без памяти») + суффикс ПРАВИЛА ПАМЯТИ при активной записи встречи.
    /// Все LLM-пути ответов проходят через этот метод — правила директив
    /// [[MEMORY_*]] должны быть в промпте везде, где ответ парсится на директивы.
    /// </summary>
    private async Task<string> BuildSystemPromptWithMemoryAsync(string baseSystemPrompt, string queryText, CancellationToken ct)
    {
        var recall = MemoryRecall;
        var store = MemoryStore;
        if (store == null || !Config.Memory.Enabled)
            return baseSystemPrompt;

        long? mid = null;
        MemoryMeeting? meeting = null;
        var agenda = "";
        try
        {
            mid = _currentMemoryMeetingId;
            meeting = mid != null ? store.GetMeeting(mid.Value) : null;
            agenda = meeting?.Agenda.Trim() ?? "";
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: meeting read failed: {ex.Message}", "Memory");
        }
        var useMemory = meeting?.UseMemory ?? (mid == null && Config.Memory.DefaultUseMemory);
        // WriteMemory активна ⇒ модель знает формат директив. SaveMode=off — писать нельзя.
        var writeMemory = (meeting?.WriteMemory ?? (mid == null && Config.Memory.DefaultWriteMemory)) &&
            !string.Equals(Config.Memory.SaveMode, "off", StringComparison.OrdinalIgnoreCase);

        var prompt = baseSystemPrompt;

        if (useMemory && recall != null && Config.Memory.AutoRecall && !string.IsNullOrWhiteSpace(queryText))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(MemoryRecallTimeoutMs);
                var projId = Config.Memory.ActiveProjectId;
                var hits = await recall.RecallAsync(new MemoryRecallQuery(
                    queryText,
                    Scope: projId.HasValue ? MemoryScopeType.Project : null,
                    ScopeId: projId,
                    MeetingId: mid,
                    MaxItems: Config.Memory.RecallMaxItems,
                    MaxTokens: Config.Memory.RecallMaxTokens), linked.Token);
                var block = recall.BuildContextBlock(hits, Config.Memory.RecallMaxTokens);
                Logger.Info($"Memory recall: {hits.Count} hits in {sw.ElapsedMilliseconds}ms", "Memory");
                // Повестка из Pre-call Brief — первой строкой блока, до hits.
                if (agenda.Length > 0)
                    block = string.IsNullOrWhiteSpace(block)
                        ? $"<memory_context>\nПовестка встречи: {agenda}\n</memory_context>"
                        : block.Replace("<memory_context>\n",
                                        $"<memory_context>\nПовестка встречи: {agenda}\n");
                if (!string.IsNullOrWhiteSpace(block))
                    prompt = block + "\n\n" + prompt;
            }
            catch (OperationCanceledException)
            {
                Logger.Warn($"Memory recall: timeout {MemoryRecallTimeoutMs}ms — asking without memory", "Memory");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Memory recall failed: {ex.Message} — asking without memory", "Memory");
            }
        }

        if (writeMemory)
            prompt += MemoryRulesPromptSuffix;
        return prompt;
    }

    /// <summary>Правила inline-директив памяти в конце системного промпта (добавляется,
    /// когда у встречи активна запись). Формат разбирает MemoryDirectiveParser.</summary>
    private const string MemoryRulesPromptSuffix =
        "\n\nПРАВИЛА ПАМЯТИ:\n" +
        "- Если собеседник просит запомнить/внести в память/обновить факт — добавь в САМОЕ НАЧАЛО ответа директиву: " +
        "[[MEMORY_ADD]]краткий факт[[/MEMORY_ADD]], для правки: " +
        "[[MEMORY_UPDATE]]старый факт→новый факт[[/MEMORY_UPDATE]], для забывания: " +
        "[[MEMORY_FORGET]]факт[[/MEMORY_FORGET]].\n" +
        "- Директивы скрыты от пользователя. НИКОГДА не пиши «записал/запомнил» без реальной директивы.\n" +
        "- В блоке <memory_context> уже подгружены известные факты — опирайся на них, дубли не добавляй.";

    /// <summary>Локализованная строка по ключу с фолбэком (для строк, которых ещё может не быть в словарях).</summary>
    private static string TT(string key, string fallback) =>
        Application.Current.TryFindResource(key) as string ?? fallback;

    /// <summary>Зелёный тост «в память добавлено» (5 сек). Вызывается из любого потока — маршаллим на UI.</summary>
    public void ShowMemoryAddedToast(IReadOnlyList<string> facts)
    {
        var body = string.Join("\n", facts);
        Dispatcher.InvokeAsync(() => _overlay?.ShowMemoryToast(
            TT("L_MemToast_AddedTitle", "В память добавлено"), body));
    }

    /// <summary>
    /// Разбор inline-директив памяти ([[MEMORY_ADD]]/[[MEMORY_UPDATE]]/[[MEMORY_FORGET]])
    /// в ответе LLM: применяет их через MemoryWriter (когда память активна) и возвращает
    /// текст без директив — юзер никогда не видит сырые теги (пузырь/караоке/журнал).
    /// Директивы при выключенной памяти тоже вырезаются, но не применяются.
    /// </summary>
    private async Task<(string Clean, IReadOnlyList<string> Applied)> ProcessMemoryDirectivesAsync(
        string? answer, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(answer) || !answer.Contains("[[", StringComparison.Ordinal))
            return (answer ?? "", Array.Empty<string>());

        var dirs = MemoryDirectiveParser.Parse(answer);
        if (dirs.Count == 0)
            return (answer, Array.Empty<string>());

        var clean = MemoryDirectiveParser.Strip(answer);
        var store = MemoryStore;
        if (store == null || !Config.Memory.Enabled)
        {
            Logger.Info($"Memory: {dirs.Count} directive(s) stripped — memory off (not applied)", "Memory");
            return (clean, Array.Empty<string>());
        }

        try
        {
            var applied = await new MemoryWriter(store, Logger)
                .ApplyAsync(dirs, CurrentMeetingId, ct);
            if (applied.Count > 0)
            {
                Logger.Info($"Memory: {applied.Count} directive(s) applied", "Memory");
                ShowMemoryAddedToast(applied);
            }
            return (clean, applied);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: directives apply failed: {ex.Message}", "Memory");
            return (clean, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Финал стрим-строки караоке с учётом вырезанных директив: в стрим ушёл СЫРОЙ
    /// текст с [[MEMORY_*]] — если дельты были и директивы вырезаны, строку снимаем
    /// и пушим очищенную заново (DropStreamLine после EndStreamLine — no-op).
    /// Вызывать ДО окончания стрим-строки (аналогично isSilent → DropStreamLine).
    /// </summary>
    private void FinalizeKaraokeStreamLine(bool stream, bool gotDelta, bool isSilent,
        bool directivesStripped, string cleanAnswer)
    {
        if (!stream) return;
        if (isSilent || (gotDelta && directivesStripped))
        {
            _karaoke?.DropStreamLine();
            if (!isSilent && !string.IsNullOrWhiteSpace(cleanAnswer))
                _karaoke?.PushLine("answer", cleanAnswer);
        }
        else
        {
            _karaoke?.EndStreamLine();
        }
    }

    /// <summary>Жёсткий бюджет live-recall на запрос (спека RECALL LATENCY ≤ ~300ms).</summary>
    private const int MemoryRecallTimeoutMs = 300;

    /// <summary>
    /// Состояние чипа памяти для оверлея: 0=off (нет стора / память выкл / встреча off),
    /// 1=только чтение (use &amp;&amp; !write), 2=только запись (!use &amp;&amp; write),
    /// 3=чтение+запись. До первой встречи показываем дефолты конфига.
    /// </summary>
    public int GetMemoryIndicatorState()
    {
        var store = MemoryStore;
        if (store == null || !Config.Memory.Enabled) return 0;
        bool use, write;
        var mid = _currentMemoryMeetingId;
        if (mid != null)
        {
            var m = store.GetMeeting(mid.Value);
            use = m?.UseMemory ?? false;
            write = m?.WriteMemory ?? false;
        }
        else
        {
            use = Config.Memory.DefaultUseMemory;
            write = Config.Memory.DefaultWriteMemory;
        }
        if (use && write) return 3;
        if (use) return 1;
        if (write) return 2;
        return 0;
    }

    /// <summary>
    /// Тогл UseMemory (хоткей MemoryToggle + клик по чипу): переключает флаг текущей
    /// встречи и дефолт для следующих; оверлей показывает новое состояние.
    /// </summary>
    public void ToggleMeetingMemory()
    {
        var store = MemoryStore;
        if (store == null || !Config.Memory.Enabled)
        {
            Notifier.ShowInfo(TT("L_Toast_Memory", "Память"), TT("L_Toast_MemoryOffSettings", "Память выключена в настройках"));
            return;
        }
        try
        {
            var mid = _currentMemoryMeetingId;
            MemoryMeeting? m = mid != null ? store.GetMeeting(mid.Value) : null;
            var next = !(m?.UseMemory ?? Config.Memory.DefaultUseMemory);
            Config.Memory.DefaultUseMemory = next;
            if (m != null)
            {
                m.UseMemory = next;
                store.UpsertMeeting(m);
            }
            Logger.Info($"Memory: UseMemory → {next} (meeting={mid?.ToString() ?? "none"})", "Memory");
            _overlay?.RefreshMemoryButton();
            _overlay?.FlashStatus(next ? TT("L_Status_MemoryOn", "Память: вкл") : TT("L_Status_MemoryOff", "Память: выкл"));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: toggle failed: {ex.Message}", "Memory");
        }
    }

    /// <summary>
    /// Окно «Ask Memory» (хоткей AskMemory): свободный вопрос к памяти.
    /// Окно — класс U3 AskMemoryWindow.
    /// </summary>
    public void OpenAskMemoryWindow()
    {
        if (!Config.Memory.Enabled || MemoryStore == null || MemoryRecall == null)
        {
            Notifier.ShowInfo(TT("L_Toast_Memory", "Память"), TT("L_Toast_MemoryOffEnable", "Память выключена — включите её в настройках"));
            return;
        }
        try
        {
            if (_askMemoryWindow != null)
            {
                _askMemoryWindow.Activate();
                return;
            }
            var w = new AskMemoryWindow();
            w.Initialize(MemoryRecall, _memoryApi ?? ApiClient, MemoryStore, Config.Memory);
            w.Closed += (s, e) => _askMemoryWindow = null;
            _askMemoryWindow = w;
            w.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("OpenAskMemoryWindow failed", ex, "Memory");
            Notifier.ShowWarning(TT("L_Toast_Memory", "Память"), ex.Message);
        }
    }

    /// <summary>Центр памяти (трей-меню «Память»). Окно — класс U2 MemoryCenterWindow.</summary>
    public void OpenMemoryCenter()
    {
        if (MemoryStore == null)
        {
            Notifier.ShowInfo(TT("L_Toast_Memory", "Память"), TT("L_Toast_MemoryOffEnable", "Память выключена — включите её в настройках"));
            return;
        }
        try
        {
            if (_memoryCenterWindow != null)
            {
                _memoryCenterWindow.Activate();
                return;
            }
            var w = new MemoryCenterWindow();
            w.Initialize(MemoryStore, MemoryRecall, Logger);
            w.Closed += (s, e) => _memoryCenterWindow = null;
            _memoryCenterWindow = w;
            w.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("OpenMemoryCenter failed", ex, "Memory");
            Notifier.ShowWarning(TT("L_Toast_Memory", "Память"), ex.Message);
        }
    }

    /// <summary>Текущий display-name спикера: ручной алиас per-meeting или null.</summary>
    public string? GetSpeakerAlias(string? speakerId)
    {
        if (string.IsNullOrEmpty(speakerId)) return null;
        lock (_speakerAliases)
            return _speakerAliases.TryGetValue(speakerId, out var name) ? name : null;
    }

    /// <summary>
    /// Ручной алиас спикера (чип → «Назначить имя…»): живёт до конца встречи
    /// (спека PERSON — только ручные алиасы, без биометрии) + персона в сторе.
    /// </summary>
    public void SetSpeakerAlias(string speakerId, string name)
    {
        if (string.IsNullOrWhiteSpace(speakerId)) return;
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;
        lock (_speakerAliases) _speakerAliases[speakerId] = trimmed;
        try
        {
            var mid = _currentMemoryMeetingId;
            if (mid != null && MemoryStore != null)
            {
                // Персона привязана к встрече через заметку — полного remap-API по спеке нет.
                var p = new MemoryPerson
                {
                    Name = trimmed,
                    Notes = $"speaker {speakerId} @ meeting {mid.Value}",
                };
                var pid = MemoryStore.UpsertPerson(p);
                Logger.Info($"Memory: speaker alias {speakerId}→\"{trimmed}\" (person id={pid})", "Memory");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Memory: person upsert failed: {ex.Message}", "Memory");
        }
        // Подсветить новое имя во всех уже показанных пузырях этого спикера.
        try
        {
            IEnumerable<QaPair> items = _overlay?.History ?? Enumerable.Empty<QaPair>();
            foreach (var p in items)
                if (p.SpeakerId == speakerId)
                    p.SpeakerAlias = trimmed;
        }
        catch { }
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
            UiText.Lang = lang; // Core-строки (роли, подписи списка, статусы) не видят словари — язык через ambient.
            var uri = new Uri($"Resources/Strings.{lang}.xaml", UriKind.Relative);
            var newDict = new ResourceDictionary { Source = uri };

            var existing = Enumerable.FirstOrDefault(Current.Resources.MergedDictionaries,
                d => d.Source != null && d.Source.OriginalString.Contains("Resources/Strings."));
            if (existing != null)
                Current.Resources.MergedDictionaries.Remove(existing);
            Current.Resources.MergedDictionaries.Add(newDict);
            // Трей-меню построено из TT один раз — перечитываем тексты при смене языка.
            if (_trayItems != null)
                foreach (var (item, key, fb) in _trayItems) item.Text = TT(key, fb);
            // Статус/плашка пустого состояния пишутся как .Text (не DynamicResource) — пересчитываем.
            RefreshOverlayStatus();
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
        // Караоке-оверлей: горячее применение + показать/скрыть по Enabled.
        // Окно переиспользуемо — Hide(), не Close(); стелс/click-through меняет сам ApplyConfig.
        if (_karaoke != null)
        {
            _karaoke.ApplyConfig(Config.Karaoke);
            if (Config.Karaoke.Enabled && !_karaoke.IsShown) _karaoke.Show();
            else if (!Config.Karaoke.Enabled && _karaoke.IsShown) _karaoke.Hide();
        }

        // Экран OCR: включение/выключение и пороги применяются на лету (пороги наблюдатель
        // читает из живого Config.ScreenOcr, здесь — только старт/стоп).
        SyncScreenOcrWatcher();

        // Значок трея: скрытие/возврат применяется на лету, без рестарта.
        TrayIcon.Visible = !Config.Ui.HideTrayIcon;
        ApplyEndpointModeLive();   // ДО UpdateConfig: в ручном режиме порог тишины применится из конфига
        AudioEngine.UpdateConfig(Config.Audio);
        Logger.Configure(Config.Logging);
        RecreateSttEngineIfChanged();   // горячая замена движка распознавания, если его сменили
        SyncSpeakerDiarizer();          // то же для диаризации: вкл/выкл и бэкенд на лету
        // Память включается без рестарта: юзер поставил Enabled и сохранил настройки —
        // стор/воркер ещё не созданы (OnStartup их пропустил). Инициализация тяжёлая
        // (SQLite + lazy embedder) → в фоне, чтобы не морозить UI сохранения; идемпотентно.
        if (Config.Memory.Enabled && MemoryStore == null && !_shuttingDown)
        {
            Logger.Info("Memory enabled in settings — starting init in background", "Memory");
            _ = Task.Run(EnsureMemoryServices);
        }
        // Симметрично: память выключили в настройках — гасим подсистему на лету,
        // той же последовательностью, что и на выходе (воркер → поля → dispose в фоне).
        else if (!Config.Memory.Enabled && MemoryStore != null)
        {
            Logger.Info("Memory disabled in settings — tearing down services", "Memory");
            TeardownMemoryServices();
        }
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
                    Notifier.ShowInfo(TT("L_Toast_Opacity", "Прозрачность"), string.Format(TT("L_Toast_OpacityFmt", "Окно: {0:P0}"), Config.Ui.WindowOpacity));
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
                case "karaoke":
                    ToggleKaraoke();
                    break;
                case "screenocr":
                    ToggleScreenOcr();
                    break;
                case "screenocr_once":
                    RecognizeScreenNow();
                    break;
                case "showtray":
                    ShowTrayIcon();
                    break;
                case "askmemory":
                    OpenAskMemoryWindow();
                    break;
                case "memtoggle":
                    ToggleMeetingMemory();
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
                // Start() в фоне: создание WasapiLoopbackCapture/waveInOpen — синхронный
                // COM, на битом аудиодрайвере блокирует UI-поток до watchdog-kill.
                _ = Task.Run(() =>
                {
                    try { AudioEngine.Start(); Logger.Info("Audio resumed", "Audio"); }
                    catch (Exception ex) { Logger.Error("Failed to resume audio", ex, "Audio"); }
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to resume audio", ex, "Audio");
            }
            RefreshOverlayStatus();
            _overlay?.RefreshMicMuteButton();
            _overlay?.RefreshLlmPauseButton();
            Notifier.ShowInfo(TT("L_Toast_PauseOff", "Пауза снята"), TT("L_Toast_PauseOffBody", "Захват аудио возобновлён"));
        }
        else
        {
            IsPaused = true;
            // Флашим открытые фразы ДО остановки захвата: иначе недоговорённая фраза
            // замерзает в буфере на всю паузу и после возобновления уезжает в STT одним
            // куском-переростком (живой лог: фраза 27с висела минуту → чанк 57.4с).
            FlushOpenUtterances();
            // Stop() уходит в фон: NAudio StopRecording джойнит capture-поток, который
            // на зависшем виртуальном аудиодрайвере (VM) может не выйти — UI-поток
            // блокировался насмерть, и UiWatchdog убивал приложение (2026-10-01).
            _ = Task.Run(() =>
            {
                try
                {
                    AudioEngine.Stop();
                    Logger.Info("Audio paused", "Audio");
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to pause audio", ex, "Audio");
                }
            });
            RefreshOverlayStatus();
            _overlay?.RefreshMicMuteButton();
            Notifier.ShowInfo(TT("L_Toast_Pause", "Пауза"), TT("L_Toast_PauseBody", "Захват аудио остановлен"));
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

    /// <summary>
    /// «Мик выкл» (кнопка микрофона на нижней панели): глушит ТОЛЬКО mic-канал без остановки
    /// захвата (см. AudioCaptureEngine.MicMuted). Динамик продолжает транскрибироваться.
    /// Полная пауза захвата — TogglePause (Ctrl+Shift+P).
    /// </summary>
    public void ToggleMicMute()
    {
        Config.Ui.MicMuted = !Config.Ui.MicMuted;
        AudioEngine.MicMuted = Config.Ui.MicMuted;
        Logger.Info($"Mic {(Config.Ui.MicMuted ? "MUTED" : "unmuted")} (микрофон {(Config.Ui.MicMuted ? "выкл" : "вкл")})", "App");
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
            if (!buffer.Flush(out var wav, out var info) || wav.Length == 0) return;
            // Тот же путь, что у чанков VAD в MainLoop: очередь + пульс маркера в оверлее.
            // Info нужна только loopback-ветке (диаризация по стриму) — для mic default.
            var diarInfo = source == "loopback" ? ToDiarStreamInfo(info) : default;
            if (writer.TryWrite(new ChunkItem(wav, source, diarInfo)))
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
            RefreshOverlayStatus();
            _overlay?.RefreshLlmPauseButton();
            Notifier.ShowInfo(TT("L_Toast_LlmResumed", "LLM возобновлён"), TT("L_Toast_LlmResumedBody", "Вопросы снова отправляются в LLM"));
        }
        else
        {
            IsLlmPaused = true;
            Logger.Info("LLM paused (audio still recording)", "App");
            RefreshOverlayStatus();
            _overlay?.RefreshLlmPauseButton();
            Notifier.ShowInfo(TT("L_Toast_LlmPaused", "Пауза LLM"), TT("L_Toast_LlmPausedBody", "Вопросы не отправляются в LLM, аудио продолжает записываться"));
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

    // (item, ключ, RU-фолбэк) — тексты перечитываются в ApplyLanguage.
    private readonly List<(ToolStripMenuItem item, string key, string fallback)> _trayItems = new();

    private void SetupTray()
    {
        TrayIcon = new NotifyIcon
        {
            Text = StealthModeService.DisplayName,
            // Значок может быть скрыт настройкой HideTrayIcon — возврат хоткеем ShowTrayIcon.
            Visible = !Config.Ui.HideTrayIcon
        };
        TrayIcon.Icon = LoadTrayIcon();

        var menu = new ContextMenuStrip();
        // Тексты держим в _trayItems — ApplyLanguage перечитывает их при смене языка.
        var trayItem = (string key, string fallback, EventHandler onClick) =>
        {
            var it = new ToolStripMenuItem(TT(key, fallback));
            it.Click += onClick;
            _trayItems.Add((it, key, fallback));
            return it;
        };
        var showItem = trayItem("L_Tray_Show", "Показать оверлей", (s, e) => { Logger.Debug("Tray: Show overlay", "UI"); ShowOverlay(); });
        var hideItem = trayItem("L_Tray_Hide", "Скрыть оверлей", (s, e) => { Logger.Debug("Tray: Hide overlay", "UI"); HideOverlay(); });
        var settingsItem = trayItem("L_Tray_Settings", "Настройки", (s, e) => { Logger.Debug("Tray: Open settings", "UI"); OpenSettings(); });
        var logsItem = trayItem("L_Tray_Logs", "Показать логи", (s, e) => { Logger.Debug("Tray: Show logs", "UI"); ShowLogWindow(); });
        var meetingsItem = trayItem("L_Tray_Meetings", "История встреч", (s, e) => { Logger.Debug("Tray: Meeting history", "UI"); Dispatcher.Invoke(ShowMeetingHistory); });
        var pauseAudioItem = trayItem("L_Tray_PauseAudio", "Пауза захвата аудио", (s, e) =>
        {
            Logger.Debug("Tray: Toggle audio pause", "UI");
            Dispatcher.Invoke(TogglePause);
        });
        var pauseLlmItem = trayItem("L_Tray_PauseLlm", "Пауза отправки в LLM", (s, e) =>
        {
            Logger.Debug("Tray: Toggle LLM pause", "UI");
            Dispatcher.Invoke(ToggleLlmPause);
        });
        var clearHistoryItem = trayItem("L_Tray_ClearHistory", "Очистить историю", (s, e) =>
        {
            Logger.Debug("Tray: Clear history", "UI");
            _overlay?.ClearHistory();
        });
        var memoryItem = trayItem("L_Tray_Memory", "Память", (s, e) => { Logger.Debug("Tray: Open memory center", "UI"); Dispatcher.Invoke(OpenMemoryCenter); });
        var exitItem = trayItem("L_Tray_Exit", "Выход", (s, e) => { Logger.Info("Tray: Exit requested", "UI"); Shutdown(); });

        menu.Items.Add(showItem);
        menu.Items.Add(hideItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(meetingsItem);
        menu.Items.Add(memoryItem);
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
                if (AudioEngine.TryGetChunk(out var wav, out var chunkInfo))
                {
                    // Info — уже в шкале diar-стрима (baseline вычтен здесь, у enqueue):
                    // воркер и IdentifyRange работают с item.Info напрямую.
                    var item = new ChunkItem(wav, "loopback", ToDiarStreamInfo(chunkInfo));
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

                    // Диаризация loopback запускается ПАРАЛЛЕЛЬНО STT (~30ms vs ~1.4s) — оверхед ~0.
                    // Ни один из путей не бросает; результат нужен только после STT+Clean.
                    // Поле читаем в локальную переменную: hot-swap может заменить diarizer между чанками.
                    // Nemotron — стрим: IdentifyRange по границам речи из ChunkInfo,
                    // индексы уже в шкале стрима.
                    var diarizer = _speakerDiarizer;
                    var sampleRate = Config.Audio.SampleRate;
                    var info = chunkItem.Info;
                    Task<SpeakerIdentificationResult?>? speakerTask = null;
                    if (source == "loopback" && diarizer != null && info.EmitSample > 0)
                    {
                        // Info=default (EmitSample==0): чанк без метаданных — identify невозможен.
                        // Тайминг identify под спек-метрики (identify_range_ms);
                        // segs в результате нет — внутренний Debug NemotronDiarizer
                        // уже пишет diar_frames по этому же вызову.
                        speakerTask = Task.Run(() =>
                        {
                            var t0 = Stopwatch.GetTimestamp();
                            var r = diarizer.IdentifyRange(info.OnsetSample, info.SpeechEndSample, sampleRate);
                            Logger.Debug($"diar identify_range_ms={Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} label={r?.SpeakerId ?? "-"}", "Speaker");
                            return r;
                        }, ct);
                    }

                    var sttStart = DateTime.Now;
                    string? text = null;
                    // Пословные таймкоды (только GigaAM loopback) — для меток
                    // [S1]/[S2] внутри строки; null для остальных движков/каналов.
                    IReadOnlyList<BrainstormBuddy.Stt.SttWordSpan>? timedWords = null;
                    bool sttError = false;
                    try
                    {
                        await Dispatcher.InvokeAsync(() => _overlay?.SetTimelineState(TimelineState.Sending));
                        var engine = SttEngine;
                        // In-flight счётчик — статус «Распознаю речь…» (OverlayStatusKind.Transcribing).
                        Interlocked.Increment(ref _sttInFlight);
                        RefreshOverlayStatus();
                        try
                        {
                            if (source == "loopback" && engine is BrainstormBuddy.Stt.IGigaamSttEngine gig)
                            {
                                // GigaAM выдаёт пословные CTC-таймкоды — они нужны
                                // внутричанковой разметке [S1]/[S2] ниже.
                                var tt = await gig.TranscribeTimedAsync(wav, ct);
                                text = tt?.Text;
                                timedWords = tt?.Words;
                            }
                            else
                            {
                                text = await engine.TranscribeAsync(wav, ct);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _sttInFlight);
                            RefreshOverlayStatus();
                        }
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
                        // speakerTask не await'им: IdentifyAsync не бросает и завершится сам
                        // (~30ms); await здесь только задержал бы обработку следующего чанка.
                        _ = speakerTask;
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

                    // speakerTask запущен до STT и к этому моменту давно завершён (~30ms ≪ ~1.4s)
                    // — await фактически бесплатен. Результат нужен ДО agg.Push: у склеенной
                    // реплики метка берётся с последнего слившегося чанка (обрыв на союзе —
                    // это тот же говорящий договаривает). null = identify не смог → метка из
                    // _lastLoopbackSpeakerId или дефолтный [Динамик].
                    SpeakerIdentificationResult? spk = null;
                    if (speakerTask != null)
                    {
                        spk = await speakerTask;
                        if (spk is { } res)
                        {
                            _lastLoopbackSpeakerId = res.SpeakerId;
                            // Эпоха стрима на момент опознавания: ресинк/Reset
                            // рестартит нумерацию S<n>, метку старой эпохи ниже не используем.
                            _lastLoopbackSpeakerEpoch = diarizer?.StreamEpoch ?? -1;
                            if (res.IsNewSpeaker)
                                Logger.Info($"New speaker {res.SpeakerId} (sim {res.Similarity:F2})", "Speaker");
                        }
                    }

                    // Внутричанковая разметка спикеров: чанк резан по
                    // хронологическим спанам IdentifyRange (спаны покрывают чанк
                    // без дыр). Источник границ по убыванию точности:
                    // пословные CTC-таймкоды → аудио-сплит по спану + STT куска →
                    // грубая пропорция по длительности. Multi-speaker only: у
                    // одного спана обычной метки доминанта достаточно.
                    var markedText = text;
                    // STT может выдать текст с '[' в начале («[музыка]») —
                    // поэтому ниже проверяем именно '[S<цифра>', а не любую скобку.
                    if (spk?.Spans is { Count: > 0 } spans && Config.SpeakerDiarization.Enabled)
                    {
                        var marked = await TryBuildSpeakerMarkup(spans, info, timedWords, wav,
                            source, sampleRate, text, ct);
                        if (marked != null)
                            markedText = marked;
                    }

                    // Вторичный сигнал темпа для адаптивного эндпойнтинга (симв/с завершённого чанка).
                    (source == "mic" ? _micAdaptive : _loopbackAdaptive)?.NoteTranscript(text.Length, audioSeconds);

                    // Семантическая склейка (semantic-режим): придержать оборванную на союзе/предлоге
                    // мысль до следующего фрагмента того же канала. Ограничено maxHold — не залипает.
                    var agg = source == "mic" ? _micAgg : _loopbackAgg;
                    if (agg != null)
                    {
                        var ready = agg.Push(markedText);
                        if (ready == null)
                        {
                            Logger.Debug($"Semantic hold [{source}]: '{text}' — ждём продолжения", "Worker");
                            await Dispatcher.InvokeAsync(() => _overlay?.SetTimelineState(TimelineState.Idle));
                            continue;
                        }
                        markedText = ready; // склейка несёт метки только из готовых чанков
                        text = ready;
                    }

                    // Label text by source: [Микрофон] for mic; loopback → [S<n>]/[S?] когда
                    // диаризация включена и спикер опознан, иначе [Динамик]. Enabled-чек нужен:
                    // _lastLoopbackSpeakerId мог устареть (выключили фичу на лету). S-метки
                    // идут в labeledText → в LLM-контекст и историю (там и нужны).
                    string? speakerId = source == "loopback" && Config.SpeakerDiarization.Enabled
                        ? spk?.SpeakerId
                          // Фолбэк на последнюю метку — только той же эпохи стрима:
                          // после Reset/авто-ресинка S-нумерация новая, старая метка мусор.
                          ?? (diarizer != null && diarizer.StreamEpoch == _lastLoopbackSpeakerEpoch
                              ? _lastLoopbackSpeakerId : null)
                        : null;
                    var label = source == "mic" ? TT("L_Label_Mic", "[Микрофон]") : (speakerId != null ? $"[{speakerId}]" : TT("L_Label_Speaker", "[Динамик]"));
                    // Если markedText начинается с метки спикера '[S<цифра>…]'
                    // (внутричанковая разметка этого чанка ИЛИ склеенная через
                    // агрегатор), внешнюю метку доминанта не дублируем. Проверка
                    // именно '[S<цифра>', не просто '[': STT может вернуть
                    // текст вроде «[музыка]», метку канала терять нельзя.
                    var hasInnerMarkup = HasLeadingSpeakerTag(markedText);
                    var labeledText = hasInnerMarkup ? markedText : $"{label} {markedText}";

                    // === STT-РЕКА: показываем вопрос СРАЗУ, ответ LLM догонит асинхронно ===
                    // AddSendingItem создаёт пузырь с «ожидание» и сам маршалит в UI-поток, возвращая QaPair.
                    // «LLM выкл» (чистый транскрибатор): пузырь сразу без блока ответа — ни строки
                    // «ожидание», ни пометок, и задача в LLM-очередь не ставится вовсе.
                    bool llmOff = Config.Ui.LlmDisabled;
                    var pair = _overlay?.AddSendingItem(labeledText, audioSeconds, chunkStart, sttStart, sttEnd, sttMs, transcriptOnly: llmOff, speakerId: speakerId);
                    // Караоке-лента: та же реплика, но БЕЗ метки — роль по каналу
                    // ([Микрофон] → "user", динамик → "question"). Фильтрация Show* — внутри PushLine.
                    // При опознанном спикере префиксуем текст: «S1: …»; у
                    // внутричанковой разметки свои метки — «S1: [S1]…» не пишем.
                    _karaoke?.PushLine(source == "mic" ? "user" : "question",
                        source != "mic" && speakerId != null && !hasInnerMarkup ? $"{speakerId}: {text}" : markedText);
                    // Краш-журнал: та же роль, БЕЗ фильтров Show* — реплика на диске сразу.
                    // speaker сохраняется отдельным полем jsonl; внутричанковые метки — в тексте.
                    AppendJournalWithMemory(source == "mic" ? "user" : "question", markedText, speaker: speakerId);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        RefreshOverlayStatus();
                        _overlay?.SetTimelineState(TimelineState.Idle);
                    });

                    if (pair == null) continue; // оверлей закрыт
                    Logger.Info($"Overlay emitted [{source}] chunk #{totalProcessed} pre_llm_ms={(DateTime.Now - chunkStart).TotalMilliseconds:F0}", "Worker");

                    if (llmOff)
                    {
                        Logger.Info($"LLM disabled — chunk #{totalProcessed} показан как чистый транскрипт", "Worker");
                        continue;
                    }

                    if (IsLlmPaused)
                    {
                        _overlay?.MarkAnswered(pair, TT("L_Mark_LlmPaused", "⏸ LLM на паузе"), DateTime.Now, 0);
                        Logger.Info($"LLM paused — chunk #{totalProcessed} показан без ответа", "Worker");
                        continue;
                    }

                    // pre_llm_ms залогирован выше в "Overlay emitted" (до веток llmOff/paused).
                    // Развязка STT↔LLM: задача ответа уходит в ОТДЕЛЬНУЮ очередь; STT-воркер НЕ ждёт LLM
                    // и сразу берёт следующий чанк. Ответ подставит answer-воркер в этот же пузырь.
                    var job = new AnswerJob(pair, labeledText, text, source, sttMs, audioSeconds, chunkStart);
                    if (!answerWriter.TryWrite(job))
                    {
                        _overlay?.MarkFailed(pair, TT("L_Mark_LlmBehind", "LLM не успевает (очередь ответов переполнена)"), DateTime.Now, 0);
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
                    // «LLM выкл» щёлкнули, пока задача уже стояла в очереди: помечаем
                    // пузырь нейтральным статусом «LLM выключен» — видно, что реплика
                    // дошла, но ответа не будет (раньше закрывали пустой строкой —
                    // пузырь выглядел «зависшим» при вкл→выкл→вкл).
                    if (Config.Ui.LlmDisabled) { _overlay?.MarkAnswered(pair, T("L_LlmDisabledMark"), DateTime.Now, 0); continue; }
                    if (IsLlmPaused) { _overlay?.MarkAnswered(pair, TT("L_Mark_LlmPaused", "⏸ LLM на паузе"), DateTime.Now, 0); continue; }
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

                        // Memory-директивы в ответах агентов: применить + вырезать до показа
                        // (мульти-агентный ответ тоже попадает в пузырь/караоке/журнал).
                        var visible = new List<AgentResponse>();
                        foreach (var r in responses.Where(r => !r.IsSilent))
                        {
                            var rc = (await ProcessMemoryDirectivesAsync(r.Text, ct)).Clean;
                            if (!string.Equals(rc, r.Text, StringComparison.Ordinal)
                                ? !string.IsNullOrWhiteSpace(rc)
                                : true)
                                visible.Add(rc == r.Text ? r : r with { Text = rc });
                        }
                        if (visible.Count > 0)
                        {
                            _overlay?.ShowMultiAgent(pair, visible);
                            // Караоке: агрегированный ответ агентов одной строкой-ответом.
                            _karaoke?.PushLine("answer", string.Join("\n", visible.Select(r => r.Text)));
                            AppendJournalWithMemory("answer", string.Join("\n", visible.Select(r => r.Text)));
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
                    var stream = Config.Advanced.StreamLlmAnswers;
                    var gotDelta = false;
                    if (stream) _karaoke?.BeginStreamLine("answer");
                    // Memory recall + правила директив памяти — общий билдер для всех LLM-путей.
                    var sysPrompt = await BuildSystemPromptWithMemoryAsync(Config.Advanced.SystemPrompt, job.Text, ct);
                    var askResult = await ApiClient.AskAsync(labeledText, sysPrompt, Config.Advanced.MaxResponseTokens, historySnapshot, ct,
                        stream ? (Action<string>)(d =>
                        {
                            gotDelta = true;
                            _overlay?.AppendAnswerDelta(pair, d);
                            _karaoke?.AppendToStreamLine(d);
                        }) : null);
                    // Memory-директивы: применить (тост) и вырезать до финала стрим-строки —
                    // сырой текст с [[MEMORY_*]] уже мог отстримиться в ленту.
                    var mem = await ProcessMemoryDirectivesAsync(askResult?.Content, ct);
                    var answer = mem.Clean;
                    // Фикс «записать в память → промолчала»: голосовая/текстовая команда
                    // с ответом только из [[MEMORY_*]] — Clean пуст, но Applied>0 = успех.
                    if (string.IsNullOrWhiteSpace(answer) && mem.Applied.Count > 0)
                        answer = TT("L_Answer_MemorySaved", "✔ записано в память");
                    var memStripped = !string.Equals(answer, askResult?.Content, StringComparison.Ordinal);
                    FinalizeKaraokeStreamLine(stream, gotDelta, askResult?.IsSilent == true, memStripped, answer);
                    var askEnd = DateTime.Now;
                    var askMs = (askEnd - askStart).TotalMilliseconds;

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
                        if (!gotDelta) _karaoke?.PushLine("answer", answer!);
                        AppendJournalWithMemory("answer", answer!); // финальный текст — и при стриминге, и без
                        await Dispatcher.InvokeAsync(() => { _overlay?.SetTimelineState(TimelineState.Received); UpdateTokenDisplay(); }); // зелёный: LLM ответил
                    }
                    else if (askResult?.IsSilent == true || memStripped)
                    {
                        // Осознанное молчание модели (finish_reason=stop при пустом
                        // content): промпт суфлёра сам требует тишины на small talk.
                        // Нейтральный статус как у мульти-агентов, НЕ красная плашка.
                        Logger.Info($"LLM silent (stop, empty content) in {askMs:F0}ms", "Answer");
                        _totalPromptTokens += askResult?.PromptTokens ?? 0;
                        _totalCompletionTokens += askResult?.CompletionTokens ?? 0;
                        QaLogger?.Log(labeledText, "[молчание]", "SILENT", job.SttMs, askMs, job.AudioSeconds, Config.Api.ChatModel);
                        _overlay?.MarkAnswered(pair, T("L_ModelSilent"), askEnd, askMs);
                        await Dispatcher.InvokeAsync(UpdateTokenDisplay);
                    }
                    else if (!string.IsNullOrEmpty(askResult?.Error))
                    {
                        // Реальная ошибка провайдера (401/402/404/429/сеть) — показываем КОД+текст, не generic.
                        Logger.Warn($"LLM error: {askResult!.Error}", "Answer");
                        QaLogger?.Log(labeledText, $"[ERROR: {askResult.Error}]", "FAIL", job.SttMs, askMs, job.AudioSeconds, Config.Api.ChatModel);
                        _overlay?.MarkFailed(pair, askResult.Error, askEnd, askMs);
                    }
                    else if (askResult?.Canceled == true)
                    {
                        // Запрос отменён токеном (пауза/стоп/закрытие) — НЕ ошибка:
                        // никакой плашки, цикл умрёт сам на следующем ReadAllAsync(ct).
                        Logger.Info($"Ask cancelled (token) in {askMs:F0}ms", "Answer");
                        continue;
                    }
                    else
                    {
                        // 200, но контент пуст и это НЕ осознанное молчание — лимит
                        // съеден рассуждениями (finish_reason=length) даже после
                        // авто-ретрая с 1024. Юзер лимит из GUI не меняет — просто
                        // «модель не ответила», без мёртвого совета про токены.
                        Logger.Warn("Empty answer from LLM (200, no content, limit exhausted)", "Answer");
                        QaLogger?.Log(labeledText, "[пустой ответ]", "EMPTY", job.SttMs, askMs, job.AudioSeconds, Config.Api.ChatModel);
                        _overlay?.MarkFailed(pair, T("L_ModelNoAnswer"), askEnd, askMs);
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

            // Оверлей закрываем ПЕРВЫМ, до dispose движков: его 500мс-таймеры
            // (OnBlinkTick/OnTimelineTick) иначе тикают в уже полумёртвое состояние
            // и на разрушении диспетчера бросают NRE → DispatcherUnhandledException
            // метит сессию как crash, а юзер видит «падение» при выходе.
            try { _overlay?.Close(); } catch { /* не мешаем выходу */ }

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
            _karaoke?.Close();
            // Экран OCR: сначала глушим наблюдателя (он держит фоновый тик и нативный движок
            // Tesseract), затем освобождаем движки и захват — нативная память не должна
            // оставаться до убийства процесса.
            try { StopScreenOcr(); } catch (Exception ex) { Logger?.Warn($"ScreenOcr stop failed: {ex.Message}", "ScreenOcr"); }
            _ocrErrorTimer?.Stop();
            _ocrIndicator?.Close();
            _ocrIndicator = null;
            _screenRecognizer?.Dispose();
            _screenCapturer?.Dispose();
            DetachLoopbackFeed();
            _speakerDiarizer?.Dispose();
            Logger?.Debug("Speaker diarizer disposed", "Speaker");
            LogWindow?.Close();

            // Память: финализация встречи (extract-джоба), останов воркера, dispose стора —
            // та же последовательность, что при live-выключении в настройках.
            TeardownMemoryServices();

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
            // Снимаем маркер живой сессии ПОСЛЕДНИМ шагом штатного выхода: если он
            // останется на диске — этот запуск умер без '=== exiting ===', и следующий
            // старт запишет это в crash.log.
            try { if (_sessionSentinelPath.Length > 0) File.Delete(_sessionSentinelPath); }
            catch { /* не мешаем выходу */ }
            // Чистый выход: останавливаем heartbeat и снимаем heartbeat.txt — тогда
            // свежий маркер при следующем старте однозначно значит «умерли нештатно».
            try { _crashGuard?.Dispose(); }
            catch { /* не мешаем выходу */ }
            base.OnExit(e);
        }
    }
}

// Info: метаданные чанка. Для loopback — уже сдвинутые в шкалу diar-стрима (см. ToDiarStreamInfo),
// для mic — default (диаризация по mic не работает).
internal readonly record struct ChunkItem(byte[] Wav, string Source, ChunkInfo Info = default);

// Задача ответа LLM: STT-воркер кладёт сюда после показа вопроса, answer-воркер отвечает
// и подставляет ответ в Pair (тот самый пузырь). Развязка STT↔LLM (транскрипция «рекой»).
internal sealed record AnswerJob(
    QaPair Pair, string LabeledText, string Text, string Source,
    double SttMs, double AudioSeconds, DateTime ChunkStart);
