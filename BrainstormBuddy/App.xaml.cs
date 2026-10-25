using System.Diagnostics;
using System.Text.Json;
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
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Core.Backup;
using BrainstormBuddy.Context;
using BrainstormBuddy.Pdf;

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
    static App()
    {
        // WPF по умолчанию рисует выделение TextBox полупрозрачным адорнером
        // поверх текста: SelectionTextBrush игнорируется, а SelectionOpacity=1
        // даёт непрозрачный прямоугольник без букв. В истории встреч нужен
        // читаемый тёмный текст на золотой подсветке — переключаем отрисовку
        // выделения на текстовую (поддерживается TextBox/PasswordBox).
        AppContext.SetSwitch(
            "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);
    }

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
    // Краш-безопасный журнал живого транскрипта (meetings/*.jsonl). Читает его
    // MeetingHistoryWindow + поисковый MeetingSearchService (§7).
    public MeetingTranscriptStore? MeetingJournal { get; private set; }
    /// <summary>Поиск по журналам прошлых встреч (§7): ScanAsync поверх
    /// MeetingJournal — для окна истории и для подмешивания фрагментов в промпт.</summary>
    public MeetingSearchService? MeetingSearch { get; private set; }

    // ===== Вложения чата (feature-chat-attachments): локальный стор документов,
    // фоновая очередь импорта и воркер извлечения фактов. PdfOcr создаётся лениво
    // (держит нативный Tesseract) — грузится при первом прикреплённом PDF.
    private AttachmentStore? _attachmentStore;
    /// <summary>Стор вложений чата; null — инициализация упала на старте (логи: Attach).</summary>
    public AttachmentStore? Attachments => _attachmentStore;
    /// <summary>Юзер включил «Извлечь сразу факты в память» в последнем Commit пикера —
    /// армирует постановку fact-джобов для ещё линкующихся документов.</summary>
    internal bool AttachmentFactsRequested { get; set; }
    private AttachmentImportRunner? _importRunner;
    private AttachmentFactWorker? _factWorker;
    /// <summary>fact-job → id прогресс-тоста оверлея (jobId→toastId), чтобы обновлять
    /// «запрос N/M» и снимать тост в терминальном статусе. Живёт только в UI-потоке.</summary>
    private readonly Dictionary<long, int> _factToastIds = new();

    // ===== Изображения в чате (feature-chat-images, ТЗ .work/feature-chat-images):
    // фасад черновик/отправка/история картинок; null — стор не поднялся.
    /// <summary>Фасад «картинки в чате» для OverlayWindow/истории/настроек.</summary>
    public ChatImageFlow? ChatImages { get; private set; }
    /// <summary>Сервис определения поддержки изображений текущей моделью (ТЗ §8).</summary>
    internal BrainstormBuddy.Core.Images.IImageCapabilityService? ImageCapabilities { get; private set; }
    private PdfOcrService? _pdfOcr;
    /// <summary>Единственный экземпляр немодального пикера вложений (UI-поток);
    /// null — окно закрыто. Живая очередь при этом остаётся в runner'е.</summary>
    private AttachmentPickerWindow? _attachmentPicker;

    // ===== Память (спека memory.txt): SQLite-стор, recall, worker джоб =====
    // Всё создаётся только при Config.Memory.Enabled; сбой инициализации логируется
    // и НЕ ломает приложение (MemoryStore остаётся null → память молча выключена).
    public IMemoryStore? MemoryStore => _memoryStore;
    // Публикуется ПОСЛЕДНИМ шагом EnsureMemoryServices, когда весь memory-стек уже готов:
    // читатели (MemoryStore != null → память активна) не должны видеть стор без
    // recall/embedder/воркера — volatile для видимости между потоками.
    private volatile SqliteMemoryStore? _memoryStore;
    /// <summary>
    /// «Стар памяти опубликован» (или «памяти в этой сессии не будет»). Стар
    /// памяти идёт ПОСЛЕ старта MCP-хоста (SQLite + эмбеддеры, секунды), поэтому
    /// MCP-клиент, пришедший сразу после запуска приложения, не должен получать
    /// «memory not included» - он ждёт публикации стора (см. MemoryStoreForMcp).
    /// </summary>
    private readonly ManualResetEventSlim _memoryPublished = new(false);
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

    // ===== MCP =====
    /// <summary>Входящий хост (named pipe); null — MCP выключен или старт упал.</summary>
    public Core.Mcp.McpIncomingHost? McpHost { get; private set; }
    /// <summary>Менеджер исходящих MCP-серверов; создаётся всегда (дешёвый до первого вызова).</summary>
    public Core.Mcp.Outgoing.McpClientManager? McpOutgoing { get; private set; }
    public Core.Mcp.Outgoing.McpChatOrchestrator? McpChat { get; private set; }
    /// <summary>Встроенный поиск Buddy (факты+встречи) как инструменты модели;
    /// null — память выключена/не инициализирована.</summary>
    public Core.Mcp.Outgoing.BuiltinMemoryTools? BuiltinTools { get; private set; }
    /// <summary>Встроенные инструменты доступны модели: память и/или прошлые
    /// встречи включены отдельными настройками.</summary>
    public bool BuiltinToolsAvailable =>
        Config.Memory.BuiltinSearchTools &&
        ((Config.Memory.Enabled && MemoryRecall != null && MemoryStore != null) ||
         (Config.Memory.MeetingSearchEnabled && MeetingSearch != null));
    public Core.Mcp.McpPermissionStore? McpPermissions { get; private set; }
    /// <summary>Журнал входящего MCP-канала ({appData}/logs/mcp.log).</summary>
    public Core.Mcp.McpIncomingJournal? McpJournal { get; private set; }
    /// <summary>Входящий HTTP-ingress (Streamable HTTP + OAuth AS); null — HttpEnabled=false.</summary>
    public Core.Mcp.Http.McpHttpIngress? McpHttp { get; private set; }
    /// <summary>Фасад для HTTP-пути, когда pipe-хост не поднят (его Dispose — наш).</summary>
    private Core.Mcp.LocalMcpFacade? _httpFacade;
    /// <summary>
    /// Реальный запрос MCP-клиента (id подключения, UTC) — сигнал карточкам
    /// в настройках: статус «Подключено» появляется без переоткрытия окна.
    /// </summary>
    public event Action<string, DateTime>? McpRequestObserved;
    /// <summary>Фасады, к которым уже подписан App (дедуп по ссылке).</summary>
    private readonly List<Core.Mcp.IMcpFacade> _mcpActivityFacades = new();
    private int _funnelPort;
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

    // ===== Контекст встречи (спека «следующая встреча»): движок + пауза-буфер =====
    /// <summary>Сводка + дословный хвост + пайплайн сворачивания; null до инициализации
    /// журнала (OnStartup) — до неё контекстные пути просто пропускаются.</summary>
    private MeetingContextManager? _meetingContext;
    /// <summary>Реплики, признанные при LLM-паузе: скапливаются здесь и одним запросом
    /// уходят в LLM при снятии паузы (спека п.15). Не путать с аудио-pause: буферит
    /// ТЕКСТ после STT, не сэмплы.</summary>
    private readonly LlmPauseBuffer _pauseBuf = new();
    /// <summary>Хвост последнего прогона журнала/пары — для RetryAnswer по пузырю.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, AskRetryArgs> _askRetryArgs = new();

    /// <summary>Параметры повторного LLM-запроса по пузырю (кнопка «↻ повторить»):
    /// текст вопроса + назначение (определяет промпт-оверрайд и лейбл).
    /// MessageId/ImageTiles — снапшот отправленных картинок: повтор идёт с ТЕМ ЖЕ
    /// messageId и теми же файлами (ТЗ image-chat: ретрай не дублирует линковку).</summary>
    private sealed record AskRetryArgs(string Text, bool IsManual, bool IsScreen,
        string? MessageId = null, IReadOnlyList<ChatImageTileVm>? ImageTiles = null);

    // Публичные свойства-совместимость: за led-счётчиками (единый источник правды).
    public int TotalPromptTokens => _tokenLedger.MeetingPromptTokens;
    public int TotalCompletionTokens => _tokenLedger.MeetingCompletionTokens;
    public int TotalTokens => _tokenLedger.MeetingTokens;
    /// <summary>Фоновый расход «Документы и память» (факты/память/сжатие).</summary>
    public int BgTokens => _tokenLedger.BackgroundTokens;

    // §8.1 аудита 15.10: раздельный учёт расхода. «Встреча» — траты на ответы
    // пользователю; фон («Документы и память»: извлечение фактов, память, сжатие
    // контекста) идёт отдельным счётчиком — путать их нельзя. Логика вынесена в
    // Core (TokenLedger), чтобы её можно было покрыть unit-тестами.
    private readonly TokenLedger _tokenLedger = new();
    /// <summary>Факт уже показанного тоста «локальная обработка — 0 LLM-токенов»
    /// (одноразовое уведомление на сессию, не спамим на каждый файл).</summary>
    private bool _localZeroNoticeShown;

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
            // BUDDY_DATA_DIR — изолированный профиль данных (UI-проверки, второй
            // экземпляр). Без env — штатный %APPDATA%\BrainstormBuddy.
            var dataDirOverride = Environment.GetEnvironmentVariable("BUDDY_DATA_DIR");
            AppDataDir = !string.IsNullOrWhiteSpace(dataDirOverride)
                ? Path.GetFullPath(dataDirOverride)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BrainstormBuddy");
            Directory.CreateDirectory(AppDataDir);
            ConfigPath = Path.Combine(AppDataDir, "config.json");

            Logger = new LoggingService(AppDataDir);
            ErrorHandler = new ErrorHandlingService(Logger);

            // Трассировка вложений (Core.AttachLog): Debug.WriteLine в release не
            // доходит до журнала — заводим строки в app.log для диагностики §4/§14.
            AttachLog.Sink = line => Logger.Info(line, "Attach");

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

            // Отложенное восстановление профиля: swap целевого каталога возможен
            // только сейчас — AppDataDir/Logger уже есть, а ConfigLoader и все
            // stores ещё не открыты. Дальше по коду профиль уже «в работе».
            ApplyPendingProfileRestore();

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
            // Дефолт чистой установки: OpenCode Go (AppConfig: BaseUrl opencode.ai/zen/go, ChatModel deepseek-v4.1-flash).
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
            MeetingSearch = new MeetingSearchService(MeetingJournal);
            // §8: смысловая ветка поиска встреч на существующем E5-провайдере.
            // Провайдер публикуется позже (EnsureMemoryServices), поэтому функция
            // читает поле лениво; повторный attach после публикации запускает
            // фоновый до-эмбеддинг индекса.
            MeetingSearch.AttachEmbeddingProvider(() => MemoryEmbedder);
            Logger.Info($"MeetingJournal initialized at {MeetingJournal.Directory}", "UI");
            // Контекст встречи: сводка+хвост. Восстановление после аварийного
            // рестарта — из sidecar'а последней встречи (последний шанс, если
            // процесс убили между Append'ами).
            _meetingContext = new MeetingContextManager();
            TryRestoreContextFromLatestMeeting();
            try
            {
                _attachmentStore = new AttachmentStore(AppDataDir, Logger);
                // RecoverOnStartup() вызывается из конструктора стора; здесь только
                // перевод зависших fact-джобов в Interrupted (после аварийного выхода).
                _attachmentStore.MarkInterruptedJobs();
                Logger.Info($"AttachmentStore initialized at {_attachmentStore.RootDirectory}", "Attach");
                // Пережившие перезапуск задания фактов (Queued/Interrupted→Queued):
                // воркер доезжает очередь фоново, статусы летят в чипы по JobUpdated.
                try { EnsureFactWorker()?.Kick(); }
                catch (Exception ex2) { Logger.Warn($"FactWorker startup kick failed: {ex2.Message}", "Attach"); }
                // ===== Картинки в чате (ТЗ image-chat): тот же AttachmentStore
                // (миграция user_version 1→2 внутри), фасад черновика/отправки.
                // Кэпабилити-провайдер живёт отдельно — без стора фича выключается.
                try
                {
                    ChatImages = new ChatImageFlow(this, _attachmentStore);
                    var caps = TryCreateImageCapabilityService();
                    ChatImages.SetCapabilityService(caps);
                    ImageCapabilities = caps;
                    if (ApiClient is BrainstormBuddy.Ai.OpenAiClient oc && caps != null)
                        oc.SetImageCapabilityService(caps);
                        // ТЗ §8: каталог поддержки изображений подтягиваем в фоне
                        // при старте (rate-limit 24ч внутри сервиса не даст дёргать
                        // чаще раза в сутки). Иначе гейт «Unsupported не отправляем»
                        // никогда не увидит каталог: GetCached без refresh = Unknown.
                        if (caps != null)
                        {
                            var capKey = Core.Images.ImageCapabilityService.KeyFor(Config.Api);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var info = await caps.RefreshCatalogAsync(capKey, force: false, CancellationToken.None);
                                    Logger.Info($"image-capability catalog: {capKey.ModelId} -> {info.State} ({info.Source})", "ChatImg");
                                }
                                catch (Exception exc) { Logger.Warn($"image-capability catalog: {exc.Message}", "ChatImg"); }
                            });
                        }
                        ChatImages.RestoreDraftForCurrentMeeting();
                    Logger.Info("ChatImageFlow initialized", "ChatImg");
                }
                catch (Exception ex3) { Logger.Warn($"ChatImageFlow init skipped: {ex3.Message}", "ChatImg"); }
            }
            catch (Exception ex)
            {
                // Вложения - фича поверх чата: сбой стора не должен ломать приложение.
                Logger.Error("AttachmentStore init failed", ex, "Attach");
                _attachmentStore = null;
            }
            // ===== MCP: входящий stdio-хост (named pipe для McpHost.exe) и
            // исходящий менеджер (инструменты чужих серверов в чат). Оба не бросают —
            // при выключенном Config.Mcp.Enabled TryStart вернёт null.
            try
            {
                McpPermissions = new Core.Mcp.McpPermissionStore(AppDataDir);
                McpJournal = new Core.Mcp.McpIncomingJournal(
                    Path.Combine(AppDataDir, "logs"), mirror: Logger);
                McpHost = Core.Mcp.McpIncomingHost.TryStart(
                    Config.Mcp, AppDataDir, MeetingJournal!, MemoryStore, _attachmentStore, Logger,
                    McpJournal, memoryProvider: () => MemoryStoreForMcp());
                // Один журнал исходящего MCP на приложение: менеджер клиентов и
                // чатовый оркестратор пишут в него оба — записи связывает requestId.
                var mcpOutgoingJournal = new Core.Mcp.Outgoing.McpJournal(AppDataDir);
                McpOutgoing = new Core.Mcp.Outgoing.McpClientManager(
                    srv => string.IsNullOrEmpty(srv.SecretRef) ? null
                        : McpPermissions?.TryRevealSecret(srv.SecretRef),
                    Logger, journal: mcpOutgoingJournal);
                // Встроенный поиск Buddy: поиск MemoryRecall/MeetingSearch идёт по
                // closure — сервисы памяти поднимаются позже в OnStartup.
                BuiltinTools = new Core.Mcp.Outgoing.BuiltinMemoryTools(
                    searchMemory: (q, maxItems, budget, ct) =>
                    {
                        var recall = MemoryRecall;
                        return recall == null
                            ? Task.FromResult(new List<MemoryRecallHit>())
                            : recall.RecallAsync(new MemoryRecallQuery(
                                q,
                                Scope: Config.Memory.ActiveProjectId.HasValue ? MemoryScopeType.Project : null,
                                ScopeId: Config.Memory.ActiveProjectId,
                                MeetingId: _currentMemoryMeetingId,
                                MaxItems: maxItems,
                                MaxTokens: budget,
                                ExplicitSearch: true), ct);
                    },
                    searchMeetings: (q, options, ct) =>
                    {
                        var search = MeetingSearch;
                        return search == null
                            ? Task.FromResult(new MeetingSearchResult
                            {
                                Hits = Array.Empty<MeetingSearchHit>(),
                                Status = new MeetingSearchStatus { IndexEnabled = true, Syncing = true },
                            })
                            : search.SearchDetailedAsync(q, ct, options);
                    },
                    currentMeetingId: () => MeetingJournal?.CurrentId,
                    extraBudgetTokens: () => MemoryBudgets.Plan(
                        Config.Memory, Config.Api.ChatModel, Config.Api.BaseUrl,
                        reservedTokens: 4000).ExtraSearchTokens,
                    scopeLabel: () => Config.Memory.ActiveProjectId is { } pid
                        ? $"project #{pid}"
                        : _currentMemoryMeetingId is { } mid ? $"meeting #{mid}" : "all",
                    fallback: McpOutgoing, log: Logger,
                    memoryEnabled: () => Config.Memory.Enabled &&
                        MemoryRecall != null && MemoryStore != null,
                    meetingsEnabled: () => Config.Memory.MeetingSearchEnabled &&
                        MeetingSearch != null);
                // Чатовый оркестратор исходящих инструментов: живёт только когда
                // ApiClient умеет tools (OpenAiClient : IMcpToolLlm). Делит
                // McpClientManager с остальным кодом (единый кеш соединений).
                if (ApiClient is Core.Mcp.Outgoing.IMcpToolLlm toolLlm)
                {
                    McpChat = new Core.Mcp.Outgoing.McpChatOrchestrator(
                        // Выключенный общий MCP-тумблер убирает внешние серверы,
                        // но встроенный поиск Buddy остаётся доступен.
                        () => Config.Mcp.Enabled ? Config.Mcp.OutgoingServers
                                                 : Array.Empty<McpOutgoingServer>(),
                        toolLlm,
                        journal: mcpOutgoingJournal,
                        clients: McpOutgoing, transport: BuiltinTools, logger: Logger)
                    {
                        IsLlmPausedFunc = () => IsLlmPaused || Config.Ui.LlmDisabled,
                        IsMcpDisabledFunc = () => !Config.Mcp.Enabled && !BuiltinToolsAvailable,
                        BuiltinToolsEnabled = () => BuiltinToolsAvailable,
                        BuiltinExtraBudgetTokens = () => MemoryBudgets.Plan(
                            Config.Memory, Config.Api.ChatModel, Config.Api.BaseUrl,
                            reservedTokens: 4000).ExtraSearchTokens,
                        ContextWindowFunc = () => ContextBudgetResolver.InputBudget(
                            Config.Api.ChatModel, Config.Api.BaseUrl),
                        ApprovalFunc = ConfirmMcpToolCallAsync,
                        Progress = OnMcpToolProgress,
                    };
                }
                if (McpHost != null)
                    Logger.Info($"MCP incoming on pipe {McpHost.PipeName}", "Mcp");
                AttachMcpFacadeActivity(McpHost?.Facade);
                // Контекст для карточек подключений в настройках (права/канал/токен/пути).
                SettingsVm.McpPermissions = McpPermissions;
                SettingsVm.McpDataRoot = AppDataDir;
                SettingsVm.McpIncomingPipe = McpHost?.PipeName;
                // HTTP-ingress + туннель: fire-and-forget (bind/detect не блокируют старт).
                _ = TryStartMcpHttpAsync();
            }
            catch (Exception ex) { Logger.Error("MCP init failed", ex, "Mcp"); }

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
                // Не открываем настройки сами: старт приложения должен оставлять
                // только чат. Баннер объясняет проблему, кнопка ведёт в LLM-раздел.
                Logger.Warn("API key missing — showing overlay notice", "App");
                // Состояние, а не одноразовый вызов: UpdateConnectionNotice
                // пересобирает баннер на каждом health-событии и иначе скрыл бы его.
                _llmOk = false;
                _llmErrMsg = UiText.Tr(
                    "API-ключ не задан. Укажите ключ и модель в настройках — кнопка «Открыть настройки» ниже.",
                    "No API key configured. Set the key and model in settings — use the “Open settings” button below.");
                UpdateConnectionNotice();
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
    /// Живой стор памяти для MCP-фасада. Стар памяти идёт ПОСЛЕ старта
    /// MCP-хоста (инициализация SQLite + эмбеддеров занимает секунды), поэтому
    /// клиент, пришедший в первые секунды после запуска приложения, не должен
    /// получать «memory not included» - ждём публикации стора (до 15 c).
    /// Память, выключенная в конфиге, не ждём: сигнал уже установлен, ответ
    /// сразу. Ожидание синхронное, но ограничено одним клиентом (у каждого
    /// подключения своя задача обработки) и только на время старта.
    /// </summary>
    private IMemoryStore? MemoryStoreForMcp()
    {
        var m = MemoryStore;
        if (m != null) return m;
        if (!Config.Memory.Enabled) return null;
        _memoryPublished.Wait(TimeSpan.FromSeconds(15));
        return MemoryStore;
    }

    /// <summary>
    /// Применение маркера отложенного восстановления профиля (Настройки →
    /// «Восстановить из архива» в текущий профиль). Вызывается из OnStartup
    /// ДО ConfigLoader и любых stores — только здесь каталог профиля можно
    /// безопасно заменить swap'ом. Пароль архива живёт в DPAPI-маркере и
    /// в лог/сообщения не попадает. Метод не бросает: сбой оставляет
    /// исходный профиль нетронутым (RestoreAsync делает staging+swap), маркер
    /// остаётся на месте, а пользователь решает — повторить или отменить.
    /// </summary>
    private void ApplyPendingProfileRestore()
    {
        try
        {
            if (!DeferredProfileRestore.IsPending) return;
            var pending = DeferredProfileRestore.TryTake();
            if (pending == null) return;
            var (archivePath, destination, password) = pending.Value;
            Logger.Info(
                $"Pending profile restore: {archivePath} -> {destination}", "App");
            // Блокирующий запуск намеренный: UI ещё не поднят, а конфиг/stores
            // обязаны открыть уже восстановленный профиль.
            var result = Task.Run(() => PortableArchive.RestoreAsync(
                    archivePath, password, destination,
                    new ArchiveRestoreOptions { StoresClosed = true }))
                .GetAwaiter().GetResult();
            if (result.Restored)
            {
                DeferredProfileRestore.Clear();
                // logs/ в архив не входит — восстанавливаем каталог журнала,
                // иначе AppendAllText до конца сессии молча мимо файла.
                try { Directory.CreateDirectory(Logger.LogDirectory); } catch { }
                Logger.Info(
                    $"Profile restored from archive ({result.Manifest?.Entries.Count ?? 0} entries)" +
                    (result.BackupDirectory != null
                        ? $"; previous profile kept at {result.BackupDirectory}"
                        : ""),
                    "App");
                // Предупреждения восстановления (например, «модели пропущены»)
                // не теряем: пользователь увидит их в окне журнала.
                foreach (var w in result.Warnings)
                    Logger.Warn($"Profile restore: {w}", "App");
            }
            else
            {
                Logger.Warn(
                    $"Pending profile restore failed: {result.Failure} — {result.Error}", "App");
                ShowDeferredRestoreRetryChoice(result.Error);
            }
        }
        catch (Exception ex)
        {
            // Никогда не роняем запуск: исходный профиль цел, маркер не тронут.
            try { Logger.Warn($"Pending profile restore error: {ex.Message}", "App"); }
            catch { }
            try { ShowDeferredRestoreRetryChoice(ex.Message); } catch { }
        }
    }

    /// <summary>
    /// При неудаче отложенного восстановления спрашивает, оставить ли маркер
    /// на повтор при следующем запуске. Нативный MessageBox — до загрузки тем.
    /// </summary>
    private void ShowDeferredRestoreRetryChoice(string? error)
    {
        var retry = System.Windows.MessageBox.Show(
            UiText.Tr(
                "Не удалось восстановить профиль из архива." +
                (string.IsNullOrWhiteSpace(error) ? "" : $"\n\n{error}") +
                "\n\nПовторить попытку при следующем запуске?\n" +
                "«Нет» — отменить восстановление и продолжить с текущим профилем.",
                "Failed to restore the profile from the archive." +
                (string.IsNullOrWhiteSpace(error) ? "" : $"\n\n{error}") +
                "\n\nRetry on next launch?\n" +
                "«No» — cancel the restore and continue with the current profile."),
            StealthModeService.DisplayName,
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (retry != MessageBoxResult.Yes)
        {
            DeferredProfileRestore.Clear();
            Logger.Info("Pending profile restore cancelled by user", "App");
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
        // Открытый MCP-аппрувал при скрытии окна отклоняем — иначе tool-loop
        // завис бы на невидимой карточке до конца запроса.
        _overlay?.CancelMcpApproval();
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
    /// Разовое распознавание экрана для настроек («Распознать сейчас» в окне настроек —
    /// превью без постановки в очередь): снимок активного монитора → даунскейл → OCR → чистка.
    /// Выполняется в фоне. Хоткей Ctrl+Shift+N идёт другим путём — EnqueueScreenSnapshot.
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
    /// Снимок экрана для очереди хоткея: захват региона + даунскейл до MaxOcrWidth.
    /// Возвращает готовые для OCR пиксели и длительность захвата (мс). Без OCR —
    /// распознавание делает OcrPixelsAsync в насосе очереди.
    /// </summary>
    private Task<(PixelBuffer Pixels, double CaptureMs)> CaptureScreenPixelsAsync() => Task.Run(() =>
    {
        EnsureScreenOcrEngines();
        var sw = Stopwatch.StartNew();
        var region = MonitorEnumerator.CaptureBoundsFor(Config.ScreenOcr.MonitorIndex);
        var frame = _screenCapturer!.CaptureRegion(region);
        var captureMs = sw.Elapsed.TotalMilliseconds;
        var pixels = ImagePreprocessor.LimitWidth(frame.Pixels, Config.ScreenOcr.MaxOcrWidth);
        return (pixels, captureMs);
    });

    /// <summary>
    /// OCR готовых пикселей (уже снятых и уменьшенных) + чистка текста.
    /// Возвращает текст и длительность движка (мс). Бросает, если движок недоступен.
    /// </summary>
    private async Task<(string Text, double OcrMs)> OcrPixelsAsync(PixelBuffer pixels)
    {
        var recognizer = await EnsureScreenRecognizerReadyAsync();
        if (recognizer is null || recognizer.Availability != RecognizerAvailability.Available)
            throw new InvalidOperationException(recognizer?.UnavailabilityReason ?? UiText.Tr("OCR недоступен", "OCR unavailable"));

        var result = await recognizer.RecognizeAsync(pixels, RecognitionOptions.Default);
        var text = ScreenTextWatcher.CleanupText(result.RawText);
        return (text, result.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Хоткей «распознать сейчас» (по умолчанию Ctrl+Shift+N): каждое нажатие ставит
    /// снимок в очередь — запрос не теряется даже при повторном нажатии во время
    /// предыдущего прохода (аудит 2026-10-14 §2). См. App.ScreenOcrQueue.cs.
    /// </summary>
    public void RecognizeScreenNow() => EnqueueScreenSnapshot();

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
        if (pair == null) return;
        // Пузырь + статус + журнал/караоке сделаны выше; сам ask — общий движок
        // (BeginRequest/стрим/ctx-пара). OCR-статусы индикатора крутит он сам.
        await RunManualAskAsync(pair, label, isScreen: true, isManual: true);
        if (Config.ScreenOcr.AutoWatchEnabled) _overlay?.SetStatus(TT("L_Ocr_Watching", "OCR: слежу за экраном"));
        else RefreshOverlayStatus();
        _ocrIndicator?.SetState(OcrIndicatorState.Idle);
        _overlay?.FlashStatus(TT("L_Ocr_Done", "OCR: готово"));
    }

    /// <summary>
    /// Общий движок запроса к LLM для ручных путей (секретное сообщение, экранный
    /// вопрос, слитый пауза-буфер) и ретрая из пузыря. Регистрирует запрос через
    /// OverlayWindow.BeginRequest (✕-отмена по requestId), обновляет ctx-пару и журнал.
    /// </summary>
    /// <param name="pair">Пузырь, к которому пришиваем ответ. Для retry — тот же.</param>
    /// <param name="label">Вопрос с меткой канала: «[secret] …», «[screen] …» или слитый пауза-запрос.</param>
    /// <param name="isScreen">true → экранный промпт-оверрайд и OCR-UI статусы.</param>
    /// <param name="isManual">true → пузырь получает кнопку «Повторить» после Failed/Cancelled.</param>
    /// <param name="fromRetry">true → звучим из OnRetry (логировать для трассировки).</param>
    private async Task RunManualAskAsync(QaPair pair, string label, bool isScreen,
        bool isManual = true, bool fromRetry = false,
        string? messageId = null, IReadOnlyList<ChatImageTileVm>? imageTiles = null)
    {
        var req = _overlay?.BeginRequest(pair, isManual);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            CancellationToken.None, req?.Cts.Token ?? CancellationToken.None);
        var ct = linkedCts.Token;
        try
        {
            if (!fromRetry)
                _askRetryArgs.AddOrUpdate(pair, new AskRetryArgs(label, isManual, isScreen, messageId, imageTiles));
            _overlay?.MarkLlmSent(pair, DateTime.Now);
            // R4: ключ встречи снимаем ДО первого await (маршрут вложений с
            // map-reduce может жечь токены минутами — «новый чат» за это время
            // не должен утащить spend в свою встречу).
            var meetingKey = MeetingSpendKey;
            var stream = Config.Advanced.StreamLlmAnswers;
            var gotDelta = false;
            var streamId = stream ? (_karaoke?.BeginStreamLine("answer") ?? 0) : 0;
            var basePrompt = isScreen
                ? Config.Advanced.SystemPrompt +
                    "\n[screen] = вопрос, распознанный с экрана пользователя (OCR). " +
                    "Отвечай на него так же, как на вопрос собеседника. " +
                    "Ответь структурой: первая строка — прямой короткий ответ " +
                    "(например вариант «б» или одно предложение), затем одна строка «Зачем: …» " +
                    "с кратким обоснованием в 1-2 предложения. Не пересказывай вопрос. " +
                    "Отвечай СТРОГО на русском языке. " +
                    "Если решил молчать — верни ПУСТОЙ ответ (без единого символа). НЕ пиши слова вроде (empty), «пустой ответ», «silent», скобок или иероглифов."
                : Config.Advanced.SystemPrompt;
            // Контекст встречи: план (сводка+хвост) + память + purpose-оверрайд.
            var ctx = _meetingContext;
            var plan = ctx?.BuildPlan(label, basePrompt,
                Config.Advanced.MaxResponseTokens, null, null,
                Config.Api.ChatModel, Config.Api.BaseUrl);
            var (sysPrompt, meetingHits) = await BuildSystemPromptWithMemoryAsync(basePrompt, label, ct,
                requestId: req != null ? "req-" + req.RequestId : null,
                progress: stage => _overlay?.SetWaitingText(pair, stage + "…"));
            _overlay?.SetWaitingText(pair, TT("L_Answer_Waiting", "Отправляю вопрос…"));
            sysPrompt = ApplyContextToPrompt(sysPrompt, plan?.SummaryBlock,
                ctx?.State.EarlyContextDropped ?? false,
                isManual ? AskPurpose.ManualChat : AskPurpose.BackgroundSpeech);
            // Пузырь-индикатор контекста (спека §UI): «Контекст: ≈N» или «Обновляю…».
            var ctxState = ctx?.State;
            _overlay?.SetContextInfo(ctxState?.SummaryInFlight == true
                ? TT("L_ContextSummarizing", "Обновляю контекст…")
                : plan != null ? string.Format(TT("L_ContextInfoFmt", "Контекст: ≈{0}"), plan.EstimatedInputTokens.ToString("N0")) : null,
                ctxState?.SummaryInFlight ?? false);
            var sw = Stopwatch.StartNew();
            // MCP и обычный ask получают уже маршрутизированный контекст вложений:
            // страница/весь документ не смешиваются, неоднозначное имя даёт короткое
            // уточнение без лишнего LLM-вызова.
            var attachmentRoute = await BuildAttachmentRouteSafeAsync(label, plan, ct);
            var attachments = attachmentRoute?.Payload;
            var immediateAttachmentAnswer = attachmentRoute?.ImmediateAnswer;

            // Картинки текущего сообщения: байты читаются с диска (SendPath),
            // refs — для ChatMessage.Images в хвосте встречи. Не на UI-потоке.
            var imgs = await Task.Run(() => BuildImagePayloads(imageTiles), ct);
            IReadOnlyList<Core.Images.ChatImageRef>? imgRefs = imageTiles?.Select(t =>
                new Core.Images.ChatImageRef(t.ImageId, t.FileName, t.Mime, t.Width, t.Height, t.ByteSize)).ToList();
            var imageResolver = BuildImagePayloadResolver();

            var mcpAnswer = (immediateAttachmentAnswer == null && isManual && !isScreen)
                ? await TryMcpChatAnswerAsync(label, sysPrompt, plan?.Tail, attachments, ct, images: imgs,
                    requestId: req != null ? "req-" + req.RequestId : null, bubble: pair)
                : null;
            AskResult result;
            if (immediateAttachmentAnswer != null)
            {
                result = new AskResult(immediateAttachmentAnswer, 0, 0, 0);
            }
            else if (mcpAnswer != null)
            {
                Logger.Info($"MCP tool answer: stop={mcpAnswer.StopReason} " +
                    $"tools={mcpAnswer.ToolCalls.Count} sources={mcpAnswer.Sources.Count} " +
                    $"requestId={mcpAnswer.RequestId}",
                    "Answer");
                var mcpText = mcpAnswer.Text;
                if (!string.IsNullOrWhiteSpace(mcpText) && mcpAnswer.Sources.Count > 0)
                    mcpText += "\n\n🔧 " + string.Join(", ", mcpAnswer.Sources);
                // Встроенный поиск: источники фактов/встреч под ответом — по чему
                // именно модель нашла данные (снимок из этого ответа, не общий
                // изменяемый список — одновременные вопросы не смешиваются).
                var builtinCites = mcpAnswer.Citations;
                if (!string.IsNullOrWhiteSpace(mcpText) && builtinCites is { Count: > 0 })
                    mcpText += "\n\n📚 " + string.Join(" · ",
                        builtinCites.Distinct(StringComparer.Ordinal));
                result = new AskResult(mcpText, mcpAnswer.PromptTokens,
                    mcpAnswer.CompletionTokens, mcpAnswer.PromptTokens + mcpAnswer.CompletionTokens)
                {
                    Canceled = mcpAnswer.StopReason == Core.Mcp.Outgoing.McpLoopStopReason.Cancelled,
                    Error = mcpAnswer.Error != null
                        ? $"{mcpAnswer.Error.Code}: {mcpAnswer.Error.Message}" : null,
                    // Оценочные токены (usage не прислали) — честно помечаем,
                    // чтобы счётчик не выдавал оценку за точный расход.
                    UsageReported = !mcpAnswer.Estimated
                };
            }
            else
            {
                result = await ApiClient.AskAsync(label, sysPrompt,
                    Config.Advanced.MaxResponseTokens, plan != null ? new List<ChatMessage>(plan.Tail) : SnapshotLegacyHistory(), ct,
                    stream ? (Action<string>)(d =>
                    {
                        gotDelta = true;
                        _overlay?.AppendAnswerDelta(pair, d);
                        _karaoke?.AppendToStreamLine(streamId, d);
                    }) : null,
                    attachmentsContext: attachments,
                    images: imgs,
                    imageResolver: imageResolver);
            }

            // Счётчик токенов: ручной вопрос и MCP-ответ раньше мимо проходили —
            // usage появлялся только в речевом пути. Считаем обе ветви result.
            // R4: ключ встречи снят на старте ask'а — если чат сменится, пока ответ
            // в полёте, этот spend уйдёт в фон, а не в «Потрачено за встречу» новой.
            AccountUsage(result, meetingKey: meetingKey);
            sw.Stop();
            var mem = await ProcessMemoryDirectivesAsync(result.Content, ct);
            var answer = mem.Clean;
            if (string.IsNullOrWhiteSpace(answer) && mem.Applied.Count > 0)
                answer = TT("L_Answer_MemorySaved", "✔ записано в память");
            var memStripped = !string.Equals(answer, result.Content, StringComparison.Ordinal);
            FinalizeKaraokeStreamLine(stream, gotDelta, result.IsSilent, memStripped, answer, streamId);
            Logger.Info($"Manual ask done in {sw.ElapsedMilliseconds}ms ({answer.Length} chars, screen={isScreen}, retry={fromRetry})", "Answer");

            if (!string.IsNullOrWhiteSpace(answer))
            {
                answer = answer.TrimStart();
                // вычищаем лидирующие метки-скобки (модель иногда повторяет метку канала)
                int _c;
                while (answer.StartsWith("[") && (_c = answer.IndexOf(']')) > 0)
                    answer = answer.Substring(_c + 1).TrimStart();
                await Dispatcher.InvokeAsync(() =>
                {
                    pair.PastMeetingHits = meetingHits; // §7: ссылки-источники под ответом
                    _overlay?.MarkAnswered(pair, answer, DateTime.Now, sw.Elapsed.TotalMilliseconds);
                    RefreshOverlayStatus();
                    if (!gotDelta) _karaoke?.PushLine("answer", answer);
                });
                AppendJournalWithMemory("answer", answer);
                // Журнал user-реплики: mid+imgs идут ТОЛЬКО при ответе — прерванная
                // отправка не оставляет сирот (восстановление черновика по
                // FindLastUnansweredImageIds).
                if (!fromRetry && (messageId != null || imgRefs?.Count > 0))
                    AppendJournalWithMemory("user", label, messageId: messageId, imgs: imgRefs);
                lock (_historyLock)
                {
                    _conversationHistory.Add(imgRefs?.Count > 0
                        ? ChatMessage.UserWithImages(label, imgRefs)
                        : ChatMessage.User(label));
                    _conversationHistory.Add(ChatMessage.Assistant(answer));
                    TrimHistory(_conversationHistory, Config.Advanced.HistorySize);
                }
                // Контекст встречи параллельно легаси-истории (обрезка по токенам, не по счёту).
                if (imgRefs?.Count > 0)
                    ctx?.AddPair(ChatMessage.UserWithImages(label, imgRefs), answer);
                else
                    ctx?.AddPair(label, answer);
                SaveMeetingContextState();
                if (plan?.ScheduleSummary == true) RunContextSummaryAsync();
            }
            else if (result.Canceled || (req != null && req.Cts.IsCancellationRequested))
            {
                // Отмена по кнопке/токену — НЕ ошибка: нейтральная метка пузыря.
                Logger.Info($"Manual ask cancelled in {sw.ElapsedMilliseconds}ms", "Answer");
                _overlay?.MarkCancelled(pair);
            }
            else
            {
                var why = result.Error ?? EmptyAnswerHint(result, memStripped);
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.MarkFailed(pair, why, DateTime.Now, sw.Elapsed.TotalMilliseconds);
                    RefreshOverlayStatus();
                });
            }
        }
        catch (OperationCanceledException) when (req?.Cts.IsCancellationRequested == true)
        {
            Logger.Info("Manual ask cancelled (request token)", "Answer");
            _overlay?.MarkCancelled(pair);
        }
        catch (Exception ex)
        {
            Logger.Error("RunManualAskAsync failed", ex, "UI");
            await Dispatcher.InvokeAsync(() => _overlay?.MarkFailed(pair, ex.Message, DateTime.Now, 0));
        }
        finally
        {
            if (req != null) _overlay?.EndRequest(req);
            linkedCts.Dispose();
        }
    }

    /// <summary>
    /// Исходящий MCP tool-loop для ручного вопроса. Возвращает null, если MCP
    /// выключен, оркестратор недоступен или ни один включённый сервер не отдал
    /// инструментов — caller тогда идёт обычным AskAsync.
    /// </summary>
    private async Task<Core.Mcp.Outgoing.McpChatAnswer?> TryMcpChatAnswerAsync(
        string label, string sysPrompt, IReadOnlyList<ChatMessage>? tail,
        string? attachmentsContext, CancellationToken ct,
        IReadOnlyList<Core.Images.ChatImagePayload>? images = null, string? requestId = null,
        QaPair? bubble = null)
    {
        var mcp = McpChat;
        if (mcp == null) return null;
        // Встроенные инструменты памяти доступны и без внешних MCP-серверов;
        // внешние — только при включённом общем тумблере.
        var external = Config.Mcp.Enabled && Config.Mcp.OutgoingServers.Any(s => s.Enabled);
        if (!external && !BuiltinToolsAvailable) return null;
        // Пауза/выключенный LLM: один ручной шаг обслуживается обычным путём
        // (ниже AskAsync), без tool-loop — иначе пауза убивала бы ответ целиком.
        if (IsLlmPaused || Config.Ui.LlmDisabled) return null;
        var prompt = sysPrompt;
        if (!string.IsNullOrWhiteSpace(attachmentsContext))
            prompt += "\n\n" + attachmentsContext;
        try
        {
            var answer = await mcp.AskWithToolsAsync(label, tail, prompt,
                maxTokens: Config.Advanced.MaxResponseTokens, images: images,
                requestId: requestId, ct: ct,
                progress: (stage, done, total) => OnMcpToolProgress(bubble, stage, done, total));
            if (answer != null && answer.StopReason != Core.Mcp.Outgoing.McpLoopStopReason.Cancelled)
                _overlay?.SetContextInfo(null, false);
            return answer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"MCP chat path failed, falling back to plain ask: {ex.Message}", "Mcp");
            return null; // ошибка MCP не блокирует обычный чат (спека §439)
        }
    }

    /// <summary>
    /// Подтверждение опасного MCP-вызова: карточка внутри оверлея (спека §491 —
    /// не модалка; оверлей исключён из захвата экрана, значит и аппрувал скрыт).
    /// Вызывается из tool-loop на фоновом потоке; overlay сам маршаллит в UI.
    /// </summary>
    private Task<bool> ConfirmMcpToolCallAsync(
        McpOutgoingServer server, Core.Mcp.Outgoing.McpToolSpec tool,
        string argsJson, Core.Mcp.McpActionRisk risk)
    {
        var riskText = risk == Core.Mcp.McpActionRisk.Destructive
            ? TT("L_McpRiskDestructive", "необратимое действие")
            : TT("L_McpRiskMutating", "изменяющее действие");
        var args = argsJson.Length > 500 ? argsJson[..500] + "…" : argsJson;
        var summary = string.Format(
            TT("L_McpApproveFmt", "Сервер «{0}» просит выполнить «{1}» ({2})"),
            server.Name, tool.OriginalName, riskText);

        // Спека §491: подтверждение — карточка ВНУТРИ оверлея (не модалка,
        // не видна на захвате экрана). Overlay вызывается с фонового потока
        // tool-loop → сам маршаллит через Dispatcher.
        if (_overlay == null || !Dispatcher.CheckAccess() && _overlay.Dispatcher.HasShutdownStarted)
            return Task.FromResult(false);
        return _overlay.RequestMcpApproval(summary, args);
    }

    /// <summary>Прогресс tool-loop → строка статуса пузыря (reuse канала «Контекст: ≈N»).</summary>
    private void OnMcpToolProgress(string? stage, int? done, int? total)
        => OnMcpToolProgress(null, stage, done, total);

    /// <summary>Этап реального поиска показывается и в самой карточке вместо
    /// «(ожидание ответа LLM…)» — только когда инструмент действительно вызван.</summary>
    private void OnMcpToolProgress(QaPair? bubble, string? stage, int? done, int? total)
    {
        var text = done != null && total != null
            ? $"{stage} {done}/{total}"
            : stage;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (bubble != null) _overlay?.SetWaitingText(bubble, text + "…");
        _overlay?.SetContextInfo(text, true, showProvidedText: true);
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
    // §7: hit != null — открыть конкретную встречу и подсветить найденную строку
    // (ссылка «Источник» в оверлее после ответа на вопрос о прошлой встрече).
    public void ShowMeetingHistory(MeetingSearchHit? hit = null)
    {
        try
        {
            var needsNavigate = hit != null;
            if (_meetingHistoryWindow != null)
            {
                if (_meetingHistoryWindow.WindowState == WindowState.Minimized)
                    _meetingHistoryWindow.WindowState = WindowState.Normal;
                _meetingHistoryWindow.Show();
                _meetingHistoryWindow.Activate();
            }
            else
            {
                _meetingHistoryWindow = new MeetingHistoryWindow();
                _meetingHistoryWindow.Closed += (s, e) => _meetingHistoryWindow = null;
                _meetingHistoryWindow.Show();
            }
            if (needsNavigate) _ = _meetingHistoryWindow!.NavigateToHitAsync(hit!);
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
        // «Думаю над …»: метка первого (единственного активного) запроса.
        // Краткая: secret/screen — каноничные лейблы; голос — начало вопроса.
        var thinking = FirstThinkingLabel();
        var input = new BrainstormBuddy.Services.OverlayStatusInput(
            IsPaused, IsLlmPaused, _sttOk, _sttErrMsg, _sttWarmedUp, IsTranscribing, _sttHttpStatus, _sttErrKind,
            thinking);
        // T(key) возвращает сам ключ, если ресурса нет — тогда берём RU-фолбэк из резолвера.
        var status = BrainstormBuddy.Services.OverlayStatusResolver.Resolve(input,
            (k, fb) => T(k) is { } s && s != k && !s.StartsWith("[") ? s : fb);
        try { Dispatcher.InvokeAsync(() => _overlay?.ApplyStatus(status)); }
        catch { /* приложение уже уходит — статус некому показывать */ }
    }

    /// <summary>Короткая метка активного LLM-запроса для статуса «Думаю над …».
    /// null — активных нет (Thinking не показывается).</summary>
    private string? FirstThinkingLabel()
    {
        var req = _overlay?.PendingRequests?.FirstOrDefault();
        if (req == null) return null;
        var q = req.Question ?? "";
        if (req.IsManual || q.StartsWith("[secret]", StringComparison.Ordinal)) return "[secret]";
        if (q.StartsWith("[screen]", StringComparison.Ordinal)) return "[screen]";
        const int max = 48;
        var shortText = q.Length <= max ? q : q[..max] + "…";
        return $"«{shortText}»";
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
            // R4: расход экспорта протокола — прямой (пользователь нажал кнопку),
            // привязываем к активной встрече на старте запроса.
            var meetingKey = MeetingSpendKey;
            var result = await ApiClient.AskAsync("Составь протокол встречи по транскрибации выше.",
                summaryPrompt, 800, new List<ChatMessage>(), CancellationToken.None);
            sw.Stop();
            AccountUsage(result, meetingKey: meetingKey);
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
                var why = result.Error ?? EmptyAnswerHint(result, false);
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
                    QaPair? pair = null;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        pair = _overlay?.AddSendingItem(label, (chunk.Length - 44) / 32000.0,
                            DateTime.Now, DateTime.Now, DateTime.Now, 0, transcriptOnly: llmOff);
                    });
                    AppendJournalWithMemory("question", shown); // журнал пишет реплику и в режиме «LLM выкл»
                    if (llmOff || pair == null) return;
                    // Молния = ручной форс-вопрос из аудио-буфера — тот же общий движок.
                    await RunManualAskAsync(pair, label, isScreen: false, isManual: false);
                }
            }
            catch (Exception ex) { Logger.Error("FlushAndSend failed", ex, "Loop"); }
        });
    }

    public async Task SendSecretMessage(string text)
    {
        // Черновик картинок есть — идём расширенным путём (ТЗ image-chat §9):
        // flow снимает готовые тайлы, делает гейт поддержки и отдаёт снапшот.
        if (ChatImages?.HasDraft == true)
        {
            await ChatImages.SendChatAsync(text);
            return;
        }

        // «LLM выкл» (чистый транскрибатор): в LLM не уходит и явное сообщение —
        // подсказываем, почему поле «не работает», вместо тихого пузыря без ответа.
        if (Config.Ui.LlmDisabled)
        {
            Notifier.ShowInfo(TT("L_Toast_LlmOff", "LLM выключен"), TT("L_Toast_LlmOffSend", "Включите LLM кнопкой на нижней панели, чтобы отправлять сообщения."));
            return;
        }
        var label = $"[secret] {text}";
        QaPair? pair = null;
        await Dispatcher.InvokeAsync(() =>
        {
            pair = _overlay?.AddSendingItem(label, 0, DateTime.Now, null, null, 0);
            _overlay?.SetStatus(TT("L_Status_ThinkingSecret", "Думаю над [secret]…"));
            // Караоке: секретное сообщение — реплика юзера без метки [secret].
            _karaoke?.PushLine("user", text);
            AppendJournalWithMemory("user", text); // журнал — всегда, без фильтров Show*
        });
        if (pair == null) return; // оверлей закрыт — не шлём без пузыря
        await RunManualAskAsync(pair, label, isScreen: false, isManual: true);
    }

    /// <summary>
    /// Отправка сообщения с картинками (ТЗ image-chat §9). Черновик инвалидируется
    /// ТОЛЬКО на принятии отправки; при паузе LLM снапшот (текст+imageIds) едет в
    /// pause-буфер и при снятии паузы сольётся одним запросом вместе с картинками.
    /// </summary>
    internal async Task SendChatWithImagesAsync(string text, ChatImageFlow.SentImageSet set)
    {
        var flow = ChatImages;
        if (flow == null) { await SendSecretMessage(text); return; }
        var imageIds = set.Tiles.Select(t => t.ImageId).ToArray();

        // Пауза LLM: буферизуем снапшот целиком (спека §9 — «пауза LLM буферизует
        // снапшот с imageIds»). Тайлы уже сняты flow — принято в отправку, поле чистим.
        if (IsLlmPaused)
        {
            var pausedLabel = string.IsNullOrEmpty(text) ? "[Изображение]" : text;
            _pauseBuf.Add("[secret]", pausedLabel, imageIds);
            _overlay?.ClearChatInput();
            Logger.Info($"chat-image: отправка ушла в pause-буфер ({imageIds.Length} img)", "ChatImg");
            return;
        }

        var labelText = $"[secret] {text}".TrimEnd();
        var mid = set.MessageId;
        var meetingId = string.IsNullOrEmpty(set.MeetingId) ? SafeEnsureMeeting() : set.MeetingId;
        QaPair? pair = null;
        await Dispatcher.InvokeAsync(() =>
        {
            pair = _overlay?.AddSendingItem(labelText, 0, DateTime.Now, null, null, 0, imageTiles: set.Tiles);
            _overlay?.SetStatus(TT("L_Status_ThinkingSecret", "Думаю над [secret]…"));
            _karaoke?.PushLine("user", text);
        });
        if (pair == null)
        {
            // Оверлей закрыт — тайлы возвращаем в черновик (set — read-only копия).
            flow.RestoreTiles(set.Tiles);
            flow.SetDraftNotice(TT("L_ImgOverlayClosed", "Оверлей закрыт — черновик сохранён"));
            return;
        }

        // Принято в отправку: линкуем картинки к messageId (идемпотентно по mid+id).
        try
        {
            if (meetingId != null)
                for (var i = 0; i < imageIds.Length; i++)
                    flow.Store.Link(imageIds[i], meetingId, mid, i);
        }
        catch (Exception ex) { Logger.Warn($"chat-image: link {mid}: {ex.Message}", "ChatImg"); }
        _overlay?.ClearChatInput();

        await RunManualAskAsync(pair, string.IsNullOrEmpty(text) ? labelText : $"[secret] {text}",
            isScreen: false, isManual: true, messageId: mid, imageTiles: set.Tiles);
    }

    /// <summary>Payload'ы картинок текущего сообщения (байты с диска, SendPath).</summary>
    private List<Core.Images.ChatImagePayload>? BuildImagePayloads(IReadOnlyList<ChatImageTileVm>? tiles)
    {
        if (tiles == null || tiles.Count == 0) return null;
        var list = new List<Core.Images.ChatImagePayload>(tiles.Count);
        foreach (var t in tiles)
        {
            var path = t.SendPath ?? t.OriginalPath;
            if (path == null || !File.Exists(path))
            {
                Logger.Warn($"chat-image: payload skip {t.ImageId} state={t.State} " +
                    $"send={t.SendPath ?? "null"} orig={t.OriginalPath ?? "null"} " +
                    $"exists={(path == null ? "-" : File.Exists(path).ToString())}", "ChatImg");
                continue;
            }
            list.Add(new Core.Images.ChatImagePayload(
                t.ImageId, t.Mime, File.ReadAllBytes(path), t.Width, t.Height));
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Резолвер imageId → payload для истории (последнее сообщение с картинками).
    /// Нужен транспорту при сборке messages: ChatMessage.Images хранит ссылки,
    /// байты достаются из стора по id.
    /// </summary>
    private Func<Core.Images.ChatImageRef, Core.Images.ChatImagePayload?>? BuildImagePayloadResolver()
    {
        var store = ChatImages?.Store;
        if (store == null) return null;
        return r =>
        {
            var s = store.Get(r.ImageId);
            if (s == null) return null;
            var path = s.SendPath ?? s.OriginalPath;
            if (path == null || !File.Exists(path))
            {
                store.MarkState(r.ImageId, Core.Images.ChatImageState.Missing);
                return null;
            }
            return new Core.Images.ChatImagePayload(
                r.ImageId, s.Ref.MimeType, File.ReadAllBytes(path), s.Ref.Width, s.Ref.Height);
        };
    }

    /// <summary>
    /// Показать тост в стеке оверлея из любого потока: при вызове не из UI-потока
    /// вызов маршалится на Dispatcher оверлея. Оверлей ещё не создан — тост пропускаем.
    /// </summary>
    public void PushOverlayToast(ToastKind kind, string title, string body)
    {
        var overlay = _overlay;
        if (overlay == null) return;
        if (overlay.Dispatcher.CheckAccess())
            overlay.PushToast(title, body, kind);
        else
            overlay.Dispatcher.BeginInvoke(() => overlay.PushToast(title, body, kind));
    }


    // ===== Вложения чата: стор, очередь импорта, контекст и пикер =====

    /// <summary>OCR страниц PDF (WinRT+Tesseract): ленивый синглтон приложения.</summary>
    internal IPdfPageOcr EnsurePdfOcr()
        => _pdfOcr ??= new PdfOcrService(Logger);

    /// <summary>Фоновый импортёр; null — AttachmentStore не инициализирован.</summary>
    private AttachmentImportRunner? EnsureImportRunner()
    {
        if (_attachmentStore == null) return null;
        if (_importRunner == null)
        {
            _importRunner = new AttachmentImportRunner(_attachmentStore, EnsurePdfOcr(), Logger);
            _importRunner.ItemUpdated += i => Dispatcher.BeginInvoke(() =>
            {
                OnImportItemToast(i);
                RefreshAttachmentChips();
            });
            // Commit вооружает позднюю линковку: импорты, доехавшие после Apply,
            // уходят в текущую встречу (вложение не пропадает).
            // Коммит отдаёт CommittedItem{DocumentId, MeetingId, FactsRequested}:
            // линкуем к встрече на момент enqueue (null → фолбэк на текущую).
            _importRunner.CommitSessionHandler += ci => CommitLinkedAttachment(ci);
            // Пропуск повторной линковки/извлечения уже привязанного документа.
            _importRunner.IsAlreadyLinked = (docId, mid) =>
                !string.IsNullOrEmpty(mid) && _attachmentStore.IsLinkedToMeeting(docId, mid);
        }
        return _importRunner;
    }

    /// <summary>Воркер извлечения фактов из документов (очередь fact_jobs).</summary>
    private AttachmentFactWorker? EnsureFactWorker()
    {
        if (_attachmentStore == null || MemoryStore == null) return null;
        if (_factWorker == null)
        {
            var extractor = new AttachmentFactExtractor(
                ApiClient, MemoryStore, Logger,
                () => Config.Memory.TranscriptExtractRules,
                consolidator: MemoryConsolidator as MemoryConsolidator,
                maxTokensSource: () => Config.Memory.ExtractMaxTokens,
                maxTotalCharsSource: () => Config.Attachments.FactExtractMaxChars,
                intensitySource: () => Config.Memory.ExtractionIntensity,
                // §8.1: фоновый LLM-расход извлечения фактов — в отдельный счётчик
                // «Документы и память», не в «Потрачено за встречу».
                onUsage: res => AccountUsage(res, background: true));
            _factWorker = new AttachmentFactWorker(
                _attachmentStore, extractor,
                () => CurrentMeetingId,
                () => Config.Memory.ReviewMode, Logger);
            _factWorker.JobUpdated += _ => Dispatcher.BeginInvoke(RefreshAttachmentChips);
            // Окна (история встречи) освежают список файлов по этому событию.
            _factWorker.JobUpdated += j => Dispatcher.BeginInvoke(() => FactJobChanged?.Invoke(j));
            // Тосты прогресса извлечения фактов (стек оверлея): Running → Progress-тост
            // с «запрос N/M», терминал — снять прогресс и показать итог.
            _factWorker.JobUpdated += j => Dispatcher.BeginInvoke(() => OnFactJobToast(j));
        }
        return _factWorker;
    }

    /// <summary>
    /// Отменить задание извлечения фактов: Running — по токену (экстрактор фиксирует
    /// Canceled между частями), Queued/Interrupted — помечается Canceled и пропускается.
    /// Вызывается из Memory Center (стоп на карточке) и может зваться до старта воркера.
    /// </summary>
    public async Task CancelFactJobAsync(long jobId)
    {
        var w = EnsureFactWorker();
        if (w != null)
            await w.CancelAsync(jobId).ConfigureAwait(false);
    }

    /// <summary>Пнуть fact-воркер после re-queue («Продолжить»/«Повторить» на карточке).</summary>
    public void KickFactWorker() => EnsureFactWorker()?.Kick();

    /// <summary>
    /// Тосты по статусу fact-джобы (вызывается в UI-потоке из JobUpdated):
    /// Running → Progress-тост с телом «запрос N/M» (обновляется по toastId),
    /// терминальные — снимают прогресс и показывают итоговый тост.
    /// </summary>
    private void OnFactJobToast(FactJob job)
    {
        var docName = AttachDocName(job.DocumentId);
        if (job.Status == FactJobStatus.Running)
        {
            var body = string.Format(
                TT("L_Toast_FactsProgressFmt", "Запрос {0}/{1}"),
                job.RequestsDone, job.RequestsPlanned);
            if (!_factToastIds.TryGetValue(job.Id, out var toastId) && _overlay != null)
            {
                // actionText/onAction — кнопка «Центр памяти» в тосте прогресса: единственный
                // постижимый путь к FactJobCard (прогресс % + «Остановить») помимо трея.
                toastId = _overlay.PushToast(
                    string.Format(TT("L_Toast_FactsRunning", "Извлечение фактов: {0}"), docName),
                    body, ToastKind.Progress,
                    actionText: TT("L_Att_OpenMemoryCenter", "Центр памяти"),
                    onAction: () => Dispatcher.BeginInvoke(new Action(OpenMemoryCenter)));
                _factToastIds[job.Id] = toastId;
            }
            else if (_overlay != null)
            {
                _overlay.UpdateToast(toastId, body);
            }
            return;
        }

        // Любой не-Running статус закрывает прогресс-тост этой джобы.
        if (_factToastIds.Remove(job.Id, out var doneToastId))
            _overlay?.DismissToast(doneToastId);

        switch (job.Status)
        {
            case FactJobStatus.Done:
                PushOverlayToast(ToastKind.Success,
                    string.Format(TT("L_Toast_FactsDoneFmt", "Факты сохранены: {0}"), job.FactsSaved),
                    docName);
                break;
            case FactJobStatus.PendingReview:
                PushOverlayToast(ToastKind.Info,
                    string.Format(TT("L_Att_FactsPendingReviewFmt", "Факты: на проверке ({0})"), job.FactsPending),
                    docName);
                break;
            case FactJobStatus.NoFacts:
                PushOverlayToast(ToastKind.Info,
                    TT("L_Toast_FactsNone", "Факты не найдены"), docName);
                break;
            case FactJobStatus.Error:
                PushOverlayToast(ToastKind.Error,
                    TT("L_Toast_FactsError", "Ошибка извлечения фактов"),
                    $"{docName}: {TruncateToast(job.Error ?? "—", 120)}");
                break;
            case FactJobStatus.Canceled:
                PushOverlayToast(ToastKind.Info,
                    TT("L_Toast_FactsCanceled", "Остановлено"),
                    string.Format(TT("L_MC_FactJob_StoppedFmt",
                        "Остановлено: обработано {0} из {1} частей, сохранено {2} фактов"),
                        job.RequestsDone, job.RequestsPlanned, job.FactsSaved));
                break;
            // Queued/Interrupted — без тоста (переходное состояние).
        }
    }

    /// <summary>
    /// Тост по итогу импорта пункта picker'а (вызывается в UI-потоке из ItemUpdated):
    /// показывает только ошибки — успех прилетает из LinkAttachmentToCurrentMeeting
    /// (там реальная привязка к встрече). Псевдо-ошибка ENCODING: не тостится —
    /// это запрос выбора кодировки, а не провал.
    /// </summary>
    private void OnImportItemToast(AttachmentImportRunner.ImportItem item)
    {
        if (item.Error == null || item.Error.StartsWith("ENCODING:", StringComparison.Ordinal))
            return;
        PushOverlayToast(ToastKind.Error,
            TT("L_Toast_AttachFail", "Не удалось прикрепить документ"),
            $"{item.DisplayName}: {TruncateToast(item.Error, 120)}");
    }

    /// <summary>Имя документа по id (фолбэк — сам id, если стор недоступен/упал).</summary>
    private string AttachDocName(string documentId)
    {
        try { return _attachmentStore?.Get(documentId)?.DisplayName ?? documentId; }
        catch { return documentId; }
    }

    /// <summary>Тело тоста не длиннее maxLen (обрезка с многоточием).</summary>
    private static string TruncateToast(string text, int maxLen)
        => text.Length <= maxLen ? text : text.Substring(0, maxLen) + "…";

    /// <summary>Смена статуса fact-джобы вложения — всегда на UI-потоке
    /// (используется окном истории встречи для перечитывания списка файлов).</summary>
    public event Action<BrainstormBuddy.Core.Attachments.FactJob>? FactJobChanged;

    /// <summary>
    /// Id живого файла встречи для вложений. Пустой журнал создаётся без фиктивной
    /// реплики (EnsureCurrentMeeting идемпотентен).
    /// </summary>
    internal string? EnsureMeetingForAttachments()
    {
        try { return MeetingJournal?.EnsureCurrentMeeting(); }
        catch (Exception ex) { Logger.Warn($"EnsureMeetingForAttachments failed: {ex.Message}", "Attach"); return null; }
    }

    /// <summary>
    /// Немодальный пикер вложений (single instance): чат остаётся доступен, окно
    /// живёт поверх оверлея-владельца. Повторный вызов при открытом окне поднимает
    /// его (Normal + Activate); закрытое окно пересоздаётся — живая сессия импорта
    /// восстанавливается из runner'а (BeginSession). «Прикрепить» даёт CommitSession
    /// + LinkAttachmentToCurrentMeeting по готовым; «Закрыть» оставляет очередь
    /// работать в фоне (поздние завершения долинковывает App).
    /// </summary>
    public Task OpenAttachmentPicker()
    {
        if (_attachmentPicker is { } open)
        {
            try
            {
                if (open.WindowState == WindowState.Minimized)
                    open.WindowState = WindowState.Normal;
                open.Show();
                open.Activate();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                // Окно уже закрывается/умерло (гонка с Closed) — забываем ссылку
                // и создаём новое ниже; Closed старого окна новую ссылку не тронет.
                Logger.Warn($"Attach: picker activate failed ({ex.Message}) — recreating", "Attach");
                _attachmentPicker = null;
            }
        }
        var runner = EnsureImportRunner();
        if (runner == null)
        {
            AppMessageBox.Show(_overlay,
                TT("L_Att_ErrStore", "Хранилище вложений недоступно — перезапустите приложение."),
                TT("L_Att_Title", "Документы"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }
        var dlg = new AttachmentPickerWindow(runner, _overlay);
        _attachmentPicker = dlg;
        dlg.Closed += (_, _) =>
        {
            if (ReferenceEquals(_attachmentPicker, dlg))
                _attachmentPicker = null;
            RefreshAttachmentChips();
        };
        dlg.Show();
        dlg.Activate();
        return Task.CompletedTask;
    }

    /// <summary>Линковка документа, доехавшего в Commit-сессии после закрытия пикера.</summary>
    private void CommitLinkedAttachment(AttachmentImportRunner.CommittedItem item)
        => Dispatcher.BeginInvoke(() => _ = LinkAttachmentToCurrentMeeting(
            item.DocumentId, item.FactsRequested, item.MeetingId));

    /// <summary>
    /// Привязать документ к живой встрече. При extractFacts - постановка fact-джобы
    /// (снимок провайдера/модели/scope/режима проверки) и пробуждение воркера.
    /// </summary>
    internal async Task LinkAttachmentToCurrentMeeting(string documentId, bool extractFacts, string? journalMeetingId = null)
    {
        var store = _attachmentStore;
        var meetingId = journalMeetingId ?? EnsureMeetingForAttachments();
        if (store == null || meetingId == null || string.IsNullOrEmpty(documentId)) return;
        try
        {
            await store.LinkToMeetingAsync(documentId, meetingId, CancellationToken.None);
            Logger.Info($"Attachment {documentId} linked to meeting {meetingId}", "Attach");
            // Тост успеха — после реальной линковки, не при enqueue импорта.
            PushOverlayToast(ToastKind.Success,
                TT("L_Toast_AttachDone", "Документ прикреплён"), AttachDocName(documentId));
            if (extractFacts)
                await EnqueueAttachmentFactJob(documentId, meetingId);
            else if (!_localZeroNoticeShown)
            {
                // §8.1: чип «Готов» после ЛОКАЛЬНОЙ обработки (PDF/OCR) не должен
                // читаться как расход LLM — честно сообщаем, что токенов не потрачено.
                _localZeroNoticeShown = true;
                PushOverlayToast(ToastKind.Info,
                    TT("L_Toast_LocalZeroTitle", "Локальная обработка"),
                    TT("L_Toast_LocalZeroBody", "Документ распознан локально — 0 LLM-токенов (извлечение фактов не запускалось)"));
                Logger.Info("Local attach: 0 LLM tokens (no fact extraction) — notice shown", "Tokens");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"LinkAttachment failed for {documentId}: {ex.Message}", "Attach");
        }
        RefreshAttachmentChips();
    }

    /// <summary>Постановка fact-джобы для документа; снимок настроек памяти на момент Commit.</summary>
    internal async Task EnqueueAttachmentFactJob(string documentId, string? journalMeetingId = null)
    {
        var store = _attachmentStore;
        if (store == null || string.IsNullOrEmpty(documentId)) return;
        var meetingId = journalMeetingId ?? EnsureMeetingForAttachments();
        if (meetingId == null) return;
        if (MemoryStore == null)
        {
            AppMessageBox.Show(_overlay,
                TT("L_Att_ErrNoMemory", "Память выключена - извлечение фактов недоступно."),
                TT("L_Att_Title", "Документы"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var job = new FactJob(
                Id: 0, DocumentId: documentId, MeetingId: meetingId,
                Status: FactJobStatus.Queued, CharsSent: 0, RequestsPlanned: 0, RequestsDone: 0,
                FactsSaved: 0, FactsPending: 0, Error: null,
                ProviderId: Config.Api.ProviderId,
                Model: Config.Api.ChatModel,
                Scope: Config.Memory.ActiveProjectId.HasValue ? "project" : "global",
                VerifyMode: Config.Memory.ReviewMode,
                CreatedUtc: DateTime.UtcNow);
            await store.EnqueueFactJobAsync(job);
            EnsureFactWorker()?.Kick();
            Logger.Info($"Fact job enqueued for {documentId}", "Attach");
        }
        catch (Exception ex)
        {
            Logger.Warn($"EnqueueFactJob failed for {documentId}: {ex.Message}", "Attach");
        }
        RefreshAttachmentChips();
    }

    /// <summary>Отвязать документ от живой встречи (крестик на чипе). Копия файла остаётся.</summary>
    public async Task RemoveMeetingAttachmentAsync(string documentId)
    {
        var store = _attachmentStore;
        var meetingId = MeetingJournal?.CurrentId;
        if (store == null || meetingId == null || string.IsNullOrEmpty(documentId)) return;
        try
        {
            await store.UnlinkFromMeetingAsync(documentId, meetingId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warn($"UnlinkAttachment failed for {documentId}: {ex.Message}", "Attach");
        }
        RefreshAttachmentChips();
    }

    /// <summary>
    /// Перерисовать строку чипов оверлея по живой встрече: имя + статус извлечения
    /// (Warnings — не ошибки: «OCR: N стр.» значит «страницы распознаны», а не сбой)
    /// + статус fact-джобы.
    /// </summary>
    public void RefreshAttachmentChips()
    {
        var store = _attachmentStore;
        var overlay = _overlay;
        var meetingId = MeetingJournal?.CurrentId;
        if (overlay == null) return;
        if (store == null || meetingId == null)
        {
            overlay.SetAttachmentChips(Array.Empty<AttachmentChipVm>());
            return;
        }
        IReadOnlyList<MeetingAttachment> docs;
        try { docs = store.ListMeetingDocuments(meetingId); }
        catch (Exception ex)
        {
            Logger.Warn($"ListMeetingDocuments failed: {ex.Message}", "Attach");
            docs = Array.Empty<MeetingAttachment>();
        }
        var chips = new List<AttachmentChipVm>(docs.Count);
        foreach (var ma in docs)
        {
            var doc = ma.Doc;
            var status = doc.Deleted ? TT("L_Att_StDeleted", "Удалён")
                : doc.Warnings.Count > 0 ? TT("L_Att_StWarnFmt", "Готов · {0}").Replace("{0}", string.Join("; ", doc.Warnings))
                : TT("L_Att_StReady", "Готов");
            FactJob? job = null;
            try { job = store.LatestJobFor(doc.DocumentId, meetingId); }
            catch { /* статус фактов - бест-эфорт */ }
            var tip = status;
            if (job != null)
            {
                var facts = job.Status switch
                {
                    FactJobStatus.Queued => TT("L_Att_FactsQueued", "Факты: в очереди"),
                    FactJobStatus.Running => TT("L_Att_FactsRunning", "Факты: извлекаются…"),
                    FactJobStatus.PendingReview => TT("L_Att_FactsPendingReviewFmt", "Факты: на проверке ({0})").Replace("{0}", job.FactsPending.ToString()),
                    FactJobStatus.Done => TT("L_Att_FactsDoneFmt", "Факты: сохранено {0}").Replace("{0}", job.FactsSaved.ToString()),
                    FactJobStatus.NoFacts => TT("L_Att_FactsNoFacts", "Факты: не найдено"),
                    FactJobStatus.Canceled => TT("L_Att_FactsCanceled", "Факты: отменено"),
                    _ => TT("L_Att_FactsErrorFmt", "Факты: ошибка — {0}").Replace("{0}", job.Error ?? ""),
                };
                status = $"{status} · {facts}";
                tip = status;
            }
            chips.Add(new AttachmentChipVm
            {
                DocumentId = doc.DocumentId,
                DisplayName = doc.DisplayName,
                StatusText = status,
                ToolTip = tip,
            });
        }

        // Ещё не слинкованные импорты (очередь runner'а): чип «В обработке», без крестика
        // (удалять нечего — связи с встречей пока нет). Уже привязанные пропускаем.
        var runner = _importRunner;
        if (runner != null)
        {
            var linkedIds = new HashSet<string>(
                docs.Select(d => d.Doc.DocumentId), StringComparer.Ordinal);
            foreach (var item in runner.SnapshotItems())
            {
                if (item.DocumentId != null && linkedIds.Contains(item.DocumentId)) continue;
                if (item.ExtractFinished) continue;   // финал есть, но связи ещё нет — мигнёт при следующем refresh
                // §5.2: живой прогресс «В обработке: 48/51 стр. · 94%» — ProgressDone/Total
                // считаются по завершённым страницам (не по номеру последней).
                string st;
                if (item.Error != null)
                    st = item.Error.StartsWith("ENCODING:", StringComparison.Ordinal)
                        ? TT("L_Att_StEncoding", "Нужен выбор кодировки")
                        : TT("L_Att_StFailedFmt", "Не удалось прочитать: {0}").Replace("{0}", item.Error);
                else if (item.StoppedByUser)
                    st = TT("L_Att_StStopped", "Остановлено");
                else if (item.ProgressTotal > 0)
                    st = string.Format(TT("L_Att_ImportProgFmt", "В обработке: {0}/{1} стр. · {2}%"),
                        item.ProgressDone, item.ProgressTotal,
                        (int)Math.Round(100.0 * item.ProgressDone / item.ProgressTotal));
                else
                    st = TT("L_Att_StProcessing", "В обработке");
                chips.Add(new AttachmentChipVm
                {
                    DocumentId = item.DocumentId ?? "",
                    DisplayName = item.DisplayName,
                    StatusText = st,
                    ToolTip = item.SourcePath,
                    Pending = true,
                });
            }
        }
        overlay.SetAttachmentChips(chips);
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
            (fullSystemPrompt, _) = await BuildSystemPromptWithMemoryAsync(
                fullSystemPrompt, pair.Question ?? "", CancellationToken.None);
            // R4: расширение ответа — прямой пользовательский запрос; ключ встречи
            // фиксируем на старте (чат мог смениться, пока ответ в полёте).
            var meetingKey = MeetingSpendKey;
            var result = await ApiClient.AskAsync("[EXPAND] Разверни последний ответ",
                fullSystemPrompt, 400, new List<ChatMessage>(), CancellationToken.None);
            AccountUsage(result, meetingKey: meetingKey);
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
                var why = result.Error ?? EmptyAnswerHint(result, false);
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
        _tokenLedger.Reset();
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
        // Контекст встречи тоже чистый: сводка/хвост не протекают через ResetConversation.
        _meetingContext?.Reset();
        _pauseBuf.Clear();
        SaveMeetingContextState();
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
            _memoryPublished.Set();   // памяти не будет - MCP-клиент не ждёт
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
            var extractor = memApi != null ? new MemoryExtractor(memApi, Logger,
                () => Config.Memory.ExtractMaxTokens,
                intensitySource: () => Config.Memory.ExtractionIntensity,
                // §8.1: фоновая память — отдельный счётчик, не «Потрачено за встречу».
                onUsage: res => AccountUsage(res, background: true)) : null;
            var consolidator = new MemoryConsolidator(memApi, embedder, Config.Memory, Logger,
                onUsage: res => AccountUsage(res, background: true));

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
            // Стор опубликован - MCP-клиенты, ждущие память, получают его сразу.
            _memoryPublished.Set();
            // §8: E5 опубликован — включаем смысловую ветку поиска встреч и
            // фоновый до-эмбеддинг уже проиндексированных реплик (без ожидания
            // следующего поискового запроса).
            try { MeetingSearch?.AttachEmbeddingProvider(() => MemoryEmbedder); }
            catch (Exception ex) { Logger.Warn($"Meeting index embedder attach failed: {ex.Message}", "Memory"); }
            Logger.Info("Memory worker started; retention+embed jobs enqueued", "Memory");

            // Встреча живёт от старта сессии — иначе сегменты до первого
            // «нового чата» писались бы в никуда (meeting row создавался только в Reset).
            // BeginMemoryMeeting может открывать PreCallBrief и дёргает оверлей →
            // только UI-поток; при фоновой инициализации из настроек маршаллим на Dispatcher.
            // Старт приложения не должен показывать окна поверх чата: встреча
            // создаётся сразу (сегменты до первого «нового чата» пишутся в неё),
            // но pre-call brief остаётся только для явного «нового чата».
            if (Dispatcher.CheckAccess())
                BeginMemoryMeeting(showPreCallBrief: false);
            else
                Dispatcher.InvokeAsync(() => { if (!_shuttingDown) BeginMemoryMeeting(showPreCallBrief: false); });
        }
        catch (Exception ex)
        {
            // Память — опция: сбой БД/моделей не должен ронять основной функционал.
            // Поля здесь НЕ трогаем — они ещё не публиковались (null); dispose локальных.
            Logger.Error("Memory init failed — memory disabled for this session", ex, "Memory");
            _memoryPublished.Set();   // памяти не будет - MCP-клиент не ждёт
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
    private void AppendJournalWithMemory(string role, string text, string? speaker = null,
        string? messageId = null, IReadOnlyList<Core.Images.ChatImageRef>? imgs = null)
    {
        MeetingJournal?.Append(role, text, speaker, messageId, imgs?.Select(i => i.ImageId).ToArray());

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
    private void BeginMemoryMeeting(bool showPreCallBrief = true)
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
            // R4: usage-счётчик узнаёт активную встречу — spend ask'ов, стартовавших
            // в ЭТОЙ встрече, копится в «Потрачено за встречу»; поздний spend
            // предыдущей (ответ в полёте на момент смены чата) уйдёт в фон.
            _tokenLedger.BeginMeeting(_currentMemoryMeetingId.Value.ToString());
            Logger.Info($"Memory: meeting created id={_currentMemoryMeetingId} mode={m.MemoryMode} use={m.UseMemory} write={m.WriteMemory}", "Memory");
            if (showPreCallBrief) MaybeShowPreCallBrief(m);
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
    private async Task<(string Prompt, IReadOnlyList<MeetingSearchHit> MeetingHits)> BuildSystemPromptWithMemoryAsync(string baseSystemPrompt, string queryText, CancellationToken ct, string? requestId = null, Action<string?>? progress = null)
    {
        // §7: журналы прошлых встреч — те же правила бюджета, что у memory recall:
        // неблокирующий ScanAsync, bounded-timeout, ошибки → без блока. Ищем при
        // непустом вопросе и включённой настройке Memory.MeetingSearchEnabled (не
        // только при включённой памяти): журналы есть и у встреч без фактов —
        // «что мы решили вчера» должно работать. Выключено → блока нет вовсе.
        var recall = MemoryRecall;
        var store = MemoryStore;
        // Спека «поиск по памяти и встречам», п.2: автоподбор (8000) делится
        // 5/8 фактам и 3/8 фрагментам встреч, дополнительный поиск модели
        // (32000) — отдельно, в tool-loop. Малому окну бюджеты урезаются по
        // фактическому входному контексту; 4000 токенов — грубый резерв под
        // историю/вложения/ответ.
        var budgets = MemoryBudgets.Plan(
            Config.Memory, Config.Api.ChatModel, Config.Api.BaseUrl,
            reservedTokens: (baseSystemPrompt.Length + 4000) / 4);
        var autoRid = requestId ?? "auto-" + Guid.NewGuid().ToString("N")[..8];
        var meetingChars = Math.Max(400, budgets.MeetingTokens * 4);
        // Этап в карточке — только при реальном поиске, не вместо любого ожидания.
        if (MeetingSearch != null && Config.Memory.MeetingSearchEnabled &&
            !string.IsNullOrWhiteSpace(queryText))
            progress?.Invoke(TT("L_Search_Meetings", "Проверяю прошлые встречи"));
        var (meetingHits, meetingSearched, meetingIncomplete, meetingSemanticPending,
            meetingResult) = await SearchPastMeetingsAsync(queryText, ct, autoRid);
        // §8: таймаут/строящийся смысловой индекс — честный блок «поиск не
        // завершён / перефразы позже», а не ложное «обсуждений не было».
        var meetingBlock = MeetingSearchService.BuildMeetingsContextBlock(meetingHits, meetingChars,
            meetingSearched, searchIncomplete: meetingIncomplete,
            semanticPending: meetingSemanticPending,
            dateFrom: meetingResult?.DateFrom, dateTo: meetingResult?.DateTo,
            dateFallback: meetingResult?.DateFallback ?? false);
        var scopeLabel = Config.Memory.ActiveProjectId is { } planPid
            ? $"project#{planPid}" : _currentMemoryMeetingId is { } planMid ? $"meeting#{planMid}" : "all";
        Logger.Info($"MEMDIAG rid={autoRid} plan q=\"{(queryText.Length > 200 ? queryText[..200] + "…" : queryText)}\" " +
                    $"scope={scopeLabel} window={budgets.Window} reserved={budgets.Reserved} " +
                    $"auto={budgets.AutoTokens} memory={budgets.MemoryTokens} meeting={budgets.MeetingTokens} " +
                    $"extra={budgets.ExtraSearchTokens} clamped={budgets.Clamped} " +
                    $"builtinTools={BuiltinToolsAvailable}", "Memory");
        var prompt = meetingBlock.Length > 0 ? meetingBlock + "\n\n" + baseSystemPrompt : baseSystemPrompt;
        // §6 аудита 16.10: действующий уровень интенсивности всегда в контексте —
        // чат отвечает на вопрос о настройке фактом, а не догадкой.
        var statusLine = MemoryPrompts.IntensityStatusLine(
            Config.Memory.ExtractionIntensity, Config.Memory.Enabled && store != null);
        var memoryState = RecallStepState.Disabled;
        var memoryHitsCount = 0;
        var memoryShown = 0;
        var memoryTruncated = false;
        var meetingState = !Config.Memory.MeetingSearchEnabled
            ? RecallStepState.Disabled
            : meetingIncomplete ? RecallStepState.Incomplete
            : meetingSearched ? (meetingHits.Count > 0 ? RecallStepState.Completed : RecallStepState.Empty)
            : RecallStepState.NotRun;
        string StatusBlock(RecallStepState memState) => MemoryContextStatus.Build(
            new MemoryContextStatusData
            {
                Now = DateTime.Now,
                MemoryEnabled = Config.Memory.Enabled && store != null,
                AutoRecallEnabled = Config.Memory.AutoRecall,
                RecallMaxItems = Config.Memory.RecallMaxItems,
                AutoRecallBudgetTokens = budgets.AutoTokens,
                ExtraSearchBudgetTokens = budgets.ExtraSearchTokens,
                BuiltinToolsAvailable = BuiltinToolsAvailable,
                MeetingSearchEnabled = Config.Memory.MeetingSearchEnabled,
                MemoryState = memState,
                MemoryHits = memoryHitsCount,
                MemoryShown = memoryShown,
                MemoryTruncated = memoryTruncated,
                MeetingState = meetingState,
                MeetingHits = meetingHits.Count,
                MeetingDateFrom = meetingResult?.DateFrom,
                MeetingDateTo = meetingResult?.DateTo,
                MeetingDateFallback = meetingResult?.DateFallback ?? false,
                MeetingStatus = meetingResult?.Status,
            });
        if (store == null || !Config.Memory.Enabled)
            return (StatusBlock(RecallStepState.Disabled) + "\n" + prompt + statusLine, meetingHits);

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

        if (useMemory && recall != null && Config.Memory.AutoRecall && !string.IsNullOrWhiteSpace(queryText))
        {
            progress?.Invoke(TT("L_Search_Memory", "Ищу в памяти"));
            var sw = Stopwatch.StartNew();
            memoryState = RecallStepState.Incomplete;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(MemoryRecallTimeoutMs);
                var projId = Config.Memory.ActiveProjectId;
                // Бюджеты (настройки памяти): обычный ответ получает
                // AutoRecallBudgetTokens (8000), а явная просьба «все/полный
                // список» — плюс ExtraSearchBudgetTokens и без лимита по числу
                // фактов: иначе модель физически не может выполнить просьбу.
                var wantsAll = MemoryRecallService.QueryWantsFullList(queryText);
                // Полносписочный запрос без встроенных инструментов не может
                // дочитать данные вторым шагом — отдаём ему дополнительный
                // бюджет сразу. С инструментами остаток остаётся tool-loop.
                var budget = budgets.MemoryTokens +
                    (wantsAll && !BuiltinToolsAvailable ? budgets.ExtraSearchTokens : 0);
                var hits = await recall.RecallAsync(new MemoryRecallQuery(
                    queryText,
                    Scope: projId.HasValue ? MemoryScopeType.Project : null,
                    ScopeId: projId,
                    MeetingId: mid,
                    // Лимит фактов снимает сам recall при полносписочном
                    // запросе (QueryWantsFullList); здесь — обычная настройка.
                    MaxItems: Config.Memory.RecallMaxItems,
                    MaxTokens: budget), linked.Token,
                    trace: t => Logger.Info($"MEMDIAG rid={autoRid} recall " +
                        $"q=\"{(queryText.Length > 120 ? queryText[..120] + "…" : queryText)}\" " +
                        $"ftsCandidates={t.FtsCandidates} vectorCandidates={t.VectorCandidates} " +
                        $"results={t.Results} embeddings={t.EmbeddingsAvailable} " +
                        $"embeddingsFailed={t.EmbeddingsFailed}", "Memory"));
                var detailed = ContextBlockBuilder.BuildDetailed(hits, budget);
                memoryHitsCount = hits.Count;
                memoryShown = detailed.Shown;
                memoryTruncated = detailed.Truncated;
                memoryState = hits.Count == 0 ? RecallStepState.Empty : RecallStepState.Completed;
                var block = detailed.Text;
                // Честный статус: «найдено / найдено частично / ничего не
                // найдено» прямо в блоке — модель обязана отличать «данных
                // нет» от «нашлось соседнее» и не выдавать усечение за полный
                // список (маркер усечения ставит ContextBlockBuilder).
                var blockTruncated = detailed.Truncated;
                var statusText = MemoryPrompts.RecallStatusLine(hits, blockTruncated);
                if (string.IsNullOrWhiteSpace(block))
                    block = $"<memory_context>\n{statusText}\n</memory_context>";
                else
                    block = block.Replace("<memory_context>\n",
                        $"<memory_context>\n{statusText}\n", StringComparison.Ordinal);
                Logger.Info($"Memory recall: {hits.Count} hits, budget {budget} tokens{(wantsAll ? " (full list)" : "")}" +
                            $" in {sw.ElapsedMilliseconds}ms", "Memory");
                Logger.Info($"MEMDIAG rid={autoRid} block shown={detailed.Shown}/{detailed.Total} " +
                            $"collapsed={detailed.Collapsed} truncated={detailed.Truncated} " +
                            $"budget={budget}p chars={block.Length} meetingChars={meetingChars} " +
                            $"meetingHits={meetingHits.Count} meetingIncomplete={meetingIncomplete}", "Memory");
                // Диагностика выдачи: какие именно факты вошли в контекст (id + score +
                // причина), чтобы «поиск не нашёл» можно было разобрать по журналу.
                var byId = hits.ToDictionary(h => h.Item.Id);
                Logger.Info($"MEMDIAG rid={autoRid} selected=" + string.Join(" | ",
                    (detailed.SelectedIds ?? Array.Empty<long>())
                        .Select(id => byId.TryGetValue(id, out var h)
                            ? $"{id}:{h.Score:F3}{(h.Why.Length > 0 ? "[" + h.Why + "]" : "")}"
                            : id.ToString())),
                    "Memory");
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
                memoryState = RecallStepState.Incomplete;
                Logger.Warn($"Memory recall: timeout {MemoryRecallTimeoutMs}ms — asking without memory", "Memory");
                Logger.Info($"MEMDIAG rid={autoRid} recall timeout={MemoryRecallTimeoutMs}ms reason=timeout", "Memory");
            }
            catch (Exception ex)
            {
                memoryState = RecallStepState.Error;
                Logger.Warn($"Memory recall failed: {ex.Message} — asking without memory", "Memory");
                Logger.Info($"MEMDIAG rid={autoRid} recall reason=error type={ex.GetType().Name}", "Memory");
            }
        }

        prompt = StatusBlock(memoryState) + "\n" + prompt;
        if (writeMemory)
            prompt += MemoryRulesPromptSuffix;
        // Встроенные инструменты доступны модели всегда (без внешнего MCP):
        // без инструкции модель не знает, что можно дочитать память сама.
        if (BuiltinToolsAvailable)
            prompt += BuiltinSearchPromptSuffix;
        prompt += statusLine;
        return (prompt, meetingHits);
    }

    /// <summary>Поиск фрагментов прошлых встреч для контекста ответа (§7, §8).
    /// Возвращает (хиты, searched, incomplete, semanticPending):
    /// searched=false — поиск не состоялся (выключен/пустой запрос/нет стора/сбой);
    /// incomplete=true — поиск оборвался по бюджету (sync/индекс не успели; либо
    /// точные хиты есть, а смысловая ветка не уложилась), и ответ обязан сказать
    /// «поиск не завершён», а не «обсуждений не было»;
    /// semanticPending=true — векторная обвязка E5 ещё строится (перефразы позже).</summary>
    private async Task<(IReadOnlyList<MeetingSearchHit> Hits, bool Searched, bool Incomplete,
        bool SemanticPending, MeetingSearchResult? Result)> SearchPastMeetingsAsync(
        string queryText, CancellationToken ct, string? requestId = null)
    {
        var search = MeetingSearch;
        if (search == null || string.IsNullOrWhiteSpace(queryText))
            return (Array.Empty<MeetingSearchHit>(), false, false, false, null);
        // Явный opt-out: журналы не читаем и НЕ добавляем в промпт блок
        // «совпадений нет» — обычный ответ не должен выглядеть как неудачный поиск.
        if (!Config.Memory.MeetingSearchEnabled)
            return (Array.Empty<MeetingSearchHit>(), false, false, false, null);

        // Бюджет из настроек; диапазон 500-10000 мс — тот же, что клампит
        // SettingsViewModel, но App читает конфиг напрямую: ручная правка
        // config.json не должна ни подвесить ответ, ни отменить поиск сразу.
        var timeoutMs = Math.Clamp(Config.Memory.MeetingSearchTimeoutMs, 500, 10_000);
        var sw = Stopwatch.StartNew();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            var res = await search.SearchDetailedAsync(queryText, linked.Token,
                new MeetingSearchOptions
                {
                    ExcludeMeetingId = MeetingJournal?.CurrentId,
                    MaxResults = 8,
                    // Дата из вопроса — фильтр встречи, а не слово внутри
                    // транскрипта; если встреч в периоде нет, блок честно
                    // скажет об этом вместо «совпадений нет».
                    IncludeMeetingsInRange = true,
                    // Точные/FTS-хиты, уже найденные до истечения бюджета, важнее
                    // необязательной смысловой ветки: отдаём их с incomplete-статусом.
                    KeepLexicalOnSemanticTimeout = true,
                });
            // Внешняя отмена (новый вопрос/закрытие) — не результат поиска:
            // ни блока, ни ложной отметки «поиск не завершён».
            if (ct.IsCancellationRequested)
                return (Array.Empty<MeetingSearchHit>(), false, false, false, null);
            Logger.Info($"Meeting search: {res.Hits.Count} hits in {sw.ElapsedMilliseconds}ms"
                + (res.Status.SemanticTimedOut ? " (semantic timed out — lexical hits kept)"
                    : res.Status.Incomplete ? " (incomplete: index still syncing)" : ""), "Memory");
            Logger.Info($"MEMDIAG rid={requestId} meetings q=\"{(queryText.Length > 120 ? queryText[..120] + "…" : queryText)}\" " +
                        $"hits={res.Hits.Count} incomplete={res.Status.Incomplete} " +
                        $"semanticAvailable={res.Status.SemanticAvailable} " +
                        $"semanticPending={res.Status.SemanticPending} " +
                        $"semanticTimedOut={res.Status.SemanticTimedOut} elapsed={sw.ElapsedMilliseconds}ms", "Memory");
            return (res.Hits, true, res.Status.Incomplete, res.Status.SemanticPending, res);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logger.Warn($"Meeting search: timeout {timeoutMs}ms — answer marked incomplete", "Memory");
            Logger.Info($"MEMDIAG rid={requestId} meetings timeout={timeoutMs}ms reason=timeout", "Memory");
            return (Array.Empty<MeetingSearchHit>(), false, true, SemanticPendingSafe(search), null);
        }
        catch (OperationCanceledException) { /* внешняя отмена — просто без блока */ }
        catch (Exception ex)
        {
            Logger.Warn($"Meeting search failed: {ex.Message} — asking without journals", "Memory");
            Logger.Info($"MEMDIAG rid={requestId} meetings reason=error type={ex.GetType().Name}", "Memory");
        }
        return (Array.Empty<MeetingSearchHit>(), false, false, false, null);
    }

    /// <summary>SemanticPending без риска уронить путь ответа (статус читает SQLite).</summary>
    private static bool SemanticPendingSafe(MeetingSearchService search)
    {
        try { return search.GetStatus().SemanticPending; }
        catch { return false; }
    }

    /// <summary>Оверрайд системного промпта для прямых вопросов оператора: поле ввода
    /// и «прокрутить буфер» — ВСЕГДА явный диалог; правило суфлёра «можешь молчать»
    /// на них не действует (иначе «Отправлено в память»/секретные вопросы падают в
    /// осознанное молчание).</summary>
    private const string ManualPurposeClause =
        "\n\n[secret] = ПРЯМОЙ вопрос оператора (не реплика собеседника). " +
        "Отвечай ВСЕГДА — правило «можешь промолчать» на такие вопросы не действует. " +
        "Если вопрос неоднозначен — уточни, а не молчи.";

    /// <summary>Итоговый системный промпт для ask: memory-контекст + правила памяти +
    /// сводка встречи (plan.SummaryBlock) + пометка «ранний контекст утерян» +
    /// purpose-оверрайд (ManualChat отключает правило молчания).</summary>
    private static string ApplyContextToPrompt(string memoryPrompt, string? summaryBlock,
        bool earlyContextDropped, AskPurpose purpose)
    {
        var prompt = memoryPrompt;
        // Сводка старшего контекста — после memory-контекста, до остального:
        // модель опирается на неё как на предысторию, а не на лимиты.
        if (!string.IsNullOrEmpty(summaryBlock))
            prompt += $"\n\nКонтекст этой встречи (сводка ранних реплик):\n{summaryBlock}\n(свежие реплики — в истории ниже)";
        if (earlyContextDropped)
            prompt += "\n(часть раннего контекста этой встречи пока недоступна — идёт свёртка)";
        if (purpose == AskPurpose.ManualChat)
            prompt += ManualPurposeClause;
        return prompt;
    }

    private List<ChatMessage> SnapshotLegacyHistory()
    {
        lock (_historyLock) return new List<ChatMessage>(_conversationHistory);
    }

    /// <summary>Состояние контекста встречи → sidecar meta.json (переживает рестарт).
    /// Best-effort: журнал и без этого пишет реплики, контекст — вторичный.</summary>
    private void SaveMeetingContextState()
    {
        try
        {
            var ctx = _meetingContext;
            if (ctx == null || MeetingJournal == null) return;
            // Sidecar привязан к файлу встречи: гарантируем, что встреча существует,
            // иначе мета запишется не туда (вызов при ResetConversation до первой реплики).
            if (MeetingJournal.CurrentId == null) return; // пустой state не создаёт встречу
            MeetingJournal.SaveContextState(JsonSerializer.Serialize(ctx.State));
        }
        catch (Exception ex) { Logger.Warn($"Context save failed: {ex.Message}", "Context"); }
    }

    /// <summary>Восстановить сводку/прогресс последней встречи после аварийного выхода:
    /// читаем sidecar самого свежего meta.json + дословный хвост реплик из её jsonl.</summary>
    private void TryRestoreContextFromLatestMeeting()
    {
        try
        {
            var ctx = _meetingContext;
            if (ctx == null || MeetingJournal == null) return;
            var latest = MeetingJournal.LoadAll().FirstOrDefault(); // LoadAll уже сортирован: свежие сверху
            if (latest == null) return;
            var stateJson = MeetingJournal.LoadContextState(latest.Id);
            MeetingContextState? restored = null;
            if (!string.IsNullOrEmpty(stateJson))
            {
                try { restored = JsonSerializer.Deserialize<MeetingContextState>(stateJson); }
                catch (Exception dx) { Logger.Warn($"ContextState parse failed: {dx.Message}", "Context"); }
            }
            var journalPath = latest.JournalPath;
            var tailJsonl = !string.IsNullOrEmpty(journalPath) && File.Exists(journalPath)
                ? journalPath : null;
            ctx.Restore(restored, ReadMeetingTailPairs(tailJsonl));
            Logger.Info($"Meeting context restored: meeting={latest.Id}, summary={restored?.RollingSummary != null}", "Context");
        }
        catch (Exception ex) { Logger.Warn($"Context restore failed: {ex.Message}", "Context"); }
    }

    /// <summary>Хвост реплик встречи из jsonl-журнала → пары (question→answer).
    /// Маппинг как у истории: question/user — реплика оператора, answer — ответ.</summary>
    private static List<(string Question, string Answer)> ReadMeetingTailPairs(string? jsonlPath)
    {
        var pairs = new List<(string Question, string Answer)>();
        if (jsonlPath == null) return pairs;
        try
        {
            // Читаем только хвост (сводка покрывает остальное) — пара-тройка экранов текста.
            var lines = File.ReadLines(jsonlPath).TakeLast(MeetingContextManager.VerbatimTailPairs * 2 + 8).ToList();
            string? pendingQ = null;
            foreach (var line in lines)
            {
                MeetingJournalLine? e;
                try { e = JsonSerializer.Deserialize<MeetingJournalLine>(line); }
                catch { continue; }
                if (e == null || string.IsNullOrWhiteSpace(e.Text)) continue;
                if (e.Role == "answer")
                {
                    if (pendingQ != null) { pairs.Add((pendingQ, e.Text)); pendingQ = null; }
                }
                else if (e.Role is "question" or "user")
                {
                    // Несколько вопросов без ответа подряд → мостим одним вопросом
                    // (иначе пара теряет более ранние реплики — поведение истории LLM).
                    pendingQ = pendingQ == null ? e.Text : pendingQ + "\n" + e.Text;
                }
            }
        }
        catch { /* хвост — лучшая попытка, журнал может быть частично повреждён */ }
        return pairs;
    }

    /// <summary>Индикатор «контекст Nk / Сводка…» в статус-строке оверлея.</summary>
    private void UpdateContextInfo(int estimatedTokens, int budgetTokens)
    {
        try
        {
            var ctx = _meetingContext;
            if (ctx == null) { _overlay?.SetContextInfo(null, false); return; }
            var text = string.Format(TT("L_Ctx_Info", "Контекст: ~{0:0.#}k / {1:0.#}k"),
                estimatedTokens / 1000.0, budgetTokens / 1000.0);
            _overlay?.SetContextInfo(text, ctx.State.SummaryInFlight);
        }
        catch { /* индикатор — вспомогательный, не должен ронять ask */ }
    }

    /// <summary>Фоновая сводка: генерируется ОДИН раз на встречу (план выставил флаг).
    /// Идёт тем же ApiClient; промпт-правила памяти тут неуместны — raw-база.</summary>
    private void RunContextSummaryAsync()
    {
        var ctx = _meetingContext;
        if (ctx == null) return;
        // §8.2: выключенный/приостановленный LLM не должен запускать скрытую
        // платную сводку по старой речи — задача просто не ставится.
        if (Config.Ui.LlmDisabled || IsLlmPaused)
        {
            Logger.Info("Context summary skipped: LLM disabled/paused", "Context");
            return;
        }
        // Единственное задание на встречу: метим до запуска, иначе каждый
        // ScheduleSummary-запрос спавнил бы параллельную сводку (индикатор
        // «Обновляю контекст…» при этом никогда не загорался).
        if (!ctx.TryMarkSummaryInFlight()) return;
        var msgs = ctx.TakeSummarizableSnapshot();
        if (msgs.Count == 0) { ctx.MarkSummaryDone(); return; }
        var coveredPairs = msgs.Count / 2;
        var priorSummary = ctx.State.RollingSummary;
        // R4: сводка принадлежит встрече, для которой поставлена — ключ фиксируем на
        // старте задачи. Доехавшая после смены чата, она уйдёт в фон (не надувает
        // «Потрачено за встречу» новой встречи).
        var summaryMeetingKey = MeetingSpendKey;
        _ = Task.Run(async () =>
        {
            try
            {
                var prompt = ContextSummarizer.BuildPrompt(priorSummary, msgs);
                // §5 аудита 16.10: отдельный бюджет фона + один повтор при исчерпании.
                // Reasoning-модель иногда съедает все 2000 на скрытые размышления и
                // отдаёт пустой content; повтор с удвоенным бюджетом спасает сводку,
                // иначе контекст не сжимается до конца встречи.
                var res = await ContextSummarizer.AskWithBudgetRetryAsync(
                    budget => ApiClient.AskAsync(prompt,
                        ContextSummarizer.SystemPrompt, budget,
                        new List<ChatMessage>(), CancellationToken.None,
                        background: true),
                    Math.Min(ContextSummarizer.DefaultMaxTokens, Config.Advanced.MaxResponseTokens),
                    // R4: usage учитываем на КАЖДОЙ попытке сводки, в т.ч. пустой/неудачной —
                    // провайдер уже списал токены (раньше расход повтора терялся).
                    r => AccountUsage(r, background: true, meetingKey: summaryMeetingKey));
                if (!string.IsNullOrWhiteSpace(res?.Content))
                {
                    // §8.2: кумулятивная сводка — ранние решения начала встречи
                    // переживают серию сжатий (новая сводка строится над старой).
                    var merged = ContextSummarizer.MergeSummary(priorSummary, res.Content);
                    // §8.2: публикуем РОВНО снимок; реплики, пришедшие во время
                    // генерации, остаются в хвосте и уйдут в следующую сводку.
                    ctx.PublishSummary(merged, coveredPairs);
                    SaveMeetingContextState();
                    Logger.Info($"Meeting summary updated ({merged.Length} chars, " +
                        $"compressed {coveredPairs} pairs, usage {res.PromptTokens}+{res.CompletionTokens}t)", "Context");
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        // Обновить индикатор «Сводка…»/«контекст Nk» после публикации:
                        // точные числа пересчитает следующий BuildPlan, здесь — флаг.
                        _overlay?.SetContextInfo(null, ctx.State.SummaryInFlight);
                        RefreshOverlayStatus();
                    });
                }
                else
                {
                    // Нет смысла крутить заново на следующем чанке: ждём новых пар —
                    // триггер выставится сам по бюджету/переполнению хвоста.
                    // §8.2: при пустом ответе хвост НЕ тронут — ранние реплики целы.
                    ctx.MarkSummaryDone();
                    // §5: причина должна быть видна в логе — сеть/HTTP, отмена и
                    // исчерпанный бюджет лечатся по-разному.
                    if (!string.IsNullOrWhiteSpace(res?.Error))
                        Logger.Warn($"Meeting summary failed: {res.Error}", "Context");
                    else
                        Logger.Warn($"Meeting summary: empty response " +
                            $"(kind={res?.EmptyKind}, finish={res?.FinishReason ?? "n/a"}, " +
                            $"usage={res?.PromptTokens}+{res?.CompletionTokens}t), " +
                            "prior summary kept, will retry on next trigger", "Context");
                }
            }
            catch (OperationCanceledException)
            {
                ctx.MarkSummaryDone();
                Logger.Info("Meeting summary canceled", "Context");
            }
            catch (Exception ex)
            {
                // §8.2: провал сводки НЕ удаляет реплики — просто снимаем in-flight,
                // следующий триггер попробует снова. Ранние сообщения на месте,
                // в логе — причина.
                ctx.MarkSummaryDone();
                Logger.Warn($"Meeting summary failed: {ex.Message}", "Context");
            }
        });
    }

    // ===== Отмена/повтор ответов (кнопки на пузыре, контракт OverlayOps) =====

    /// <summary>Отмена конкретного ожидающего ответа (✕ на пузыре «отправляю…»).</summary>
    public void CancelAnswer(int requestId)
    {
        _overlay?.CancelRequest(requestId);
        // Открытый MCP-аппрувал при отмене отклоняем — иначе tool-loop
        // останется висеть на карточке снятого запроса.
        _overlay?.CancelMcpApproval();
        RefreshOverlayStatus();
        Logger.Info($"CancelAnswer: request #{requestId} cancelled", "Answer");
    }

    /// <summary>Повтор отправки того же вопроса в тот же пузырь (↻ после ошибки/отмены).</summary>
    public void RetryAnswer(QaPair pair)
    {
        if (!_askRetryArgs.TryGetValue(pair, out var args))
        {
            Logger.Warn("RetryAnswer: no saved args for pair — bubble not re-askable", "Answer");
            _overlay?.MarkFailed(pair, TT("L_Answer_NoRetry", "Повтор недоступен"), DateTime.Now, 0);
            return;
        }
        _overlay?.ReopenForRetry(pair);
        _overlay?.SetWaitingText(pair, TT("L_Answer_Waiting", "Отправляю вопрос…"));
        _ = Task.Run(async () => await RunManualAskAsync(pair, args.Text, args.IsScreen,
            fromRetry: true, messageId: args.MessageId, imageTiles: args.ImageTiles));
    }

    /// <summary>Правила inline-директив памяти в конце системного промпта (добавляется,
    /// когда у встречи активна запись). Формат разбирает MemoryDirectiveParser.</summary>
    /// <summary>Инструкции самостоятельного поиска: модель проверяет сохранённое
    /// до утверждений «данных нет» и уточняет запрос, если первой подборки мало
    /// (спека «поиск по памяти и встречам», п.3).</summary>
    private const string BuiltinSearchPromptSuffix =
        "\n\nВСТРОЕННЫЙ ПОИСК BUDDY (инструменты доступны, пользователь их не выбирает):\n" +
        "- Вопрос о сохранённой информации, прошлых договорённостях или встречах — СНАЧАЛА проверь " +
        "данные инструментами памяти/встреч, не полагайся только на автоматическую подборку ниже.\n" +
        "- Если <memory_context> или фрагменты встреч не покрывают вопрос — вызови поиск по памяти " +
        "и/или по прошлым встречам со своими ключевыми словами (синонимы, короткие обозначения вроде QA, ML, " +
        "номера задач). Найденный источник можно прочитать целиком.\n" +
        "- Не повторяй тот же запрос: уточняй формулировку. Остановись, когда сведений достаточно " +
        "или инструмент сообщил об исчерпании бюджета.\n" +
        "- НЕ утверждай «в памяти/встречах этого нет», пока не выполнил поиск. Если поиск вернул " +
        "ограничение, усечение или неполный статус — скажи об этом прямо.\n" +
        "- Если ответа действительно нет — начни ответ словами «В проверенных источниках нет данных» " +
        "и не подменяй ответ соседними фактами.\n" +
        "- Различай происхождение: role=user/participant — слова участника; role=answer — ответ ИИ; " +
        "role=summary — AI-резюме; provenance=extraction/llm-directive/attachment-directive — " +
        "извлечённый факт, а не цитата. Цитируй только дословный text своей строки; не склеивай " +
        "соседние реплики и не приписывай слова участника ответу ИИ.\n" +
        "- В ответе укажи, на какие факты/фрагменты встреч опираешься (id источника из результата).";

    private const string MemoryRulesPromptSuffix =
        "\n\nПРАВИЛА ПАМЯТИ:\n" +
        "- Если собеседник ЯВНО просит запомнить/внести в память/обновить факт — добавь в САМОЕ НАЧАЛО ответа директиву: " +
        "[[MEMORY_ADD]]краткий факт[[/MEMORY_ADD]], для правки: " +
        "[[MEMORY_UPDATE]]старый факт→новый факт[[/MEMORY_UPDATE]], для забывания: " +
        "[[MEMORY_FORGET]]факт[[/MEMORY_FORGET]].\n" +
        "- Максимум 2 директивы за ответ, каждая — ОДИН факт. НИКОГДА не повторяй директиву в следующих ответах — " +
        "один раз выдал, дальше хранилище само.\n" +
        "- В блоке <memory_context> уже подгружены известные факты: если факт (или та же мысль другими словами) " +
        "уже там — директиву НЕ выдавай вообще.\n" +
        "- Директивы скрыты от пользователя: НИКОГДА не повторяй их текст, слово «MEMORY_» и «записал/запомнил» " +
        "в видимой части ответа.";

    /// <summary>Локализованная строка по ключу с фолбэком (для строк, которых ещё может не быть в словарях).</summary>
    internal static string TT(string key, string fallback) =>
        Application.Current.TryFindResource(key) as string ?? fallback;

    /// <summary>Понятная причина пустого ответа по <see cref="AskResult.EmptyKind"/>.
    /// Совет «увеличьте лимит» показываем ТОЛЬКО при LengthExhausted (бюджет съеден
    /// размышлениями); пустой ответ при stop/повреждённом теле/внутреннем лимите
    /// провайдера лимитом токенов не объясняется (инцидент 2026-10-12: совет вводил
    /// в заблуждение — completion был 100/1500 и 278/3500 при finish_reason=stop).</summary>
    private static string EmptyAnswerHint(AskResult result, bool silentByStrip) =>
        result.EmptyKind switch
        {
            EmptyAnswerKind.LengthExhausted =>
                TT("L_Err_TokenBudget", "Лимит токенов исчерпан размышлениями модели. Увеличьте лимит токенов в настройках."),
            EmptyAnswerKind.ProviderCap =>
                TT("L_Err_ProviderCap", "Ответ обрезан внутренним лимитом провайдера. Попробуйте другую модель или провайдера."),
            EmptyAnswerKind.TransportOrParse =>
                TT("L_Err_TransportParse", "Ответ модели не распознан (повреждённый ответ сервера). Повторите запрос."),
            // ExpectedSilence или стрипленая директива — допустимое молчание/фильтр.
            var kind when kind == EmptyAnswerKind.ExpectedSilence || silentByStrip =>
                TT("L_Err_ModelSilent", "Модель промолчала (пустой ответ). Попробуйте переформулировать."),
            _ => TT("L_Err_LlmEmpty", "пустой ответ от LLM"),
        };

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
            var applied = await new MemoryWriter(store, Logger, MemoryConsolidator as MemoryConsolidator)
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
        bool directivesStripped, string cleanAnswer, int streamId = 0)
    {
        if (!stream) return;
        if (isSilent || (gotDelta && directivesStripped))
        {
            _karaoke?.DropStreamLine(streamId);
            if (!isSilent && !string.IsNullOrWhiteSpace(cleanAnswer))
                _karaoke?.PushLine("answer", cleanAnswer);
        }
        else
        {
            _karaoke?.EndStreamLine(streamId);
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
            Logger.Info($"AskMemory ignored: memory off (Enabled={Config.Memory.Enabled}, store={MemoryStore != null})", "Memory");
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
        // MCP: входящий pipe-хост включается/гасится на лету; у исходящих серверов
        // конфиг перечитывается на каждый запрос (оркестратор держит Func), а
        // закешированные stdio-подключения рвём — реконнект пойдёт по новым параметрам.
        if (Config.Mcp.Enabled && Config.Mcp.StdioHostEnabled && McpHost == null && !_shuttingDown)
        {
            try
            {
                McpJournal ??= new Core.Mcp.McpIncomingJournal(
                    Path.Combine(AppDataDir, "logs"), mirror: Logger);
                McpHost = Core.Mcp.McpIncomingHost.TryStart(
                    Config.Mcp, AppDataDir, MeetingJournal!, MemoryStore, _attachmentStore, Logger,
                    McpJournal, memoryProvider: () => MemoryStoreForMcp());
                if (McpHost != null) Logger.Info($"MCP incoming on pipe {McpHost.PipeName}", "Mcp");
                AttachMcpFacadeActivity(McpHost?.Facade);
            }
            catch (Exception ex) { Logger.Warn($"MCP host start failed: {ex.Message}", "Mcp"); }
            SettingsVm.McpIncomingPipe = McpHost?.PipeName;
        }
        else if ((!Config.Mcp.Enabled || !Config.Mcp.StdioHostEnabled) && McpHost != null)
        {
            DetachMcpFacadeActivity(McpHost.Facade);
            try { McpHost.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            McpHost = null;
            SettingsVm.McpIncomingPipe = null;
        }
        // HTTP-ingress: живой тоггл без рестарта (симметрично pipe-блоку выше).
        if (Config.Mcp.Enabled && Config.Mcp.HttpEnabled && McpHttp == null && !_shuttingDown)
        {
            _ = TryStartMcpHttpAsync();
        }
        else if ((!Config.Mcp.Enabled || !Config.Mcp.HttpEnabled) && McpHttp != null)
        {
            try { McpHttp.Facade.PendingClientAdded -= OnMcpPendingClient; } catch { }
            // Отписываемся только от СВОЕГО фасада: при pipe-хосте HTTP-ingress
            // использует тот же экземпляр — его подписку трогать нельзя.
            if (_httpFacade != null) DetachMcpFacadeActivity(_httpFacade);
            try { McpHttp.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            McpHttp = null;
        }
        if (McpChat != null) _ = McpChat.RefreshServersAsync();
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
        Logger.Info($"LLM {(Config.Ui.LlmDisabled ? "DISABLED (чистый транскрибатор, речь в буфер)" : "enabled")}", "App");
        // Плашка «Ошибка ИИ (LLM)» в этом режиме неуместна — пересобираем баннер сразу.
        UpdateConnectionNotice();
        SaveConfigSafe();
        // Гейт OCR-очереди изменился: при включении могут появиться «ждущие» снимки —
        // обновляем тост отложенных (§2.7-2.8 аудита: сами не отправляем, ждём «Отправить»).
        OnOcrLlmGateChanged();
        if (!Config.Ui.LlmDisabled && !IsLlmPaused)
        {
            // Включили обратно: пропущенная за «LLM выкл» речь уходит ОДНИМ
            // запросом — та же механика, что у снятия паузы LLM (спека п.15).
            // Пауза всё ещё стоит → буфер ждёт ToggleLlmPause, ничего не теряем.
            var pauseImageIds2 = _pauseBuf.SnapshotImageIds();
            var paused = _pauseBuf.DrainAsSingleRequest(out var pausedCount);
            if (!string.IsNullOrWhiteSpace(paused))
            {
                Logger.Info("LLM re-enabled — сливаю буфер пропущенного одним запросом", "App");
                _ = Task.Run(() => DrainPauseBufferAsync(paused, pausedCount, pauseImageIds2));
            }
        }
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

    /// <summary>
    /// Тест-инъекция речи (env BUDDY_SPEECH_INJECT_FILE): тейлит файл и кладёт строки
    /// "<src>\t<текст>" в chunk-очередь как сентинел "BTX:src:text". Воркер минует
    /// только захват+STT — пузырь/журнал/pause-буфер/очередь ответов работают живьём.
    /// В проде без env — полностью выключено (одна проверка на старт MainLoop).
    /// </summary>
    private void StartSpeechInjectionWatcher(
        System.Threading.Channels.ChannelWriter<ChunkItem> writer, CancellationToken ct)
    {
        var path = Environment.GetEnvironmentVariable("BUDDY_SPEECH_INJECT_FILE");
        if (string.IsNullOrWhiteSpace(path)) return;
        Logger.Warn($"Speech injection ENABLED: tail '{path}' (test harness only)", "Loop");
        _ = Task.Run(async () =>
        {
            try
            {
                long pos = File.Exists(path) ? new FileInfo(path).Length : 0; // хвост
                using var fs = new FileStream(path, FileMode.OpenOrCreate,
                    FileAccess.Read, FileShare.ReadWrite);
                var pending = "";
                while (!ct.IsCancellationRequested)
                {
                    if (fs.Length > pos)
                    {
                        fs.Position = pos;
                        using var sr = new StreamReader(fs, Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: true, bufferSize: 4096,
                            leaveOpen: true);
                        var chunk = await sr.ReadToEndAsync();
                        pos = fs.Length;
                        pending += chunk;
                        int nl;
                        while ((nl = pending.IndexOf('\n')) >= 0)
                        {
                            var line = pending[..nl].TrimEnd('\r');
                            pending = pending[(nl + 1)..];
                            if (line.Length == 0) continue;
                            var tab = line.IndexOf('\t');
                            var src = tab > 0 ? line[..tab] : "loopback";
                            var txt = tab > 0 ? line[(tab + 1)..] : line;
                            if (txt.Length == 0) continue;
                            var sent = Encoding.UTF8.GetBytes($"BTX:{src}:{txt}");
                            if (writer.TryWrite(new ChunkItem(sent, src)))
                                Logger.Info($"SpeechInject [{src}]: '{txt.Substring(0, Math.Min(50, txt.Length))}'", "Loop");
                        }
                    }
                    await Task.Delay(150, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn($"SpeechInject watcher died: {ex.Message}", "Loop"); }
        }, ct);
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
            // Спека п.15: накопленный за паузу контекст уходит ОДНИМ запросом —
            // без очереди на каждую пропущенную реплику.
            var pausedCount = 0;
            var pauseImageIds = Config.Ui.LlmDisabled ? null : _pauseBuf.SnapshotImageIds();
            var paused = Config.Ui.LlmDisabled ? null : _pauseBuf.DrainAsSingleRequest(out pausedCount);
            // Если сняли паузу, но кнопка «LLM выкл» всё ещё активна — буфер
            // остаётся и уйдёт одним запросом при включении LLM обратно.
            if (!string.IsNullOrWhiteSpace(paused))
            {
                Logger.Info("LLM resume — сливаю pause-буфер одним запросом", "App");
                _ = Task.Run(() => DrainPauseBufferAsync(paused, pausedCount, pauseImageIds));
            }
            // OCR-гейт: пауза снята — отложенные снимки теперь доступны для «Отправить».
            OnOcrLlmGateChanged();
        }
        else
        {
            IsLlmPaused = true;
            Logger.Info("LLM paused (audio still recording)", "App");
            RefreshOverlayStatus();
            _overlay?.RefreshLlmPauseButton();
            Notifier.ShowInfo(TT("L_Toast_LlmPaused", "Пауза LLM"), TT("L_Toast_LlmPausedBody", "Вопросы не отправляются в LLM, аудио продолжает записываться"));
            // OCR-гейт закрылся: покажем «ждут LLM» у вновь отложенных.
            OnOcrLlmGateChanged();
        }
    }

    /// <summary>Слитый pause-буфер (спека п.15): один пузырь с КОМПАКТНОЙ меткой
    /// (настраиваемая Ui.PauseResumeMarker + число реплик) + один ask через общий
    /// движок (isManual=false → AskPurpose.BackgroundSpeech, без правила «прямой
    /// вопрос оператора»). Текст-инструкция уходит только в LLM — в пузырь, журнал
    /// и караоке попадает маркер, а не многострочная свалка (раньше писался
    /// весь [pause]-блок — засорял чат и журнал встречи).</summary>
    private async Task DrainPauseBufferAsync(string pausedText, int itemCount,
        IReadOnlyList<string>? imageIds = null)
    {
        // Гонка: пока Task.Run разгонялся, юзер мог снова выключить LLM или
        // поставить паузу — текст НЕ теряем, возвращаем в буфер одним блоком.
        if (Config.Ui.LlmDisabled || IsLlmPaused)
        {
            Logger.Info("Drain deferred — LLM снова выключен/на паузе, текст возвращён в буфер", "App");
            _pauseBuf.Add(label: "", text: pausedText, imageIds: imageIds?.ToArray());
            return;
        }
        var marker = Config.Ui.PauseResumeMarker?.Trim();
        if (string.IsNullOrEmpty(marker))
            marker = TT("L_PauseMarkerDefault", "⏸ За период паузы");
        var displayMarker = $"{marker} ({itemCount})";

        // imageIds из буфера → тайлы для пузыря и payload (спека §9: картинки
        // периода паузы едут одним запросом вместе с текстом).
        var tiles = ChatImages != null && imageIds?.Count > 0
            ? imageIds.Select(id => ChatImages.GetTile(id)).Where(t => t != null).Cast<ChatImageTileVm>().ToList()
            : null;
        if (tiles?.Count > 0)
            displayMarker += $" +{tiles.Count}🖼";

        QaPair? pair = null;
        await Dispatcher.InvokeAsync(() =>
        {
            pair = _overlay?.AddSendingItem(displayMarker, 0, DateTime.Now, null, null, 0, imageTiles: tiles);
            _overlay?.SetStatus(TT("L_Status_ThinkingPaused", "Отвечаю на пропущенное…"));
            _karaoke?.PushLine("question", displayMarker);
            AppendJournalWithMemory("question", displayMarker);
        });
        if (pair == null) return;
        // Линковка буферизованных картинок к слитому сообщению (иначе после
        // снятия черновика они остались бы без ссылок → CollectGarbage).
        var pauseMid = "pause-" + Guid.NewGuid().ToString("N")[..12];
        var pauseMeetingId = ChatImages?.CurrentMeetingId ?? SafeEnsureMeeting();
        if (pauseMeetingId != null && imageIds?.Count > 0 && ChatImages != null)
        {
            try
            {
                for (var i = 0; i < imageIds.Count; i++)
                    ChatImages.Store.Link(imageIds[i], pauseMeetingId, pauseMid, i);
            }
            catch (Exception ex) { Logger.Warn($"chat-image: pause link {pauseMid}: {ex.Message}", "ChatImg"); }
        }
        // LLM получает полный текст с инструкцией; метка для чата уже показана.
        await RunManualAskAsync(pair, pausedText, isScreen: false, isManual: false,
            messageId: tiles?.Count > 0 ? pauseMid : null,
            imageTiles: tiles);
    }

    /// <summary>Текущий meetingId журнала; журнала нет → null (картинки остаются
    /// несвязанными до следующей отправки — безопасно).</summary>
    private string? SafeEnsureMeeting()
    {
        try { return MeetingJournal?.EnsureCurrentMeeting(); }
        catch { return null; }
    }

    /// <summary>Кэпабилити-сервис картинок (ТЗ §8): каталоги/пробник через свой
    /// HttpClient (дефолтный дедлайн 100с — это не стриминговый путь), кэш —
    /// тот же AttachmentStore (attachments.db). Без стора фича выключена.</summary>
    private Core.Images.IImageCapabilityService? TryCreateImageCapabilityService()
    {
        if (_attachmentStore == null) return null;
        try
        {
            return new Core.Images.ImageCapabilityService(
                new System.Net.Http.HttpClient(), _attachmentStore, () => Config.Api, Logger);
        }
        catch (Exception ex)
        {
            Logger.Warn($"chat-image: capability service недоступен: {ex.Message}", "ChatImg");
            return null;
        }
    }

    /// <summary>
    /// Ключ активной встречи для привязки usage (§8.1/R4 аудита 2026-10-16). Снимается
    /// на СТАРТЕ ask'а/задания и передаётся в <see cref="AccountUsage"/>. Если встречу
    /// сменили, пока spend был в полёте (или фоновое задание доехало после сброса),
    /// TokenLedger уведёт его в фон, и «Потрачено за встречу» не раздувается чужими
    /// расходами. null — память выключена или встреча ещё не создана: учёт без привязки
    /// (прежнее поведение).
    /// </summary>
    private string? MeetingSpendKey => CurrentMeetingId?.ToString();

    /// <summary>
    /// Единая точка учёта usage LLM (§8.1 аудита 15.10). Все пути — ручной вопрос,
    /// речь, MCP, мульти-агенты, сжатие контекста, извлечение фактов/памяти —
    /// обязаны проходить через неё, иначе счётчик врёт («ответы есть, токены 0»).
    /// <para>
    /// <paramref name="background"/> = true для фонового расхода (документы/память/
    /// сжатие) — он идёт в отдельный счётчик «Документы и память», а не в «Потрачено
    /// за встречу». <paramref name="meetingKey"/> привязывает spend к встрече, на
    /// старте которой ask/задание поставлено. Несовпадение с активной встречью
    /// (смена чата, пока ответ был в полёте) → расход уходит в фон: поздний spend
    /// старой встречи не надувает «Потрачено за встречу» новой.
    /// </para>
    /// <para>
    /// Нули при UsageReported=false — это «провайдер не сообщил», а не «потрачено 0»:
    /// взводим NA-флаг, чтобы UI показал «расход недоступен» вместо ложного нуля.
    /// </para>
    /// </summary>
    private void AccountUsage(AskResult? res, bool background = false, string? meetingKey = null)
    {
        var spent = _tokenLedger.Add(res, background, meetingKey);
        if (!spent && !_tokenLedger.UsageUnknown) return;
        if (meetingKey != null && spent)
            Logger.Info($"AccountUsage: +{res!.PromptTokens}+{res.CompletionTokens}t, " +
                $"background={background}, meeting={meetingKey}", "Tokens");
        else if (!spent)
            Logger.Info($"AccountUsage: usage не сообщён (NA), background={background}", "Tokens");
        _ = Dispatcher.InvokeAsync(UpdateTokenDisplay);
    }

    /// <summary>
    /// Учёт usage фоновой операции ИЗ ОКНА (AI-резюме встречи в истории): тот же
    /// <see cref="TokenLedger"/>, что у чата, но публичный вход — окно не дублирует
    /// логику счётчика и NA-флага. Резюме сохранённой встречи не является ответом
    /// пользователю в текущем чате, поэтому идёт в счётчик «Документы и память».
    /// Потокобезопасен: вызывается из колбэков сервиса на пуле потоков.
    /// </summary>
    public void AccountBackgroundUsage(AskResult? res) => AccountUsage(res, background: true);

    private void UpdateTokenDisplay()
    {
        if (_tokenLedger.UsageNotReported)
            _overlay?.SetTokensDetailed(true, 0, 0, 0, 0);
        else
            _overlay?.SetTokensDetailed(false, _tokenLedger.MeetingTokens,
                _tokenLedger.MeetingPromptTokens, _tokenLedger.MeetingCompletionTokens,
                _tokenLedger.BackgroundTokens);
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
        var meetingsItem = trayItem("L_Tray_Meetings", "История встреч", (s, e) => { Logger.Debug("Tray: Meeting history", "UI"); Dispatcher.Invoke(() => ShowMeetingHistory()); });
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

        // Тест-инъекция речи: env BUDDY_SPEECH_INJECT_FILE — тейлим файл, строки
        // "<src>\t<текст>" заворачиваем в сентинел "BTX:src:text" и кладём в ту же
        // chunk-очередь: воркер минует только STT-вызов, весь остальной путь живой.
        StartSpeechInjectionWatcher(chunkChannel.Writer, ct);

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
                    // Тест-инъекция через chunk-канал (env BUDDY_SPEECH_INJECT_FILE):
                    // WAV "BTX:<src>:<текст>" минует только захват+STT — пузырь, журнал,
                    // pause-буфер и очередь ответов идут по-настоящему.
                    var injected = wav.Length > 4 && wav[0] == 'B' && wav[1] == 'T' && wav[2] == 'X' && wav[3] == ':';
                    if (injected)
                    {
                        var payload = Encoding.UTF8.GetString(wav.AsSpan(4));
                        var colon = payload.IndexOf(':');
                        if (colon > 0)
                        {
                            var injSrc = payload[..colon];
                            if (injSrc is "mic" or "loopback") source = injSrc;
                            text = payload[(colon + 1)..];
                        }
                    }
                    if (!injected)
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
                        // «LLM выкл» — чистый транскрибатор, но реплики НЕ теряем:
                        // копим в общий pause-буфер (как при IsLlmPaused) — при
                        // включении уходят ОДНИМ контекстным запросом, иначе LLM
                        // «слепнет»: не видит сказанное за время выключения.
                        _pauseBuf.Add(label, markedText);
                        Logger.Info($"LLM disabled — chunk #{totalProcessed} показан как чистый транскрипт + в буфер", "Worker");
                        continue;
                    }

                    if (IsLlmPaused)
                    {
                        // Спека п.15: во время паузы LLM распознанный текст копится
                        // в _pauseBuf (на снятии уйдёт одним контекстным запросом),
                        // а пузырь остаётся транскриптом с меткой паузы.
                        _pauseBuf.Add(label, markedText);
                        _overlay?.MarkAnswered(pair, TT("L_Mark_LlmPaused", "⏸ LLM на паузе"), DateTime.Now, 0);
                        Logger.Info($"LLM paused — chunk #{totalProcessed} показан без ответа, добавлен в pause-буфер", "Worker");
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
                // R4: spend ответа на эту реплику принадлежит встрече, активной на
                // старте обработки (к ответу чат мог смениться — поздний spend уйдёт
                // в фон, а не в «Потрачено за встречу» новой). Один ключ на итерацию:
                // обе ветки ниже (мульти-агентная и одиночный ask) используют его.
                var meetingKey = MeetingSpendKey;
                // Pending-запрос (✕-кнопка пузыря): linked-CT снимает и отмену
                // по кнопке, и общий токен воркера — EndRequest в finally.
                var req = _overlay?.BeginRequest(pair, isManual: false);
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, req?.Cts.Token ?? CancellationToken.None);
                var act = linkedCts.Token;
                try
                {
                    // «LLM выкл» щёлкнули, пока задача уже стояла в очереди: помечаем
                    // пузырь нейтральным статусом «LLM выключен» — видно, что реплика
                    // дошла, но ответа не будет (раньше закрывали пустой строкой —
                    // пузырь выглядел «зависшим» при вкл→выкл→вкл).
                    // «LLM выкл»/пауза: реплику НЕ теряем — кладём в общий буфер
                    // (_pauseBuf): при включении уходит ОДНИМ контекстным запросом
                    // (спека «дослать пропущенное одним сообщением»). Пузырь
                    // помечаем статусом — видно, что текст дошёл и буферизован.
                    if (Config.Ui.LlmDisabled) { _pauseBuf.Add(label: "", text: labeledText); _overlay?.MarkAnswered(pair, T("L_LlmDisabledMark"), DateTime.Now, 0); continue; }
                    if (IsLlmPaused) { _pauseBuf.Add(label: "", text: labeledText); _overlay?.MarkAnswered(pair, TT("L_Mark_LlmPaused", "⏸ LLM на паузе"), DateTime.Now, 0); continue; }
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

                        // R4: ключ встречи снят на старте обработки реплики (см. loop body).
                        var responses = await Orchestrator.ProcessAsync(labeledText, Config.Api.ChatModel, act);
                        var maEnd = DateTime.Now;
                        var maMs = (maEnd - askStart).TotalMilliseconds;

                        foreach (var r in responses)
                            SettingsVm.AddLlmLog(new LlmLogEntry
                            {
                                Timestamp = askStart, ScenarioName = Orchestrator.ActiveScenario?.Name ?? "", AgentName = r.AgentName,
                                Color = r.Color, UserText = labeledText, Response = r.Text, LatencyMs = r.LatencyMs, IsSilent = r.IsSilent, IsError = r.Error != null
                            });
                        // §8.1: единая точка учёта на все ответы агентов (Interlocked.Add
                        // обходил AccountUsage и не поднимал NA-флаг).
                        foreach (var r in responses)
                            AccountUsage(new AskResult(r.Text, r.PromptTokens, r.CompletionTokens,
                                r.PromptTokens + r.CompletionTokens), meetingKey: meetingKey);

                        // Memory-директивы в ответах агентов: применить + вырезать до показа
                        // (мульти-агентный ответ тоже попадает в пузырь/караоке/журнал).
                        var visible = new List<AgentResponse>();
                        foreach (var r in responses.Where(r => !r.IsSilent))
                        {
                            var rc = (await ProcessMemoryDirectivesAsync(r.Text, act)).Clean;
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
                        await Dispatcher.InvokeAsync(() => { _overlay?.SetTimelineState(TimelineState.Received); UpdateTokenDisplay(); });
                        continue;
                    }

                    // ── Одиночный LLM ──
                    // Контекст встречи: план (сводка+дословный хвост) заменяет голый
                    // legacy-history snapshot — ответ опирается на встречу, а не на FIFO.
                    var ctx = _meetingContext;
                    var plan = ctx?.BuildPlan(labeledText, Config.Advanced.SystemPrompt,
                        Config.Advanced.MaxResponseTokens, null, null,
                        Config.Api.ChatModel, Config.Api.BaseUrl);
                    // Memory recall + правила директив памяти — общий билдер для всех LLM-путей.
                    var (sysPrompt, meetingHits) = await BuildSystemPromptWithMemoryAsync(Config.Advanced.SystemPrompt, job.Text, act);
                    sysPrompt = ApplyContextToPrompt(sysPrompt, plan?.SummaryBlock,
                        ctx?.State.EarlyContextDropped ?? false, AskPurpose.BackgroundSpeech);
                    var ctxSt = ctx?.State;
                    _overlay?.SetContextInfo(ctxSt?.SummaryInFlight == true
                        ? TT("L_ContextSummarizing", "Обновляю контекст…")
                        : plan != null ? string.Format(TT("L_ContextInfoFmt", "Контекст: ≈{0}"), plan.EstimatedInputTokens.ToString("N0")) : null,
                        ctxSt?.SummaryInFlight ?? false);
                    var stream = Config.Advanced.StreamLlmAnswers;
                    var gotDelta = false;
                    var streamId = stream ? (_karaoke?.BeginStreamLine("answer") ?? 0) : 0;
                    // Маршрут вложений общий с ручным ask: Clarify/ошибка чтения
                    // документа — ImmediateAnswer без финального LLM-вызова,
                    // map-reduce уже учёл свои вызовы (background).
                    var attachmentRoute = await BuildAttachmentRouteSafeAsync(job.Text, plan, act);
                    var attachments = attachmentRoute?.Payload;
                    var immediateAttachmentAnswer = attachmentRoute?.ImmediateAnswer;
                    var askResult = immediateAttachmentAnswer != null
                        ? new AskResult(immediateAttachmentAnswer, 0, 0, 0)
                        : await ApiClient.AskAsync(labeledText, sysPrompt, Config.Advanced.MaxResponseTokens, plan != null ? new List<ChatMessage>(plan.Tail) : SnapshotLegacyHistory(), act,
                            stream ? (Action<string>)(d =>
                            {
                                gotDelta = true;
                                _overlay?.AppendAnswerDelta(pair, d);
                                _karaoke?.AppendToStreamLine(streamId, d);
                            }) : null,
                            attachmentsContext: attachments);
                    // Memory-директивы: применить (тост) и вырезать до финала стрим-строки —
                    // сырой текст с [[MEMORY_*]] уже мог отстримиться в ленту.
                    var mem = await ProcessMemoryDirectivesAsync(askResult?.Content, act);
                    var answer = mem.Clean;
                    // Фикс «записать в память → промолчала»: голосовая/текстовая команда
                    // с ответом только из [[MEMORY_*]] — Clean пуст, но Applied>0 = успех.
                    if (string.IsNullOrWhiteSpace(answer) && mem.Applied.Count > 0)
                        answer = TT("L_Answer_MemorySaved", "✔ записано в память");
                    var memStripped = !string.Equals(answer, askResult?.Content, StringComparison.Ordinal);
                    FinalizeKaraokeStreamLine(stream, gotDelta, askResult?.IsSilent == true, memStripped, answer, streamId);
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
                        // §8.1: единая точка учёта (прямой += давал расхождение цифр
                        // с ручным путём и не поднимал NA-флаг для провайдеров без usage).
                        AccountUsage(askResult, meetingKey: meetingKey);
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
                        await Dispatcher.InvokeAsync(() => { pair.PastMeetingHits = meetingHits; _overlay?.SetTimelineState(TimelineState.Received); UpdateTokenDisplay(); }); // §7: ссылки-источники + зелёный статус
                        // Контекст встречи параллельно легаси-истории: пара в хвост,
                        // состояние в sidecar, фоновая сводка по расписанию плана.
                        ctx?.AddPair(labeledText, answer!);
                        SaveMeetingContextState();
                        if (plan?.ScheduleSummary == true) RunContextSummaryAsync();
                    }
                    else if (askResult?.IsSilent == true || memStripped)
                    {
                        // Осознанное молчание модели (finish_reason=stop при пустом
                        // content): промпт суфлёра сам требует тишины на small talk.
                        // Нейтральный статус как у мульти-агентов, НЕ красная плашка.
                        Logger.Info($"LLM silent (stop, empty content) in {askMs:F0}ms", "Answer");
                        // Молчание тоже стоит токенов (reasoning-модели жгут бюджет
                        // на размышления) — учитываем, иначе счётчик врёт.
                        AccountUsage(askResult, meetingKey: meetingKey);
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
                    else if (askResult?.Canceled == true || (req != null && req.Cts.IsCancellationRequested))
                    {
                        // Запрос отменён (✕ на пузыре или токен воркера) — НЕ ошибка:
                        // нейтральная метка «Остановлено», без красной плашки.
                        Logger.Info($"Ask cancelled (token) in {askMs:F0}ms", "Answer");
                        _overlay?.MarkCancelled(pair);
                    }
                    else
                    {
                        // 200, но контент пуст и это НЕ осознанное молчание: конкретная
                        // причина по EmptyKind (лимит съеден размышлениями / кап
                        // провайдера / повреждённое тело). Совет «увеличьте лимит»
                        // только при доказанном LengthExhausted — инцидент 2026-10-12.
                        Logger.Warn($"Empty answer from LLM (200, EmptyKind={askResult?.EmptyKind}, finish={askResult?.FinishReason})", "Answer");
                        var why = askResult != null ? EmptyAnswerHint(askResult, memStripped) : T("L_ModelNoAnswer");
                        QaLogger?.Log(labeledText, $"[пустой ответ: {askResult?.EmptyKind}]", "EMPTY", job.SttMs, askMs, job.AudioSeconds, Config.Api.ChatModel);
                        _overlay?.MarkFailed(pair, why, askEnd, askMs);
                    }
                }
                catch (OperationCanceledException) when (req?.Cts.IsCancellationRequested == true && !ct.IsCancellationRequested)
                {
                    // Отмена только этой заявки (✕) — воркер живёт, помечаем пузырь.
                    Logger.Info("Ask cancelled (request token)", "Answer");
                    _overlay?.MarkCancelled(pair);
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
                finally
                {
                    if (req != null) _overlay?.EndRequest(req);
                    linkedCts.Dispose();
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

    /// <summary>
    /// Старт входящего HTTP-ingress по Mcp.HttpEnabled (+ funnel при
    /// TunnelMode=tailscale-funnel). Ошибки bind/funnel → статус «Нет связи»
    /// через ReportTransportState + лог, приложение не падает.
    /// </summary>
    private async Task TryStartMcpHttpAsync()
    {
        if (!Config.Mcp.Enabled || !Config.Mcp.HttpEnabled) return;
        try
        {
            // Тот же фасад, что у pipe-хоста (единые карточки/активность);
            // если pipe выключен — свой экземпляр (Dispose на выходе).
            Core.Mcp.IMcpFacade facade;
            if (McpHost != null)
            {
                facade = McpHost.Facade;
            }
            else
            {
                var jobs = new Core.Mcp.McpJobStore(AppDataDir);
                _httpFacade = new Core.Mcp.LocalMcpFacade(
                    MeetingJournal!, MemoryStore, _attachmentStore, jobs, Config.Mcp, Logger, McpPermissions,
                    journal: McpJournal, memoryProvider: () => MemoryStore);
                facade = _httpFacade;
            }
            var auth = new Core.Mcp.Http.McpHttpAuthStore(
                AppDataDir, McpPermissions!, () => Config.Mcp.Connections);
            AttachMcpFacadeActivity(facade);
            McpHttp = await Core.Mcp.Http.McpHttpIngress.TryStartAsync(
                facade, Config.Mcp, auth, () => Config.Mcp.Connections, Logger);
            // Согласие OAuth-клиентов (first-contact): тост владельцу + Warn-лог;
            // карточку approve/deny рисует вкладка MCP (ListPendingClients/ResolvePendingClient).
            facade.PendingClientAdded += OnMcpPendingClient;
            if (McpHttp != null)
                Logger.Info($"MCP HTTP ingress on {McpHttp.LocalPrefix}", "Mcp");
            if (McpHttp != null && Config.Mcp.TunnelMode is "tailscale-funnel" or "tailscale")
                await TryStartMcpFunnelAsync(facade);
            else if (McpHttp != null && Config.Mcp.TunnelMode == "manual")
                facade.ReportTransportState("funnel", true);
        }
        catch (Exception ex)
        {
            try { McpHost?.Facade.ReportTransportState("http", false, ex.Message); } catch { }
            Logger.Warn($"MCP HTTP start failed: {ex.Message}", "Mcp");
        }
    }

    /// <summary>
    /// Новый OAuth-клиент ждёт разрешения владельца: тост + Warn. Само решение —
    /// кнопки на карточке pending-клиента во вкладке MCP (ListPendingClients /
    /// ResolvePendingClient на фасаде).
    /// </summary>
    private void OnMcpPendingClient(Core.Mcp.McpPendingClient p)
    {
        Logger.Warn(
            $"MCP OAuth: клиент '{p.ClientName ?? p.ClientId}' ждёт разрешения ({p.RedirectUri})", "Mcp");
        var name = p.ClientName ?? p.ClientId;
        try
        {
            Dispatcher.InvokeAsync(() =>
                Notifier?.ShowWarning(
                    TT("L_Mcp_PendingClient_Title", "MCP: запрос доступа"),
                    string.Format(
                        TT("L_Mcp_PendingClient_BodyFmt",
                            "Клиент «{0}» запрашивает доступ — Настройки → MCP."), name)));
        }
        catch { /* диспетчер может быть недоступен на раннем старте */ }
    }

    /// <summary>
    /// Подписаться на «реальный запрос» фасада: карточка подключения в
    /// настройках обновляет статус без переоткрытия окна. Повторная подписка
    /// на тот же фасад не дублируется. Здесь же подключается подтверждение
    /// опасных операций (удаление факта) — карточка в оверлее.
    /// </summary>
    private void AttachMcpFacadeActivity(Core.Mcp.IMcpFacade? facade)
    {
        if (facade == null) return;
        foreach (var known in _mcpActivityFacades)
            if (ReferenceEquals(known, facade)) return;
        try
        {
            facade.RequestObserved += OnMcpRequestObserved;
            facade.SetApprovalHandler(RequestMcpFactApprovalAsync);
            _mcpActivityFacades.Add(facade);
        }
        catch (Exception ex)
        {
            Logger?.Warn($"MCP activity subscribe failed: {ex.Message}", "Mcp");
        }
    }

    /// <summary>
    /// Подтверждение удаления факта по запросу MCP-клиента: карточка
    /// «Разрешить/Отклонить» ВНУТРИ оверлея (не модалка). Оверлея нет или он
    /// скрыт — подтвердить нечем, значит отказ (удаление не выполняется).
    /// </summary>
    private Task<bool> RequestMcpFactApprovalAsync(Core.Mcp.McpApprovalRequest request)
    {
        var overlay = _overlay;
        if (overlay == null || !overlay.IsVisible)
            return Task.FromResult(false);
        try
        {
            return overlay.RequestMcpApproval(request.Summary, request.Details);
        }
        catch (Exception ex)
        {
            Logger?.Warn($"MCP fact approval failed: {ex.Message}", "Mcp");
            return Task.FromResult(false);
        }
    }

    /// <summary>Отписаться от фасада (хост/ingress остановлен).</summary>
    private void DetachMcpFacadeActivity(Core.Mcp.IMcpFacade? facade)
    {
        if (facade == null) return;
        try { facade.RequestObserved -= OnMcpRequestObserved; } catch { }
        _mcpActivityFacades.RemoveAll(f => ReferenceEquals(f, facade));
    }

    /// <summary>Реальный запрос клиента → сигнал UI (карточки подключений).</summary>
    private void OnMcpRequestObserved(string connectionId, DateTime utc)
    {
        Logger?.Debug($"MCP request observed: {connectionId}", "Mcp");
        try { McpRequestObserved?.Invoke(connectionId, utc); }
        catch (Exception ex) { Logger?.Warn($"MCP request observer failed: {ex.Message}", "Mcp"); }
    }

    /// <summary>Tailscale Funnel на локальный HttpPort; отсутствие/сбой → «Нет связи»+причина.</summary>
    private async Task TryStartMcpFunnelAsync(Core.Mcp.IMcpFacade facade)
    {
        try
        {
            var info = await Core.Mcp.Http.TailscaleFunnel.DetectAsync(Config.Mcp.HttpPort);
            switch (info.State)
            {
                case Core.Mcp.Http.TailscaleFunnel.State.NotInstalled:
                case Core.Mcp.Http.TailscaleFunnel.State.DaemonUnavailable:
                    facade.ReportTransportState("funnel", false,
                        info.Detail ?? "Tailscale недоступен");
                    return;
                case Core.Mcp.Http.TailscaleFunnel.State.Running:
                    Config.Mcp.PublicBaseUrl = info.PublicUrl ?? "";
                    facade.ReportTransportState("funnel", true);
                    return;
            }
            var (url, error) = await Core.Mcp.Http.TailscaleFunnel.StartFunnelAsync(Config.Mcp.HttpPort);
            if (url == null)
            {
                facade.ReportTransportState("funnel", false, error ?? "funnel не поднялся");
                Logger.Warn($"Tailscale funnel failed: {error}", "Mcp");
                return;
            }
            _funnelPort = Config.Mcp.HttpPort;
            Config.Mcp.PublicBaseUrl = url;
            SaveConfigSafe();
            facade.ReportTransportState("funnel", true);
            Logger.Info($"Tailscale funnel: {url} → 127.0.0.1:{_funnelPort}", "Mcp");
        }
        catch (Exception ex)
        {
            facade.ReportTransportState("funnel", false, ex.Message);
            Logger.Warn($"Tailscale funnel error: {ex.Message}", "Mcp");
        }
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
            // MCP: исходящие клиенты и входящий pipe-хост — до финального лога.
            // StopAsync ждём коротко: pipe должен отпуститься до смерти процесса,
            // иначе следующий старт может встретить зависший listener.
            try { if (McpHost != null) McpHost.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            // HTTP-ingress и его фасад (только если создавали свой — pipe-хост гасит свой сам).
            try { if (McpHttp != null) McpHttp.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { if (McpHttp != null) McpHttp.Facade.PendingClientAdded -= OnMcpPendingClient; } catch { }
            try { if (_funnelPort != 0) Core.Mcp.Http.TailscaleFunnel.StopFunnelAsync(_funnelPort).Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _httpFacade?.Dispose(); } catch { }
            // Оркестратор делит McpClientManager с McpOutgoing — гасим первым,
            // иначе его DisposeAsync закроет общих клиентов дважды.
            try { if (McpChat != null) McpChat.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { if (McpOutgoing != null) McpOutgoing.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }

            // Вложения: воркер фактов -> очередь импорта -> нативный OCR -> стор.
            // Interrupt-метки running-джоб снимает MarkInterruptedJobs на следующем старте.
            try { _factWorker?.Dispose(); } catch { }
            try { _importRunner?.Dispose(); } catch { }
            try { _pdfOcr?.Dispose(); } catch { }
            try { _attachmentStore?.Dispose(); } catch { }

            // §8: поиск встреч держит meetings-index.db и фоновый до-эмбеддинг E5 —
            // гасим его ДО teardown памяти и dispose логгера, иначе job пишет в закрытую БД/лог.
            try { MeetingSearch?.Dispose(); } catch { }
            MeetingSearch = null;

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
