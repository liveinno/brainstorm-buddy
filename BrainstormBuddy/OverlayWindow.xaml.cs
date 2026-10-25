using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrainstormBuddy.Config;
using BrainstormBuddy.Native;
using BrainstormBuddy.Transcription;
using Key = System.Windows.Input.Key;

namespace BrainstormBuddy;

public enum TimelineState { Idle, Recording, Sending, Received, ChunkSent }

/// <summary>Вид стекового тоста оверлея: определяет акцентный цвет и время автозакрытия.</summary>
public enum ToastKind { Info, Progress, Success, Error }

public partial class OverlayWindow : Window
{
    private bool _clickThroughApplied;
    private bool _screenCaptureVisible;
    private UiConfig _uiConfig = new();
    private bool _initialized;
    private DispatcherTimer _blinkTimer;
    private bool _blinkOn;
    private bool _llmBlink;

    // Геометрия окна (позиция/размер): дебаунс-сохранение после ресайза и
    // восстановление сохранённого прямоугольника в ApplyUiConfig/Initialize.
    private DispatcherTimer? _geometrySaveTimer;
    private bool _geometryReady;
    private bool _restoringGeometry;
    private Rect? _restoredGeometry;
    private double _lastNormalWidth;
    private double _lastNormalHeight;

    // Активные LLM-запросы по id — замена единственного _sendingItem:
    // параллельные ask'и больше не сражаются за одну ссылку на пузырь.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, PendingRequest> _pendingRequests = new();
    private int _lastRequestId;

    /// <summary>Зарегистрированный запрос к LLM: конкретный пузырь + отмена + признак ручного ввода.</summary>
    public sealed class PendingRequest
    {
        public PendingRequest(int requestId, QaPair pair, CancellationTokenSource cts, bool isManual, string question)
        {
            RequestId = requestId;
            Pair = pair;
            Cts = cts;
            IsManual = isManual;
            Question = question;
        }
        public int RequestId { get; }
        public QaPair Pair { get; }
        public CancellationTokenSource Cts { get; }
        public bool IsManual { get; }
        public string Question { get; }
    }

    /// <summary>Активные запросы (статус «Думаю над…» деривируется отсюда у lead).</summary>
    public IReadOnlyCollection<PendingRequest> PendingRequests =>
        (IReadOnlyCollection<PendingRequest>)_pendingRequests.Values;

    /// <summary>Привязать пузырь к новому запросу: RequestId (Interlocked), CTS отмены,
    /// IsManual + исходный вопрос для «Повторить». Потокобезопасно.</summary>
    public PendingRequest BeginRequest(QaPair pair, bool isManual)
    {
        var id = System.Threading.Interlocked.Increment(ref _lastRequestId);
        var req = new PendingRequest(id, pair, new CancellationTokenSource(), isManual, pair.Question);
        pair.RequestId = id;
        pair.IsManual = isManual;
        pair.RequestQuestion = pair.Question;
        _pendingRequests[id] = req;
        return req;
    }

    /// <summary>Снять запрос из активных (идемпотентно: false, если уже снят).</summary>
    public bool EndRequest(PendingRequest req) => _pendingRequests.TryRemove(req.RequestId, out _);

    // Timeline (2 min, 500ms per sample = 240 samples).
    // Рендер — эквалайзер как в design/main frame: бар 3px + промежуток 1px,
    // скруглённая верхушка, приглушённые цвета.
    private const int TimelineCols = 240;
    private const int TimelineBarPx = 3;
    private const int TimelineGapPx = 1;
    private const int TimelineBmpWidth = TimelineCols * (TimelineBarPx + TimelineGapPx);
    private const int TimelineHeight = 38;
    private readonly float[] _timelineRms = new float[TimelineCols];
    private readonly TimelineState[] _timelineStates = new TimelineState[TimelineCols];
    private int _timelineHead;
    private int _timelineCount;
    private TimelineState _currentTimelineState = TimelineState.Idle;
    private DispatcherTimer _timelineTimer;
    private WriteableBitmap? _timelineBitmap;
    private readonly byte[] _timelinePixels = new byte[TimelineBmpWidth * TimelineHeight * 4];
    private int _lastClockSecond = -1;
    private bool _autoScroll = true;

    // Cached frozen brushes (no allocations per tick)
    // Цвета чипов REC/LLM строго по макету Overlay Final 6a (idle/active фазы блинка).
    private static readonly SolidColorBrush _brushGray = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x5A, 0x64, 0x78)));     // idle-точка #5a6478
    private static readonly SolidColorBrush _brushRed = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x6A)));      // REC dot #ff5a6a
    private static readonly SolidColorBrush _brushDarkRed = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x8F, 0x33, 0x3C)));  // REC dot (off-фаза блинка)
    private static readonly SolidColorBrush _brushBlue = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x6F, 0x8C, 0xFF)));     // LLM dot #6f8cff
    private static readonly SolidColorBrush _brushDarkBlue = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x3D, 0x4A, 0x99))); // LLM dot (off-фаза блинка)
    private static readonly SolidColorBrush _brushRulerTick = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x6D, 0x7F, 0x9E)));  // TextFaint — делители линейки
    private static readonly SolidColorBrush _brushRulerLabel = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x9F, 0xB0, 0xCC))); // TextMuted — подписи времени
    private static readonly System.Windows.Media.FontFamily _rulerFont = new("Cascadia Mono, Consolas");

    private static SolidColorBrush FreezeBrush(SolidColorBrush b) { b.Freeze(); return b; }

    public ObservableCollection<QaPair> History { get; } = new();
    /// <summary>Локализатор строк (App.T) — установлен в конструкторе.</summary>
    private Func<string, string> _localizer = key => key;

    public OverlayWindow()
    {
        InitializeComponent();

        _geometrySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _geometrySaveTimer.Tick += OnGeometrySaveTick;
        SizeChanged += OnGeometrySizeChanged;

        // Дизайнерские pressed-иконки BuddyV2: зажатая кнопка показывает Icon*Pressed-арт.
        PressedIconSwap.Wire(AutoScrollButton, "IconAutoScroll");
        PressedIconSwap.Wire(SettingsButton, "IconSettings");
        PressedIconSwap.Wire(MinimizeButton, "IconMinimize");
        PressedIconSwap.Wire(MaximizeButton, "IconMaximize");
        PressedIconSwap.Wire(HideButton, "IconClose");
        PressedIconSwap.Wire(ExportTransBtn, "IconExport");
        PressedIconSwap.Wire(ExportSummaryBtn, "IconAiProtocol");
        PressedIconSwap.Wire(AttachFilesButton, "BuddyAttachment.Icon.Default");
        PressedIconSwap.Wire(ScreenShotButton, "IconScreenshot");
        PressedIconSwap.Wire(CloakButton, "IconCloak");
        PressedIconSwap.Wire(EraseButton, "IconErase");
        PressedIconSwap.Wire(MicMuteButton, "IconMic");
        PressedIconSwap.Wire(LlmOffButton, "IconLlm");
        PressedIconSwap.Wire(SpeakerMuteButton, "IconSpeaker");
        PressedIconSwap.Wire(MemoryButton, "IconMemory");
        PressedIconSwap.Wire(AttachFilesButton, "BuddyAttachment.Icon.Default");
        PressedIconSwap.Wire(LiveSendBtn, "IconSendNow");
        PressedIconSwap.Wire(SlowSpeechBtn, "BuddyExtra.Icon.SpeechSlow.Default");
        PressedIconSwap.Wire(ResetSpeedBtn, "BuddyExtra.Icon.SpeechReset.Default");
        PressedIconSwap.Wire(FastSpeechBtn, "BuddyExtra.Icon.SpeechFast.Default");

        Title = StealthModeService.DisplayName; // стелс-alias в заголовке (Task Manager/Alt-Tab)
        HistoryList.ItemsSource = History;
        _localizer = key => App.Current.T(key);
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += OnBlinkTick;
        _blinkTimer.Start();

        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timelineTimer.Tick += OnTimelineTick;
        _timelineTimer.Start();

        Loaded += (s, e) =>
        {
            _timelineBitmap = new WriteableBitmap(TimelineBmpWidth, TimelineHeight, 96, 96, PixelFormats.Bgra32, null);
            TimelineImage.Source = _timelineBitmap;
            DrawTimelineRuler();
            ApplyTimelineCollapsed(App.Current.Config.Ui.TimelineCollapsed);
        };

        // Ctrl+V по изображению в черновик — перехват до текстовой вставки (ТЗ §3).
        System.Windows.DataObject.AddPastingHandler(ChatInputBox, OnChatInputPaste);
        // WPF-TextBox: Paste.CanExecute = false для image-only буфера (нет
        // AcceptsRichContent) → DataObject.Pasting не поднимается и OnChatInputPaste
        // недостижим даже реальным Ctrl+V / пунктом «Вставить» (disabled).
        // Свой CommandBinding объявляет команду выполнимой, когда в буфере
        // есть картинки — текстовая вставка идёт дефолтным путём.
        ChatInputBox.CommandBindings.Add(new System.Windows.Input.CommandBinding(
            System.Windows.Input.ApplicationCommands.Paste,
            (_, _) =>
            {
                AttachImageFlow();
                App.Current.ChatImages?.RequestAddDraftFromClipboard();
            },
            (s, e) =>
            {
                if (ChatImageCodec.GetClipboardImages().Count > 0)
                {
                    e.CanExecute = true;
                    e.Handled = true; // картинка — наша команда, дефолт не зовём
                }
            }));
        Loaded += (_, _) => AttachImageFlow();
    }


    // Фиолетовая отсечка «чанк ушёл в STT» (инженерная фича): MainLoop дёргает PulseChunkMarker
    // с фонового потока, ближайший тик таймлайна рисует один столбик на всю высоту. Удобно на

    // ===== Память: чип-индикатор + алиас спикера (спека memory.txt) =====

    /// <summary>
    /// Обновить чип памяти в нижней панели: цвет = состояние (0 off / 1 read /
    /// 2 write / 3 read+write), текст-подсказка локализована.
    /// </summary>
    public void RefreshMemoryButton()
    {
        if (MemoryButton == null) return;
        var state = App.Current.GetMemoryIndicatorState();
        var ringKey = state switch
        {
            3 => "SuccessBrush",
            1 or 2 => "WarnBrush",
            _ => (string?)null,
        };
        var tipKey = state switch
        {
            3 => "L_MemToggle_On",
            1 => "L_MemToggle_ReadOnly",
            2 => "L_MemToggle_WriteOnly",
            _ => "L_MemToggle_Off",
        };
        try
        {
            // Кольцо на рамке чипа: off — без кольца, read/write-only — золото, on — зелёное.
            SetStateRing(MemoryButton, ringKey);
        }
        catch (Exception ex) { App.Current.Logger.Error("RefreshMemoryButton failed", ex, "UI"); }
        MemoryButton.ToolTip = _localizer(tipKey);
    }

    /// <summary>Клик по чипу памяти — быстрый тогл UseMemory для текущей встречи.</summary>
    private void OnMemoryClick(object sender, RoutedEventArgs e) => App.Current.ToggleMeetingMemory();

    /// <summary>
    /// Контекстное меню чипа спикера → «Назначить имя…»: простой ввод → алиас
    /// per-meeting (App.SetSpeakerAlias). DataContext меню = SpeakerChipVm (Tag на чипе).
    /// </summary>
    private void OnAssignSpeakerNameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not SpeakerChipVm chip)
            return;
        var speakerId = chip.Id;
        var prompt = string.Format(_localizer("L_MemAssignNamePrompt"), speakerId);
        var name = PromptTextInput(prompt, chip.Alias ?? speakerId);
        if (string.IsNullOrWhiteSpace(name)) return;
        App.Current.SetSpeakerAlias(speakerId, name);
        // App.SetSpeakerAlias обновляет только пузыри, где спикер доминантный
        // (p.SpeakerId == speakerId); в мульти-спикерных пузырях он может быть
        // вторичным — поэтому чипы всех пар обновляем здесь напрямую.
        foreach (var p in History)
            p.ApplySpeakerAlias(speakerId, name);
    }

    /// <summary>Минимальный ввод строки: тема приложения + TextBox + OK/Отмена (Enter/Esc).</summary>
    private string? PromptTextInput(string prompt, string initial)
    {
        var dlg = new Window
        {
            Title = "Brainstorm Buddy",
            Width = 340, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = TryFindResource("PanelBrush") as System.Windows.Media.Brush,
            Foreground = TryFindResource("TextBrush") as System.Windows.Media.Brush,
            FontFamily = TryFindResource("UiFontFamily") as System.Windows.Media.FontFamily,
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            FontSize = 14, LineHeight = 21 });
        var tb = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 8),
            FontSize = 14, Padding = new Thickness(12, 8, 12, 8) };
        sp.Children.Add(tb);
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var ok = new System.Windows.Controls.Button { Content = _localizer("L_Msg_Ok"), Width = 72, Margin = new Thickness(0, 0, 6, 0), IsDefault = true,
            FontSize = 14, FontWeight = FontWeights.Medium };
        var cancel = new System.Windows.Controls.Button { Content = _localizer("L_Msg_Cancel"), Width = 72, IsCancel = true,
            FontSize = 14, FontWeight = FontWeights.Medium };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        sp.Children.Add(row);
        dlg.Content = sp;
        string? result = null;
        ok.Click += (s, e2) => { result = tb.Text; dlg.DialogResult = true; };
        dlg.ShowDialog();
        return result;
    }
    // Маркер чанка на таймлайне: пульс при enqueue — во время длинной речи: видно,
    // ушло ли уже что-то в распознавание.
    private volatile bool _chunkMarkerPending;
    public void PulseChunkMarker() => _chunkMarkerPending = true;

    private int _stateHoldTicks;
    public void SetTimelineState(TimelineState state)
    {
        _currentTimelineState = state;
        // Sending/Received держим ограниченное время, потом эквалайзер возвращается в Idle
        // (иначе кадры тишины красятся зелёным «вечно» — баг «постоянно зелёный свет»).
        if (state != TimelineState.Idle) _stateHoldTicks = 6; // ~3с при тике 500мс
    }

    private void OnTimelineHeaderClick(object sender, MouseButtonEventArgs e)
    {
        var collapsed = !App.Current.Config.Ui.TimelineCollapsed;
        App.Current.Config.Ui.TimelineCollapsed = collapsed;
        ApplyTimelineCollapsed(collapsed);
        // Состояние переживает перезапуск: конфиг сохраняется при выходе (OnExit)
        App.Current.Logger.Info($"Overlay: timeline {(collapsed ? "collapsed" : "expanded")}", "UI");
    }

    private void ApplyTimelineCollapsed(bool collapsed)
    {
        TimelineBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        TimelineChevron.Text = collapsed ? "▸" : "▾";
    }

    private void OnTimelineTick(object? sender, EventArgs e)
    {
        // См. OnBlinkTick: поздний тик на закрывающемся окне не должен трогать
        // уже мёртвые визуалы/битмап.
        if (!IsLoaded || _timelineBitmap == null) return;
        try
        {
            float rms = App.Current.GetCurrentRms();
            // Живой порог детекции (авто-калиброванный или ползунок) вместо хардкода 0.010:
            // раньше ползунок RMS на шкалу не влиял вообще — «красные столбики не убрать».
            float speechThr = (float)App.Current.GetCurrentEffectiveThreshold();
            _timelineRms[_timelineHead] = rms;
            var pauseActive = App.Current.IsPaused || App.Current.IsLlmPaused;
            _timelineStates[_timelineHead] = pauseActive
                ? TimelineState.Idle
                : rms >= speechThr ? TimelineState.Recording : _currentTimelineState;
            _timelineRms[_timelineHead] = pauseActive ? 0f : rms;
            // Отсечка «чанк ушёл в STT» перекрывает состояние этого тика (один фиолетовый столбик).
            if (_chunkMarkerPending)
            {
                _chunkMarkerPending = false;
                _timelineStates[_timelineHead] = TimelineState.ChunkSent;
            }
            _timelineHead = (_timelineHead + 1) % TimelineCols;
            if (_timelineCount < TimelineCols) _timelineCount++;

            // Через ~3с после Sending/Received возвращаемся в Idle, чтобы тишина не была зелёной
            if (_currentTimelineState != TimelineState.Idle && --_stateHoldTicks <= 0)
                _currentTimelineState = TimelineState.Idle;

            // Update clock + ruler every second
            var now = DateTime.Now;
            if (now.Second != _lastClockSecond)
            {
                _lastClockSecond = now.Second;
                ClockText.Text = now.ToString("HH:mm:ss");
                UpdateRuler();
            }

            // Render equalizer bars (reuse pixel array, no allocations)
            if (_timelineBitmap == null) return;
            int stride = TimelineBmpWidth * 4;
            int entries = Math.Min(_timelineCount, TimelineCols);
            int start = (_timelineHead - entries + TimelineCols) % TimelineCols;

            // Clear entire buffer (прозрачный фон)
            Array.Clear(_timelinePixels, 0, _timelinePixels.Length);

            for (int col = 0; col < TimelineCols; col++)
            {
                int idx = (start + col) % TimelineCols;
                float rmsVal = _timelineRms[idx];
                var state = _timelineStates[idx];

                int barH;
                Color c;

                switch (state)
                {
                    case TimelineState.Sending:
                        barH = (int)(TimelineHeight * 0.80);
                        c = Color.FromRgb(0x60, 0xA5, 0xFA);        // приглушённый синий
                        break;
                    case TimelineState.Received:
                        barH = (int)(TimelineHeight * 0.80);
                        c = Color.FromRgb(0x4A, 0xDE, 0x80);
                        break;
                    case TimelineState.Recording:
                        barH = (int)(rmsVal / 0.05 * TimelineHeight);
                        if (barH > TimelineHeight) barH = TimelineHeight;
                        if (barH < 3) barH = 3;
                        c = Color.FromRgb(0xF8, 0x71, 0x71);        // розово-красный из референса
                        break;
                    case TimelineState.ChunkSent:
                        barH = TimelineHeight;                       // отсечка на всю высоту
                        c = Color.FromRgb(0xA7, 0x8B, 0xFA);        // фиолетовый: чанк ушёл в STT
                        break;
                    default: // Idle
                        if (rmsVal >= speechThr)
                        {
                            barH = (int)(rmsVal / 0.05 * TimelineHeight);
                            if (barH > TimelineHeight) barH = TimelineHeight;
                            if (barH < 3) barH = 3;
                            c = Color.FromRgb(0x9C, 0x5A, 0x5A);    // тихий приглушённый
                        }
                        else
                        {
                            barH = 2;
                            c = Color.FromRgb(0x3A, 0x46, 0x53);    // серо-синий idle
                        }
                        break;
                }

                // Бар 3px + gap 1px, скруглённая верхушка (верхний ряд — только центральный пиксель)
                int x0 = col * (TimelineBarPx + TimelineGapPx);
                int topY = TimelineHeight - barH;
                for (int y = topY; y < TimelineHeight; y++)
                {
                    bool roundedRow = y == topY && barH >= 3;
                    for (int bx = 0; bx < TimelineBarPx; bx++)
                    {
                        if (roundedRow && bx != TimelineBarPx / 2) continue;
                        int pi = (y * stride) + ((x0 + bx) * 4);
                        _timelinePixels[pi] = c.B;
                        _timelinePixels[pi + 1] = c.G;
                        _timelinePixels[pi + 2] = c.R;
                        _timelinePixels[pi + 3] = 255;
                    }
                }
            }
            _timelineBitmap.WritePixels(new Int32Rect(0, 0, TimelineBmpWidth, TimelineHeight), _timelinePixels, stride, 0);
        }
        catch (Exception ex) { App.Current.Logger.Error("OnTimelineTick failed", ex, "UI"); }
    }

    // Pre-created ruler labels (updated text each second, no UI recreation)
    private readonly TextBlock[] _rulerLabels = new TextBlock[12];
    private readonly System.Windows.Shapes.Rectangle[] _rulerTicks = new System.Windows.Shapes.Rectangle[12];
    private bool _rulerCreated;

    /// <summary>
    /// Creates ruler UI elements ONCE and repositions them on resize.
    /// After creation, only .Text is updated every second.
    /// </summary>
    private void DrawTimelineRuler()
    {
        try
        {
            if (!_rulerCreated)
            {
                for (int i = 0; i < 12; i++)
                {
                    var tick = new System.Windows.Shapes.Rectangle
                    {
                        Width = 1,
                        Height = 4,
                        Fill = _brushRulerTick,
                    };
                    Canvas.SetTop(tick, 0);
                    TimelineRuler.Children.Add(tick);
                    _rulerTicks[i] = tick;

                    var label = new TextBlock
                    {
                        Text = "", // filled by UpdateRuler
                        Foreground = _brushRulerLabel,
                        FontSize = 12,
                        FontFamily = TryFindResource("MonoFont") as System.Windows.Media.FontFamily ?? _rulerFont,
                    };
                    Canvas.SetTop(label, 2);
                    TimelineRuler.Children.Add(label);
                    _rulerLabels[i] = label;
                }
                _rulerCreated = true;
                TimelineRuler.SizeChanged += (s, e) => LayoutRuler();
            }
            LayoutRuler();
            UpdateRuler(); // initial fill
        }
        catch (Exception ex) { App.Current.Logger.Error("DrawTimelineRuler failed", ex, "UI"); }
    }

    /// <summary>
    /// Positions ruler ticks/labels across the current canvas width (called on resize).
    /// </summary>
    private void LayoutRuler()
    {
        const double totalSec = 120;
        double w = TimelineRuler.ActualWidth;
        if (w <= 0) w = 600;

        // Шаг подписей по реальной ширине текста: на узком оверлее "HH:mm:ss"
        // шире шага тиков (w/12) — без прореживания метки слипаются. Центруем
        // по измеренной ширине, а не фикс-отступом −26.
        var labelStep = 1;
        var tickGap = w / 12.0;
        var labelW = _rulerLabels[0]?.DesiredSize.Width ?? 0;
        if (labelW <= 0)
        {
            // Measure не прогнан (холодный старт) — берём худший случай MonoFont 12.
            labelW = 8 * 12 * 0.62;
        }
        while (labelStep < 12 && labelW + 8 > tickGap * labelStep)
            labelStep++;

        for (int i = 0; i < 12; i++)
        {
            if (_rulerTicks[i] == null || _rulerLabels[i] == null) continue;
            int sec = (i + 1) * 10;
            double x = w - (sec / totalSec) * w;
            bool visible = x >= 0 && x <= w;
            _rulerTicks[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            var label = _rulerLabels[i];
            var lw = label.DesiredSize.Width > 0 ? label.DesiredSize.Width : labelW;
            bool labelVisible = visible && (11 - i) % labelStep == 0; // свежий (правый) край всегда подписан
            label.Visibility = labelVisible ? Visibility.Visible : Visibility.Collapsed;
            if (labelVisible)
                Canvas.SetLeft(label, Math.Max(0, x - lw / 2));
        }
    }

    /// <summary>
    /// Called every second: updates ruler label texts to absolute HH:mm:ss.
    /// No UI elements created/destroyed — just property changes.
    /// </summary>
    private void UpdateRuler()
    {
        try
        {
            if (!_rulerCreated) return;
            var now = DateTime.Now;
            for (int i = 0; i < 12; i++)
            {
                if (_rulerLabels[i] == null) continue;
                int sec = (i + 1) * 10;
                var t = now.AddSeconds(-sec);
                _rulerLabels[i].Text = t.ToString("HH:mm:ss");
            }
        }
        catch (Exception ex) { App.Current.Logger.Error("UpdateRuler failed", ex, "UI"); }
    }

    public void SetActivePresets(ObservableCollection<NamedPrompt> presets, string activeName)
    {
        // Имя показывает NamedPrompt.ToString() — DisplayMemberPath не нужен и опасен:
        // в прод-логах ловили InvalidOperationException «Нельзя одновременно задать значения
        // DisplayMemberPath и ItemTemplate» (сеттер бросает, когда ItemTemplate уже задан).
        // Тот же приём — у ScenarioConfig/LlmProviderProfile (ToString => Name).
        PresetSelector.ItemsSource = presets;
        var found = presets.FirstOrDefault(p => p.Name == activeName);
        if (found == null) found = presets.FirstOrDefault();
        PresetSelector.SelectedItem = found;
    }

    private void OnPresetSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetSelector.SelectedItem is NamedPrompt np)
        {
            App.Current.SetActiveSystemPrompt(np);
        }
    }

    public void Initialize(UiConfig ui, Action<Window> applyClickThrough, Action<Window> applyExclude)
    {
        _uiConfig = ui;
        Opacity = 1.0;                       // окно всегда 100% — прозрачность только у фон-слоя
        BgLayer.Opacity = ui.WindowOpacity;
        RestoreSavedGeometry(ui);
        ApplyPosition(ui.WindowPosition);
        UpdateBorder();
        UpdateFontSize();
        UpdateBubblePadding();
        ApplyEngineerMode();

        SourceInitialized += OnSourceInitialized;
        Loaded += (s, e) =>
        {
            if (!_initialized)
            {
                // Тестовый хук: BUDDY_CAPTURE_VISIBLE=1 оставляет окно видимым для скриншотов.
                // В проде переменной нет → стелс-режим (exclude-from-capture) включается как обычно.
                if (Environment.GetEnvironmentVariable("BUDDY_CAPTURE_VISIBLE") != "1")
                {
                    applyExclude(this);
                    _screenCaptureVisible = false;
                }
                else
                {
                    _screenCaptureVisible = true;
                }
                _initialized = true;
            }
            ApplySilenceDisplay(App.Current.Config.Audio);
            RefreshAutoScrollButton();
        };

        // Чипы вложений: ItemsSource вручную (ItemsControl переименован — коллекция и так публичная),
        // счётчик «Ещё N» после каждого layout-прохода.
        AttachmentChipsList.ItemsSource = AttachmentChips;
        AttachmentChipsList.LayoutUpdated += (_, _) => UpdateMoreAttachmentsButton();

        // P0/§4 аудита: якорь читаемого места при выкл. автоскролле. Recapture —
        // ТОЛЬКО на пользовательскую прокрутку (OverlayScrollClassifier: VerticalChange≠0
        // + свежий вводный маркер, либо немаркированный сдвиг без изменения extent).
        // Layout/programmatic ScrollChanged якорь не трогают — стабилизация через
        // RestoreAnchorIfNeeded (LayoutUpdated возвращает якорь, допуск ≤0.5 DIP).
        HistoryScroll.ScrollChanged += (_, e) =>
        {
            if (_autoScroll)
            {
                // Контент вырос — стриминг ответа или перерисовка markdown: держим низ
                // списка. ScrollToEnd() вызывается до пересчёта высоты и попадает в
                // устаревшую высоту, поэтому подпираем его по факту изменения extent:
                // событие приходит уже после пересчёта.
                if (e.ExtentHeightChange > 0) ProgrammaticScrollToEnd();
                return;
            }
            int now = Environment.TickCount;
            bool userMarked = OverlayScrollClassifier.IsMarkFresh(_lastUserScrollTick, now);
            bool programmatic = _suppressAnchorRecapture
                || OverlayScrollClassifier.IsMarkFresh(_lastProgrammaticScrollTick, now);
            if (OverlayScrollClassifier.IsUserScroll(
                    userMarked, programmatic, e.VerticalChange, e.ExtentHeightChange, e.ViewportHeightChange))
                CaptureScrollAnchor();
        };
        // Маркеры пользовательского ввода: колесо, перетаскивание бегунка/стрелки/
        // дорожка ScrollBar (ScrollBar.ScrollEvent — routed, всплывает из шаблона),
        // клавиши прокрутки, touch-панорамирование. Только эти события дают право
        // переснять якорь — программные ScrollTo* и layout-индуцированные сдвиги нет.
        HistoryScroll.PreviewMouseWheel += (_, _) => MarkUserScroll();
        HistoryScroll.AddHandler(System.Windows.Controls.Primitives.ScrollBar.ScrollEvent, (System.Windows.Controls.Primitives.ScrollEventHandler)((_, _) => MarkUserScroll()));
        HistoryScroll.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.PageUp: case Key.PageDown:
                case Key.Up: case Key.Down:
                case Key.Home: case Key.End:
                case Key.Space:
                    MarkUserScroll();
                    break;
            }
        };
        HistoryScroll.PreviewTouchMove += (_, _) => MarkUserScroll();
        HistoryScroll.LayoutUpdated += (_, _) =>
        {
            if (_autoScroll)
            {
                // Контент изменился (стриминг ответа, перерисовка markdown) — держим низ
                // списка. Перерисовка меняет высоту пузыря уже после того, как ScrollChanged
                // увидел старый размер, поэтому сверяемся с фактическим extent на каждом
                // проходе layout: это последняя точка, где высота уже пересчитана.
                double extent = HistoryScroll.ExtentHeight;
                if (Math.Abs(extent - _lastAutoScrollExtent) > 0.5)
                {
                    _lastAutoScrollExtent = extent;
                    ProgrammaticScrollToEnd();
                }
                return;
            }
            if (!RestoreAnchorIfNeeded() && ShouldRecaptureAnchor())
                CaptureScrollAnchor();
        };
        HistoryScroll.SizeChanged += (_, _) => { if (_autoScroll) ProgrammaticScrollToEnd(); };

        _geometryReady = true;
        _lastNormalWidth = Width;
        _lastNormalHeight = Height;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const int HTCAPTION = 2; // зона заголовка → нативное перетаскивание окна
    // Коды зон для ресайза безрамочного окна (WinUser.h)
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const double ResizeBorder = 8.0;  // толщина зоны захвата у края (DIP)
    private const double TitleBarHeight = 58.0; // высота шапки-заголовка (DIP)

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        // В сквозном режиме окно полностью прозрачно для кликов.
        // Исключение — карточка подтверждения: её кнопки должны принимать клик.
        if (_clickThroughApplied && !_approvalInteractive)
        {
            handled = true;
            return (IntPtr)HTTRANSPARENT;
        }

        // Ресайз за края: WindowStyle=None не даёт этого сам — определяем зону вручную.
        // lParam: младшее слово = X, старшее = Y (экранные координаты, со знаком).
        int lp = lParam.ToInt32();
        int sx = (short)(lp & 0xFFFF);
        int sy = (short)((lp >> 16) & 0xFFFF);
        var p = PointFromScreen(new System.Windows.Point(sx, sy)); // в DIP относительно окна
        double w = ActualWidth, h = ActualHeight;
        bool left = p.X <= ResizeBorder, right = p.X >= w - ResizeBorder;
        bool top = p.Y <= ResizeBorder, bottom = p.Y >= h - ResizeBorder;

        int code = 0;
        if (top && left) code = HTTOPLEFT;
        else if (top && right) code = HTTOPRIGHT;
        else if (bottom && left) code = HTBOTTOMLEFT;
        else if (bottom && right) code = HTBOTTOMRIGHT;
        else if (left) code = HTLEFT;
        else if (right) code = HTRIGHT;
        else if (top) code = HTTOP;
        else if (bottom) code = HTBOTTOM;

        if (code != 0 && WindowState == WindowState.Normal)
        {
            handled = true;
            return (IntPtr)code;
        }

        // Перетаскивание: вся шапка — зона захвата, кроме самих контролов.
        // Возвращаем HTCAPTION → Windows двигает окно нативно (без «снайперского» прицеливания
        // по 8px-щелям). Над кнопками/селектором/полем — обычный клиентский клик.
        if (p.Y > ResizeBorder && p.Y <= TitleBarHeight &&
            p.X > ResizeBorder && p.X < w - ResizeBorder &&
            !IsOverInteractive(p))
        {
            handled = true;
            return (IntPtr)HTCAPTION;
        }
        return IntPtr.Zero;
    }

    // true, если точка (DIP относительно окна) попадает на интерактивный контрол —
    // такой участок шапки НЕ должен перехватываться под перетаскивание.
    private bool IsOverInteractive(System.Windows.Point p)
    {
        var hit = System.Windows.Media.VisualTreeHelper.HitTest(this, p);
        var el = hit?.VisualHit as DependencyObject;
        while (el != null)
        {
            if (el is System.Windows.Controls.Primitives.ButtonBase ||
                el is System.Windows.Controls.ComboBox ||
                el is System.Windows.Controls.Primitives.TextBoxBase ||
                el is System.Windows.Controls.Slider ||
                el is System.Windows.Controls.Primitives.Thumb ||
                // MarkdownView (ответ LLM с разметкой): его RichTextBox и так ловится
                // как TextBoxBase, но сам компонент тоже интерактивен (выделение/копирование).
                el is BrainstormBuddy.Controls.MarkdownView)
                return true;
            el = System.Windows.Media.VisualTreeHelper.GetParent(el);
        }
        return false;
    }

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleCloak();
            return;
        }
        bool wasCloaked = _clickThroughApplied;
        if (wasCloaked) WindowHelper.RemoveClickThrough(this);
        try { DragMove(); }
        catch { }
        finally
        {
            if (wasCloaked) WindowHelper.ApplyClickThrough(this);
        }
        if (!_uiConfig.RememberWindowGeometry)
        {
            if (Left != _uiConfig.WindowPositionX || Top != _uiConfig.WindowPositionY)
            {
                _uiConfig.WindowPositionX = Left;
                _uiConfig.WindowPositionY = Top;
                App.Current.Logger.Info($"Overlay dragged to ({Left:F0},{Top:F0})", "UI");
            }
            return;
        }
        // Перетаскивание — явный пользовательский выбор позиции: переключаем
        // пресет на Custom и сразу фиксируем валидный прямоугольник.
        _uiConfig.WindowPosition = "Custom";
        App.Current.Logger.Info($"Overlay dragged to ({Left:F0},{Top:F0})", "UI");
        SaveGeometryNow();
        App.Current.SettingsVm?.NotifyWindowPositionChanged();
    }

    private void OnMicMuteClick(object sender, RoutedEventArgs e)
    {
        App.Current.ToggleMicMute();
        RefreshMicMuteButton();
    }

    private void OnLlmPauseClick(object sender, RoutedEventArgs e)
    {
        App.Current.ToggleLlmPause();
        RefreshLlmPauseButton();
    }

    private void OnLlmOffClick(object sender, RoutedEventArgs e)
    {
        App.Current.ToggleLlmDisabled();
        RefreshLlmOffButton();
    }

    private void OnSpeakerMuteClick(object sender, RoutedEventArgs e)
    {
        App.Current.ToggleLoopbackMute();
        RefreshSpeakerMuteButton();
    }

    private void OnCloakClick(object sender, RoutedEventArgs e)
    {
        ToggleCloak();
    }

    private void OnEraseClick(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Overlay: erase (clear history) clicked", "UI");
        ClearHistory();
        // Также сбрасываем счётчики токенов и LLM-историю (новый чат)
        App.Current.ResetConversation();
    }

    public void ToggleCloak() => SetCloak(!_clickThroughApplied);

    /// <summary>
    /// Применить/снять click-through. Единственная точка смены плаща: вызывается
    /// хоткеями/двойным кликом по хэндлу (ToggleCloak), при старте и при
    /// ApplyUiConfig — чтобы чекбокс в настройках снимал плащ без рестарта.
    /// Идемпотентен: повторный вызов с тем же значением ничего не делает.
    /// </summary>
    public void SetCloak(bool on)
    {
        if (on == _clickThroughApplied)
        {
            RefreshCloakUi();
            return;
        }
        if (on)
        {
            // Пока открыта карточка подтверждения — клик-проскок не возвращаем:
            // вернём его после закрытия карточки.
            if (!_approvalInteractive) WindowHelper.ApplyClickThrough(this);
            _clickThroughApplied = true;
        }
        else
        {
            WindowHelper.RemoveClickThrough(this);
            _clickThroughApplied = false;
        }
        RefreshCloakUi();
        App.Current.Logger.Info($"Overlay: cloak {(on ? "ON (click-through)" : "OFF (interactive)")}", "UI");
    }

    private void RefreshCloakUi()
    {
        bool on = _clickThroughApplied;
        PressedIconSwap.UpdateKey(CloakButton, "IconCloak");
        if (on)
        {
            CloakButton.SetResourceReference(BackgroundProperty, "HoverBrush");
            CloakButton.Opacity = 0.6;
        }
        else
        {
            CloakButton.Background = Brushes.Transparent;
            CloakButton.Opacity = 1.0;
        }
        CloakButton.ToolTip = _localizer(on ? "L_Ovl_CloakOn" : "L_Ovl_CloakOff");
        // Баннер-индикатор: в сквозном режиме кнопки оверлея недоступны мышью,
        // поэтому баннер текстовый и показывает путь снятия через хоткей/трей.
        CloakBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            var hk = App.Current.Config?.Hotkeys?.OpenSettings;
            if (string.IsNullOrWhiteSpace(hk)) hk = "Ctrl+Shift+S";
            CloakBannerBody.Text = string.Format(_localizer("L_Ovl_CloakBannerFmt"), hk);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Overlay: settings button clicked", "UI");
        App.Current.OpenSettings();
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Overlay: hide button clicked", "UI");
        App.Current.HideOverlay();
    }

    // Ключ текущего показанного уведомления — чтобы то же самое не всплывало
    // повторно после того как юзер его закрыл, но новая проблема — показалась.
    private string _noticeKey = "";

    /// <summary>
    /// Показать/скрыть баннер о проблеме связи (STT/LLM). title=null → скрыть.
    /// Вызывать в UI-потоке.
    /// </summary>
    public void SetConnectionNotice(string? title, string? body, string? settingsTarget = null)
    {
        if (string.IsNullOrEmpty(title))
        {
            // Всё восстановилось — прячем баннер и снимаем «дисмисс».
            NoticeBanner.Visibility = Visibility.Collapsed;
            _noticeKey = "";
            _dismissedTarget = null;
            return;
        }

        // Юзер уже закрыл уведомление по этому компоненту — не навязываемся, пока он не восстановится.
        if (!string.IsNullOrEmpty(_dismissedTarget) && settingsTarget == _dismissedTarget)
            return;

        var key = (title ?? "") + "|" + (body ?? "");
        if (key == _noticeKey && NoticeBanner.Visibility == Visibility.Visible)
            return; // ровно то же самое уже на экране — не дёргаем

        _noticeKey = key;
        _noticeTarget = settingsTarget;
        NoticeTitle.Text = title;
        NoticeBody.Text = body ?? "";
        NoticeActionBtn.Visibility = string.IsNullOrEmpty(settingsTarget) ? Visibility.Collapsed : Visibility.Visible;
        NoticeBanner.Visibility = Visibility.Visible;
    }

    private string? _noticeTarget;
    private string? _dismissedTarget;   // компонент, уведомление по которому юзер закрыл

    // Ссылка «Открыть настройки» из баннера: открывает настройки на нужном разделе и подсвечивает его.
    private void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        App.Current.OpenSettingsAndHighlight(_noticeTarget);
    }

    private void OnNoticeClose(object sender, RoutedEventArgs e)
    {
        NoticeBanner.Visibility = Visibility.Collapsed;
        // Запоминаем закрытый компонент — не всплываем по нему снова, пока он не восстановится.
        _dismissedTarget = _noticeTarget;
    }

    // Ссылка «ответ обрезан» под пузырём ответа — настройки на вкладке «Оверлей»
    // (поле Ui.MaxAnswerChars подсвечивается золотой рамкой).
    private void OnTruncatedHintClick(object sender, RoutedEventArgs e)
    {
        App.Current.OpenSettingsAndHighlight("overlay");
    }

    // §7: ссылка-источник под ответом — открывает окно истории на встрече
    // и подсвечивает найденную строку (ShowMeetingHistory(hit)).
    private void OnPastMeetingSourceClick(object sender, RoutedEventArgs e)
    {
        var hit = (sender as FrameworkContentElement)?.DataContext as MeetingSearchHit;
        if (hit == null) return;
        try { App.Current.ShowMeetingHistory(hit); }
        catch (Exception ex) { App.Current.Logger?.Warn($"Open meeting source failed: {ex.Message}", "UI"); }
    }

    // Лимит длины ответа LLM (Ui.MaxAnswerChars, 0 = без обрезки): обрезка с «…»
    // + флаг IsAnswerTruncated (под пузырём показывается строка-ссылка в настройки).
    // Применяется только к финальному тексту ответа; во время стриминга дельты
    // идут без обрезки (финализация — MarkAnswered). Плейсхолдеры/ошибки
    // устанавливаются напрямую через item.Answer и лимитом не затрагиваются.
    private void ApplyAnswerCharLimit(QaPair item, string? answer)
    {
        var max = App.Current.Config.Ui.MaxAnswerChars;
        if (answer != null && max > 0 && answer.Length > max)
        {
            item.Answer = answer.Substring(0, max) + "…";
            item.IsAnswerTruncated = true;
        }
        else
        {
            item.Answer = answer ?? string.Empty;
            item.IsAnswerTruncated = false;
        }
    }

    // Золотистый тост аудио-устройств: 25 секунд и сам исчезает; крестиком — сразу.
    private DispatcherTimer? _deviceToastTimer;

    /// <summary>Показать тост «новое устройство / смена вывода». Вызывать в UI-потоке.</summary>
    public void ShowDeviceToast(string title, string body)
    {
        DeviceToastTitle.Text = title;
        DeviceToastBody.Text = body;
        DeviceToast.Visibility = Visibility.Visible;
        if (_deviceToastTimer == null)
        {
            _deviceToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            _deviceToastTimer.Tick += DeviceToastTimerTick;
        }
        _deviceToastTimer.Stop();
        _deviceToastTimer.Start();
    }

    private void DeviceToastTimerTick(object? s, EventArgs e)
    {
        _deviceToastTimer?.Stop();
        DeviceToast.Visibility = Visibility.Collapsed;
    }

    private void OnDeviceToastClose(object sender, RoutedEventArgs e)
    {
        _deviceToastTimer?.Stop();
        DeviceToast.Visibility = Visibility.Collapsed;
    }

    // Зелёный тост памяти («LLM добавил факт в память»): 5 секунд и сам исчезает;
    // повторный вызов перезапускает таймер, крестик — сразу.
    private DispatcherTimer? _memoryToastTimer;

    /// <summary>Показать зелёный тост «в память добавлено». Вызывать в UI-потоке.</summary>
    public void ShowMemoryToast(string title, string body)
    {
        MemoryToastTitle.Text = title;
        MemoryToastBody.Text = body;
        MemoryToast.Visibility = Visibility.Visible;
        if (_memoryToastTimer == null)
        {
            _memoryToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _memoryToastTimer.Tick += MemoryToastTimerTick;
        }
        _memoryToastTimer.Stop();
        _memoryToastTimer.Start();
    }

    private void MemoryToastTimerTick(object? s, EventArgs e)
    {
        _memoryToastTimer?.Stop();
        MemoryToast.Visibility = Visibility.Collapsed;
    }

    private void OnMemoryToastClose(object sender, RoutedEventArgs e)
    {
        _memoryToastTimer?.Stop();
        MemoryToast.Visibility = Visibility.Collapsed;
    }

    // ===== Стек общих тостов (PushToast): несколько одновременно, максимум 4.
    // Новый — сверху (Items[0]); при переполнении вытесняется самый старый
    // Info/Progress, а если все — Success/Error, то самый старый вообще.
    // Автоскрытие: Info/Success 3.5c, Error 5c; Progress живёт до Update/Dismiss.
    // Все цвета — через ресурсы темы (Accent/Success/Error), без литералов.
    private const int MaxStackToasts = 4;
    private static readonly TimeSpan StackToastShort = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan StackToastError = TimeSpan.FromSeconds(5);
    private int _lastStackToastId;

    /// <summary>Вью-модель одного тоста стека: Body перезаписывается UpdateToast.</summary>
    public sealed class OverlayToastItem : INotifyPropertyChanged
    {
        private string _body = "";
        public required int Id { get; init; }
        public required ToastKind Kind { get; init; }
        public required string Title { get; init; }
        public required Brush Accent { get; init; }
        public string Body
        {
            get => _body;
            set
            {
                if (value == _body) return;
                _body = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Body)));
            }
        }
        /// <summary>Таймер автозакрытия (null для Progress — живёт до DismissToast).</summary>
        public DispatcherTimer? AutoHide;
        /// <summary>Действие при ручном закрытии крестиком (например, очистка очереди).</summary>
        public Action? OnClose;
        /// <summary>Действие по кнопке действия (например, «Отправить очередь»).</summary>
        public Action? OnAction;
        /// <summary>Подпись кнопки действия; null/пусто → кнопки нет.</summary>
        public string? ActionText { get; init; }
        /// <summary>Кнопка действия видима только если есть и текст, и обработчик.</summary>
        public bool HasAction => !string.IsNullOrEmpty(ActionText) && OnAction != null;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Добавить тост в стек. Возвращает id для UpdateToast/DismissToast.
    /// Вызывать только в UI-потоке оверлея (App.PushOverlayToast сам маршалит).
    /// onClose срабатывает только при ручном закрытии крестиком.</summary>
    public int PushToast(string title, string body, ToastKind kind,
        Action? onClose = null, string? actionText = null, Action? onAction = null)
    {
        var item = new OverlayToastItem
        {
            Id = ++_lastStackToastId,
            Kind = kind,
            Title = title,
            Accent = ToastAccentFor(kind),
            Body = body,
            OnClose = onClose,
            ActionText = actionText,
            OnAction = onAction,
        };
        if (kind != ToastKind.Progress)
        {
            var timer = new DispatcherTimer { Interval = kind == ToastKind.Error ? StackToastError : StackToastShort };
            int id = item.Id;
            timer.Tick += (_, _) => { timer.Stop(); RemoveStackToast(id); };
            item.AutoHide = timer;
        }
        ToastStack.Items.Insert(0, item);
        item.AutoHide?.Start();
        if (ToastStack.Items.Count > MaxStackToasts)
        {
            var victim = OldestEvictableToast();
            if (victim != null) RemoveStackToast(victim.Id);
        }
        return item.Id;
    }

    /// <summary>Обновить тело живого тоста (прогресс-строки). Нет такого id — no-op.</summary>
    public void UpdateToast(int toastId, string body)
    {
        if (FindStackToast(toastId) is { } item) item.Body = body;
    }

    /// <summary>Снять тост по id. Несуществующий id — no-op.</summary>
    public void DismissToast(int toastId) => RemoveStackToast(toastId);

    private void OnStackToastClose(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: int id }) return;
        // Крестик — ручное закрытие: выполняем onClose (если задан), потом снимаем тост.
        FindStackToast(id)?.OnClose?.Invoke();
        RemoveStackToast(id);
    }
    private void OnStackToastAction(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: int id }) return;
        // Кнопка действия тоста (например, «Отправить очередь»). Тост НЕ закрываем —
        // обработчик сам решит (обновит тело или снимет через DismissToast).
        FindStackToast(id)?.OnAction?.Invoke();
    }


    private OverlayToastItem? FindStackToast(int toastId)
    {
        foreach (var o in ToastStack.Items)
            if (o is OverlayToastItem t && t.Id == toastId) return t;
        return null;
    }

    private void RemoveStackToast(int toastId)
    {
        var item = FindStackToast(toastId);
        if (item == null) return;
        item.AutoHide?.Stop();
        ToastStack.Items.Remove(item);
    }

    /// <summary>Жертва вытеснения: старейший Info/Progress, иначе просто самый старый.</summary>
    private OverlayToastItem? OldestEvictableToast()
    {
        OverlayToastItem? oldest = null;
        for (int i = ToastStack.Items.Count - 1; i >= 0; i--)
        {
            if (ToastStack.Items[i] is not OverlayToastItem t) continue;
            oldest ??= t; // конец коллекции — самые старые, первый попавшийся и есть oldest
            if (t.Kind is ToastKind.Info or ToastKind.Progress) return t;
        }
        return oldest;
    }

    private Brush ToastAccentFor(ToastKind kind)
    {
        var key = kind switch
        {
            ToastKind.Success => "SuccessBrush",
            ToastKind.Error => "ErrorBrush",
            _ => "AccentBrush",
        };
        return TryFindResource(key) as Brush
               ?? TryFindResource("AccentBrush") as Brush
               ?? TryFindResource("TextMutedBrush") as Brush
               ?? Brushes.Transparent; // ресурс отсутствует — не красим, цвета не выдумываем
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        // У безрамочного прозрачного окна WindowState.Minimized даёт «огрызок» в углу,
        // а ShowInTaskbar=False лишает таскбар-кнопки. Поэтому сворачиваем = прячем в трей;
        // возврат — из трея «Показать оверлей» (ShowOverlay восстановит полный размер) или Ctrl+Shift+H.
        App.Current.Logger.Info("Overlay: minimize → hide to tray", "UI");
        App.Current.HideOverlay();
        App.Current.NotifyMinimizedToTray();
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        App.Current.Logger.Info($"Overlay: maximize toggled → {WindowState}", "UI");
    }

    private void OnScreenShotClick(object sender, RoutedEventArgs e)
    {
        ToggleScreenCaptureVisibility();
    }

    public void ToggleScreenCaptureVisibility()
    {
        _screenCaptureVisible = !_screenCaptureVisible;
        if (_screenCaptureVisible)
        {
            WindowHelper.RemoveExcludeFromCapture(this);
            WindowHelper.RemoveClickThrough(this);
            ScreenShotButton.Opacity = 1.0;
            SetStateRing(ScreenShotButton, "WarnBrush"); // золотое кольцо = режим скриншота активен
            ScreenShotButton.ToolTip = _localizer("L_Ovl_ScreenshotOff");
            App.Current.Logger.Info("Overlay: screen capture VISIBLE (for screenshots)", "UI");
        }
        else
        {
            WindowHelper.ApplyExcludeFromCapture(this);
            if (_clickThroughApplied && !_approvalInteractive) WindowHelper.ApplyClickThrough(this);
            ScreenShotButton.Opacity = 1.0;
            SetStateRing(ScreenShotButton, null); // вернуть штатный вид
            ScreenShotButton.ToolTip = _localizer("L_Ovl_ScreenshotOn");
            App.Current.Logger.Info("Overlay: screen capture HIDDEN", "UI");
        }
    }

    private void OnAutoScrollClick(object sender, RoutedEventArgs e)
    {
        _autoScroll = !_autoScroll;
        // Повторное включение автоскролла — прыжок к самому новому элементу
        // (раньше список оставался на старом месте до следующего сообщения).
        if (_autoScroll) { _scrollAnchorItem = null; ProgrammaticScrollToEnd(); }
        RefreshAutoScrollButton();
        App.Current.Logger.Info($"Overlay: auto-scroll {(_autoScroll ? "ON" : "OFF")}", "UI");
    }

    // Активный автоскролл подсвечивается жёлтым (как пауза/скриншот).
    private void RefreshAutoScrollButton()
    {
        if (AutoScrollButton == null) return;
        AutoScrollButton.Opacity = 1.0;
        if (_autoScroll)
        {
            SetStateRing(AutoScrollButton, "WarnBrush");
            AutoScrollButton.ToolTip = _localizer("L_Ovl_AutoScrollOn");
        }
        else
        {
            SetStateRing(AutoScrollButton, null);
            AutoScrollButton.ToolTip = _localizer("L_Ovl_AutoScrollOff");
        }
    }

    private void OnSilenceSpeedDown(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        cfg.SilenceSeconds = Math.Max(0.4, cfg.SilenceSeconds - 0.1);
        ApplySilenceDisplay(cfg);
        App.Current.AudioEngine.UpdateConfig(cfg);
        App.Current.Logger.Info($"Overlay: silence speed DOWN → {cfg.SilenceSeconds:F1}s", "UI");
    }

    private void OnSilenceSpeedUp(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        cfg.SilenceSeconds = Math.Min(10.0, cfg.SilenceSeconds + 0.1);
        ApplySilenceDisplay(cfg);
        App.Current.AudioEngine.UpdateConfig(cfg);
        App.Current.Logger.Info($"Overlay: silence speed UP → {cfg.SilenceSeconds:F1}s", "UI");
    }

    private void ApplySilenceDisplay(AudioConfig cfg)
    {
        if (SilenceSlider != null) SilenceSlider.Value = Math.Round(cfg.SilenceSeconds, 1);
        if (ChunkSlider != null) ChunkSlider.Value = cfg.ChunkMaxSeconds;
    }

    private void OnChunkLenDown(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        cfg.ChunkMaxSeconds = Math.Max(8, cfg.ChunkMaxSeconds - 10);
        ApplySilenceDisplay(cfg);
        App.Current.AudioEngine.UpdateConfig(cfg);
        App.Current.Logger.Info($"Overlay: chunk len DOWN → {cfg.ChunkMaxSeconds}s", "UI");
    }

    private void OnChunkLenUp(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        cfg.ChunkMaxSeconds = Math.Min(300, cfg.ChunkMaxSeconds + 10);
        ApplySilenceDisplay(cfg);
        App.Current.AudioEngine.UpdateConfig(cfg);
        App.Current.Logger.Info($"Overlay: chunk len UP → {cfg.ChunkMaxSeconds}s", "UI");
    }

    private void OnSendNow(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Overlay: send now clicked", "UI");
        App.Current.FlushAndSend();
    }

    private void OnChatInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnChatSend(sender, e);
            e.Handled = true;
        }
    }

    private async void OnChatSend(object sender, RoutedEventArgs e)
    {
        var text = ChatInputBox.Text?.Trim();
        var flow = App.Current.ChatImages;
        // Картинка без текста отправляется (ТЗ §1): пустое поле + черновик — тоже отправка.
        if (string.IsNullOrWhiteSpace(text) && flow?.HasDraft != true) return;
        if (flow != null)
        {
            // Поле НЕ очищаем: flow оставляет черновик при LlmOff/Unsupported и при
            // паузе; App.SendChatWithImagesAsync сам очистит поле через ClearChatInput,
            // когда отправка реально ушла (решение координатора).
            await flow.SendChatAsync(text ?? "");
        }
        else
        {
            ChatInputBox.Text = "";
            await App.Current.SendSecretMessage(text!);
        }
    }

    /// <summary>Очистить поле ввода (вызывает App только когда отправка реально ушла).</summary>
    public void ClearChatInput() => ChatInputBox.Text = "";

    // ---- Черновик изображений (feature-chat-images, ТЗ §3) ----
    // Подписка на App.ChatImages ленивая: фасад поднимается позже стора. Дергаем
    // AttachImageFlow из Loaded/вставки/отправки — первый живой контакт подпишется.

    private ChatImageFlow? _imageFlow;

    /// <summary>Подписаться на фасад картинок (идемпотентно); без фасада строки скрыты.</summary>
    private void AttachImageFlow()
    {
        var flow = App.Current.ChatImages;
        if (ReferenceEquals(flow, _imageFlow)) return;
        if (_imageFlow != null) _imageFlow.DraftChanged -= OnImageDraftChanged;
        _imageFlow = flow;
        if (_imageFlow != null) _imageFlow.DraftChanged += OnImageDraftChanged;
        RefreshImageDrafts();
    }

    private void OnImageDraftChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshImageDrafts();
        else Dispatcher.BeginInvoke(new Action(RefreshImageDrafts));
    }

    /// <summary>Перечитать полосу черновика и строку-уведомление (DraftNotice).</summary>
    private void RefreshImageDrafts()
    {
        var flow = _imageFlow ?? App.Current.ChatImages;
        var tiles = flow?.DraftTiles;
        // ItemsSource надо давать НОВЫЙ список: переприсвоение той же ссылки List<>
        // WPF игнорирует (второй+ тайл черновика не появлялся бы в UI — баг i2).
        ImageDraftList.ItemsSource = tiles is { Count: > 0 } ? tiles.ToList() : null;
        ImageDraftRow.Visibility = tiles is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        var notice = flow?.DraftNotice;
        ImageDraftNotice.Text = notice ?? "";
        ImageDraftNotice.Visibility = string.IsNullOrEmpty(notice)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Ctrl+V в поле ввода: буфер с изображением — тайл в черновик,
    /// текстовая вставка не выполняется (иначе двойная вставка, ТЗ §3.1).</summary>
    private void OnChatInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        AttachImageFlow();
        var flow = App.Current.ChatImages;
        if (flow == null) return; // нет стора — обычная текстовая вставка
        var imgs = ChatImageCodec.GetClipboardImages();
        if (imgs.Count == 0) return;
        flow.RequestAddDraftFromClipboard();
        e.CancelCommand();
    }

    /// <summary>Список тайлов, к которому принадлежит элемент тайла (черновик/пузырь).</summary>
    private IReadOnlyList<ChatImageTileVm> TilesAround(DependencyObject? element, ChatImageTileVm tile)
    {
        var ic = FindAncestor<ItemsControl>(element);
        if (ic?.ItemsSource is IReadOnlyList<ChatImageTileVm> list && list.Contains(tile))
            return list;
        // Фолбэк: элемент без ItemsSource-списка — черновик фасада.
        var draft = App.Current.ChatImages?.DraftTiles;
        return draft != null && draft.Contains(tile) ? draft : new List<ChatImageTileVm> { tile };
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void OpenImageViewer(DependencyObject? anchor, ChatImageTileVm tile)
    {
        var tiles = TilesAround(anchor, tile);
        var idx = 0;
        for (var i = 0; i < tiles.Count; i++)
            if (ReferenceEquals(tiles[i], tile)) { idx = i; break; }
        var owner = anchor != null ? Window.GetWindow(anchor) : this;
        ImageViewerWindow.Show(owner ?? this, tiles, idx, App.Current.ChatImages);
    }

    /// <summary>Клик по тайлу (черновик или пузырь) — просмотрщик.</summary>
    private void OnImageTileClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ChatImageTileVm tile) return;
        if (tile.State == ChatImageTileState.Error) return; // ошибочные не открываем (ТЗ §3.8)
        OpenImageViewer(fe, tile);
        e.Handled = true;
    }

    private static ChatImageTileVm? TileFrom(object sender)
        => (sender as FrameworkElement)?.Tag as ChatImageTileVm;

    private void OnImageDraftRemoveClick(object sender, RoutedEventArgs e)
    {
        var tile = TileFrom(sender);
        if (tile == null) return;
        App.Current.ChatImages?.RemoveDraft(tile.ImageId);
        e.Handled = true;
    }

    private void OnImageMenuOpen(object sender, RoutedEventArgs e)
    {
        var tile = TileFrom(sender);
        if (tile == null) return;
        var anchor = (sender as MenuItem)?.Parent is ContextMenu cm ? cm.PlacementTarget : null;
        OpenImageViewer(anchor, tile);
    }

    private void OnImageMenuRemoveDraft(object sender, RoutedEventArgs e)
    {
        var tile = TileFrom(sender);
        if (tile != null) App.Current.ChatImages?.RemoveDraft(tile.ImageId);
    }

    private void OnImageMenuAddToMsg(object sender, RoutedEventArgs e)
    {
        var tile = TileFrom(sender);
        if (tile != null) App.Current.ChatImages?.AddDraftFromStored(tile.ImageId);
    }

    /// <summary>«Копировать изображение» — PNG+bitmap в буфер + тост L_ImgCopied (ТЗ §5).</summary>
    private async void OnImageMenuCopy(object sender, RoutedEventArgs e)
    {
        var tile = TileFrom(sender);
        var flow = App.Current.ChatImages;
        if (tile == null || flow == null) return;
        try
        {
            var bytes = await flow.LoadOriginalBytesAsync(tile.ImageId);
            if (bytes is not { Length: > 0 }) return;
            var decoded = ChatImageCodec.TryDecode(bytes, tile.FileName);
            if (decoded == null) return;
            var png = tile.Mime == "image/png" ? bytes : ChatImageCodec.ToPngBytes(decoded.Bitmap);
            ChatImageCodec.PutImageToClipboard(png, decoded.Bitmap);
            App.Current.PushOverlayToast(ToastKind.Info,
                App.TT("L_ImgCopied", "Изображение скопировано"), tile.FileName);
        }
        catch { /* буфер занят/файл ушёл — тихо, статус уже показывался flow'ом */ }
    }

    /// <summary>«Сохранить как…» — байты оригинала побайтово, имя/расширение оригинала.</summary>
    private async void OnImageMenuSaveAs(object sender, RoutedEventArgs e)
    {
        var tile = TileFrom(sender);
        var flow = App.Current.ChatImages;
        if (tile == null || flow == null) return;
        byte[]? bytes;
        try { bytes = await flow.LoadOriginalBytesAsync(tile.ImageId); }
        catch { return; }
        if (bytes is not { Length: > 0 }) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = App.TT("L_ImgSaveTitle", "Сохранить изображение"),
            FileName = tile.FileName,
            Filter = "Image|*" + System.IO.Path.GetExtension(tile.FileName) + "|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return; // отмена — тихо
        try { System.IO.File.WriteAllBytes(dlg.FileName, bytes); }
        catch (Exception ex) { App.Current.Logger.Error("ImageViewer: save failed", ex, "UI"); }
    }

    // ---- Прикреплённые документы (чипы над полем ввода) ----
    // Наполнение — App.RefreshAttachmentChips: он читает AttachmentStore и передаёт
    // готовые VM; окно магазина не знает. Клик по крестику уходит в App —
    // там же UnlinkFromMeeting + перерисовка.

    /// <summary>Чипы файлов текущей встречи (ItemsSource строки над полем ввода).</summary>
    public ObservableCollection<AttachmentChipVm> AttachmentChips { get; } = new();

    /// <summary>True — оверлей исключён из захвата экрана (стелс); модальные окна
    /// наследуют exclude-from-capture только в этом режиме.</summary>
    public bool IsHiddenFromCapture => !_screenCaptureVisible;

    /// <summary>Перерисовать строку чипов. Пустой список — строка прячется.</summary>
    public void SetAttachmentChips(IReadOnlyList<AttachmentChipVm> chips)
    {
        AttachmentChips.Clear();
        foreach (var c in chips) AttachmentChips.Add(c);
        AttachmentChipsRow.Visibility = chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Подсчёт скрытых чипов валиден только после генерации контейнеров и меры —
        // доезжаем по завершении раскладки (иначе TranslatePoint/контейнеры ещё пусты).
        Dispatcher.BeginInvoke(new Action(UpdateMoreAttachmentsButton),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnAttachFilesClick(object sender, RoutedEventArgs e)
    {
        // Открытие диалога и вся логика импорта — в App (см. App.OpenAttachmentPicker).
        _ = App.Current.OpenAttachmentPicker();
    }

    private async void OnAttachmentRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string docId })
            await App.Current.RemoveMeetingAttachmentAsync(docId);
    }

    /// <summary>Считает чипы, не влезшие в две строки (ScrollViewer с MaxHeight=62),
    /// и показывает кнопку «Ещё N».</summary>
    private void UpdateMoreAttachmentsButton()
    {
        var ic = AttachmentChipsList;
        if (ic == null || ic.Items.Count == 0)
        {
            MoreAttachmentsBtn.Visibility = Visibility.Collapsed;
            return;
        }
        var hidden = 0;
        for (var i = 0; i < ic.Items.Count; i++)
        {
            if (ic.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            // Низ чипа в координатах клипующего ScrollViewer за пределами 62 — он под обрезом.
            if (c.TranslatePoint(new System.Windows.Point(0, c.ActualHeight), AttachmentChipsScroll).Y
                > AttachmentChipsScroll.MaxHeight + 0.5)
                hidden++;
        }
        if (hidden > 0)
        {
            MoreAttachmentsBtn.Content = string.Format(_localizer("L_Att_MoreFmt"), hidden);
            MoreAttachmentsBtn.Visibility = Visibility.Visible;
        }
        else
        {
            MoreAttachmentsBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnExpandAnswerClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QaPair pair && !string.IsNullOrWhiteSpace(pair.Answer))
        {
            App.Current.Logger.Info($"Overlay: expand answer for '{pair.Question.Substring(0, Math.Min(40, pair.Question.Length))}…'", "UI");
            await App.Current.ExpandAnswer(pair);
        }
    }

    private void OnClearExpandedClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QaPair pair)
        {
            pair.ExpandedAnswer = string.Empty;
            App.Current.Logger.Info("Overlay: cleared expanded answer", "UI");
        }
    }

    private void OnFocusPairClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QaPair pair)
        {
            foreach (var item in History)
                item.IsFocused = false;
            pair.IsFocused = true;
            App.Current.Logger.Info("Overlay: focus on pair", "UI");
        }
    }

    private void OnClearAllFocusClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in History)
            item.IsFocused = false;
        App.Current.Logger.Info("Overlay: cleared all focus", "UI");
    }

    private void OnExportTranscript(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Overlay: export transcript clicked", "UI");
        App.Current.ExportTranscript();
        ShowExportDoneCheckmark();
    }

    // Макет 6a: после экспорта иконка на ~1.5с меняется на зелёную галочку
    private async void ShowExportDoneCheckmark()
    {
        try
        {
            PressedIconSwap.UpdateKey(ExportTransBtn, "IconExportDone");
            await Task.Delay(1500);
            PressedIconSwap.UpdateKey(ExportTransBtn, "IconExport");
        }
        catch (Exception ex) { App.Current.Logger.Error("ShowExportDoneCheckmark failed", ex, "UI"); }
    }

    private async void OnExportAiSummary(object sender, RoutedEventArgs e)
    {
        App.Current.Logger.Info("Overlay: export AI summary clicked", "UI");
        await App.Current.ExportAiSummary();
    }

    // «Копировать всё» раньше копировало ОДНУ пару (по клику) — юзер ждал все сообщения экрана.
    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in History)
            sb.AppendLine(p.Question).AppendLine("---").AppendLine(ToVisibleText(p.Answer)).AppendLine();
        CopyToClipboard(sb.ToString().TrimEnd());
    }

    // Полная история сессии (экран держит ~30 пар, старые уезжают — тут ВСЁ с запуска).
    private void OnCopyFullHistoryClick(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        lock (_fullHistory)
        {
            foreach (var p in _fullHistory)
                sb.AppendLine(p.Question).AppendLine("---").AppendLine(ToVisibleText(p.Answer)).AppendLine();
        }
        CopyToClipboard(sb.ToString().TrimEnd());
    }

    // Пары за ВСЮ сессию (ссылки на те же QaPair — ответы дозаполняются сами). Не тримится.
    private readonly List<QaPair> _fullHistory = new();

    // Ссылка в тексте ответа: открываем по явному клику юзера. Схему (http/https/mailto)
    // проверяет сам компонент и в событие отдаёт уже безопасный Url — здесь только запуск.
    // Подписка — из Loaded шаблона: тип аргумента события компонента не нужен, лямбда
    // выводит его сама (ConditionalWeakTable защищает от повторной подписки при
    // переподключении элемента к визуальному дереву).
    private readonly ConditionalWeakTable<BrainstormBuddy.Controls.MarkdownView, object> _markdownLinkSubscribed = new();

    private void OnMarkdownViewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not BrainstormBuddy.Controls.MarkdownView mv) return;
        if (_markdownLinkSubscribed.TryGetValue(mv, out _)) return;
        _markdownLinkSubscribed.Add(mv, new object());
        mv.LinkClicked += (s, args) => MarkdownLinkOpener.Open(args.Url);
    }

    private void OnCopyQuestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QaPair pair)
            CopyToClipboard(pair.Question);
    }

    private void OnCopyAnswerClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QaPair pair)
            CopyToClipboard(ToVisibleText(pair.Answer));
    }

    // Копирование ответа — ВИДИМЫЙ текст без служебной разметки. Исходник ответа
    // (QaPair.Answer) хранится с markdown как есть, а в буфер обмена попадает ровно то,
    // что видит юзер на экране.
    private static string ToVisibleText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        try
        {
            return BrainstormBuddy.Markdown.MarkdownSupport.ToPlainText(
                BrainstormBuddy.Markdown.MarkdownSupport.Parse(raw));
        }
        catch (Exception ex)
        {
            // Разбор не должен падать, но копирование — действие юзера: отдаём исходник.
            App.Current?.Logger?.Warn($"ToVisibleText failed: {ex.Message}", "UI");
            return raw;
        }
    }

    private static void CopyToClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (Exception ex) { App.Current?.Logger?.Error("CopyToClipboard failed", ex, "UI"); }
    }

    // Семантическая смена иконки кнопки нижней пилюли (вкл/выкл-вариант): через
    // PressedIconSwap.UpdateKey — заодно переключается и Icon*Pressed-арт зажатия.
    private void SetButtonIcon(System.Windows.Controls.Button btn, string iconKey)
    {
        PressedIconSwap.UpdateKey(btn, iconKey);
    }

    // Активное состояние state-кнопок = кольцо на рамке Bd внутри шаблона.
    // Старый механизм (IconBrush-тинт) мёртв на BuddyV2-иконках с baked-цветом;
    // кольцо читается на любом глифе. brushKey=null → кольцо снять.
    private void SetStateRing(System.Windows.Controls.Button btn, string? brushKey)
    {
        if (btn.Template.FindName("Bd", btn) is not Border bd) return;
        if (brushKey == null)
        {
            bd.ClearValue(Border.BorderBrushProperty);
            bd.ClearValue(Border.BorderThicknessProperty);
        }
        else
        {
            bd.SetResourceReference(Border.BorderBrushProperty, brushKey);
            bd.BorderThickness = new Thickness(0.75);
        }
    }

    // «Мик выкл» на нижней панели: золотая иконка = микрофон заглушён (динамик живёт).
    // Полная пауза аудио (Ctrl+Shift+P) здесь не отображается — см. UpdateCombinedStatus.
    public void RefreshMicMuteButton()
    {
        try
        {
            if (MicMuteButton == null) return;
            if (App.Current.Config.Ui.MicMuted)
            {
                SetStateRing(MicMuteButton, "WarnBrush"); // золотое кольцо = заглушён
                SetButtonIcon(MicMuteButton, "IconMicOff");
                MicMuteButton.ToolTip = _localizer("L_Ovl_MicMuted");
            }
            else
            {
                SetButtonIcon(MicMuteButton, "IconMic");
                SetStateRing(MicMuteButton, null); // вернуть штатный вид
                MicMuteButton.ToolTip = FindResource("L_TT_MicMute");
            }
            UpdateCombinedStatus();
        }
        catch (Exception ex) { App.Current.Logger.Error("RefreshMicMuteButton failed", ex, "UI"); }
    }

    // LLM-пауза больше не имеет иконки в шапке (убрана по фидбеку). Функция доступна по
    // хоткею Ctrl+Shift+Y; состояние отражается в чипе LLM и общем статусе.
    public void RefreshLlmPauseButton()
    {
        try { UpdateCombinedStatus(); }
        catch (Exception ex) { App.Current.Logger.Error("RefreshLlmPauseButton failed", ex, "UI"); }
    }

    // «LLM выкл» (чистый транскрибатор): активное состояние — золотое кольцо, как у микрофона.
    public void RefreshLlmOffButton()
    {
        try
        {
            if (LlmOffButton == null) return;
            if (App.Current.Config.Ui.LlmDisabled)
            {
                SetStateRing(LlmOffButton, "WarnBrush"); // золотое кольцо = LLM выключен
                SetButtonIcon(LlmOffButton, "IconLlmOff");
                LlmOffButton.ToolTip = _localizer("L_Ovl_LlmOffActive");
            }
            else
            {
                SetButtonIcon(LlmOffButton, "IconLlm");
                SetStateRing(LlmOffButton, null); // вернуть штатный вид
                LlmOffButton.ToolTip = FindResource("L_TT_LlmOff");
            }
        }
        catch (Exception ex) { App.Current.Logger.Error("RefreshLlmOffButton failed", ex, "UI"); }
    }

    // «Динамик выкл» (мьют loopback): активное состояние — та же золотая рамка.
    public void RefreshSpeakerMuteButton()
    {
        try
        {
            if (SpeakerMuteButton == null) return;
            if (App.Current.Config.Ui.LoopbackMuted)
            {
                SetStateRing(SpeakerMuteButton, "WarnBrush"); // золотое кольцо = динамик заглушен
                SetButtonIcon(SpeakerMuteButton, "IconSpeakerOff");
                SpeakerMuteButton.ToolTip = _localizer("L_Ovl_SpeakerMuted");
            }
            else
            {
                SetButtonIcon(SpeakerMuteButton, "IconSpeaker");
                SetStateRing(SpeakerMuteButton, null);
                SpeakerMuteButton.ToolTip = FindResource("L_TT_SpeakerMute");
            }
        }
        catch (Exception ex) { App.Current.Logger.Error("RefreshSpeakerMuteButton failed", ex, "UI"); }
    }

    // Статусная строка теперь единая: приоритетное состояние резолвит App
    // (OverlayStatusResolver) — здесь только триггерим пересчёт.
    private void UpdateCombinedStatus() => App.Current.RefreshOverlayStatus();

    // В строке может жить транзиентный текст (OCR-слежение, «Отправлено…», протокол,
    // вспышка FlashStatus) — резолвленный «Готов…» не должен затирать его мгновенно.
    // Ставится SetStatus/FlashStatus, снимается при первом же реальном применении статуса.
    private bool _transientHold;

    /// <summary>Применить резолвленный статус: строка StatusText + плашка пустого состояния.</summary>
    public void ApplyStatus(BrainstormBuddy.Services.OverlayStatus st)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ApplyStatus(st)); return; }
        if (!_transientHold || st.IsError)
        {
            StatusText.Text = st.Title;
            var brushKey = st.IsError && TryFindResource("WarnBrush") != null ? "WarnBrush"
                : st.IsError ? "AccentBrush" : "TextMutedBrush";
            StatusText.SetResourceReference(ForegroundProperty, brushKey);
            _transientHold = false;
        }
        // Плашка холодного старта — не статус-строка: обновляем всегда.
        ColdStartTitle.Text = st.Title;
        if (st.Detail != null) { ColdStartBody.Text = st.Detail; ColdStartBody.Visibility = Visibility.Visible; }
        else { ColdStartBody.Text = ""; ColdStartBody.Visibility = Visibility.Collapsed; }
    }

    public void SetStatus(string text)
    {
        App.Current.Logger.Debug($"Overlay.Status = '{text}'", "UI");
        Dispatcher.Invoke(() =>
        {
            if (App.Current.IsPaused) return;
            if (App.Current.IsLlmPaused) return;
            StatusText.Text = text;
            StatusText.SetResourceReference(ForegroundProperty, "TextMutedBrush");
            _transientHold = true;
        });
    }

    // Кратко подсветить статус (напр. «✓ сохранено …») зелёным и вернуть резолвленное состояние.
    private int _flashToken;
    public void FlashStatus(string text, int seconds = 4)
    {
        Dispatcher.Invoke(async () =>
        {
            _transientHold = true;
            var myToken = ++_flashToken;
            StatusText.Text = text;
            StatusText.SetResourceReference(ForegroundProperty, "SuccessBrush");
            await Task.Delay(seconds * 1000);
            if (myToken != _flashToken) return; // перезаписано новым flash — не трогаем
            _transientHold = false;
            App.Current.RefreshOverlayStatus(); // вернуть резолвленный статус (сам выставит цвет)
        });
    }

    public QaPair AddHistoryItem(string question, string answer)
    {
        QaPair? item = null;
        Dispatcher.Invoke(() =>
        {
            item = new QaPair { Question = question, Status = QaStatus.Answered, ShowMeetingSources = _uiConfig.ShowMeetingSources };
            ApplyAnswerCharLimit(item, answer);
            History.Add(item);
            lock (_fullHistory) _fullHistory.Add(item);
            TrimHistory();
            if (_autoScroll) ProgrammaticScrollToEnd();
        });
        return item!;
    }

    // === Якорь читаемого места при выключенном автоскролле ===
    // Якорь = сообщение (QaPair-референс — устойчивый идентификатор элемента) +
    // offset его верха от верха вьюпорта. Переснимается ТОЛЬКО на пользовательскую
    // прокрутку (классификация — OverlayScrollClassifier). На любые изменения
    // содержимого (новые пузыри, стрим-дельты, картинки, resize, трим) LayoutUpdated
    // через RestoreAnchorIfNeeded возвращает якорь на место — читаемая строка
    // не «уплывает» (допуск ≤0.5 DIP).
    private QaPair? _scrollAnchorItem;
    private double _scrollAnchorOffset;
    private bool _suppressAnchorRecapture;   // синхронный приоритетный маркер программного ScrollTo*
    private int _lastUserScrollTick = int.MinValue;        // последний ввод над скроллом
    private int _lastProgrammaticScrollTick = int.MinValue; // последний программный ScrollTo*
    private bool _scrollToEndScheduled;                      // уже стоит отложенный догоняющий скролл
    private double _lastAutoScrollExtent;                    // extent списка на прошлом проходе layout

    private const int HistoryTargetCap = 30;   // целевой размер списка
    private const int HistoryHardCap = 120;    // потолок окна без защищённого якоря

    private void CaptureScrollAnchor()
    {
        var (item, offset) = FindFirstVisibleItem();
        _scrollAnchorItem = item;
        _scrollAnchorOffset = offset;
    }

    /// <summary>Отметить пользовательский ввод над скроллом: следующий сдвиг
    /// вьюпорта в окне OverlayScrollClassifier.ScrollMarkWindowMs считается
    /// пользовательской прокруткой и переснимает якорь читаемого места.</summary>
    private void MarkUserScroll() => _lastUserScrollTick = Environment.TickCount;

    /// <summary>Программный скролл в конец: помечаем, чтобы отложенный ScrollChanged
    /// не переснял якорь при выключенном автоскролле.</summary>
    private void ProgrammaticScrollToEnd()
    {
        _lastProgrammaticScrollTick = Environment.TickCount;
        HistoryScroll.ScrollToEnd();
        ScheduleScrollToEndAfterLayout();
    }

    /// <summary>Догоняющий скролл после пересчёта высоты содержимого. Перерисовка
    /// markdown меняет высоту пузыря ответа уже после того, как текст обновился, поэтому
    /// мгновенный ScrollToEnd() попадает в устаревшую высоту и низ списка «уезжает».
    /// Повторяем скролл после прохода layout: приоритет Background ниже Render/Loaded —
    /// значит, отложенный скролл выполняется после пересчёта и после возможного
    /// восстановления смещения самим компонентом. Запросы сливаются: на поток дельт —
    /// не больше одного отложенного скролла.</summary>
    private void ScheduleScrollToEndAfterLayout()
    {
        if (_scrollToEndScheduled) return;
        _scrollToEndScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollToEndScheduled = false;
            if (!_autoScroll || !IsLoaded) return;
            _lastProgrammaticScrollTick = Environment.TickCount;
            HistoryScroll.ScrollToEnd();
        }));
    }

    /// <summary>Переснимать ли якорь из LayoutUpdated: только когда якоря нет
    /// вовсе или он легально ушёл из списка. Если элемент в списке есть, но
    /// контейнер временно не реализован — якорь сохраняем (вернёмся на следующем
    /// проходе layout), иначе переснятие зафиксировало бы сдвинутую позицию.</summary>
    private bool ShouldRecaptureAnchor() =>
        _scrollAnchorItem == null || !History.Contains(_scrollAnchorItem);

    // Контейнер первого элемента, пересекающего верх вьюпорта. StackPanel не виртуализируется —
    // ContainerFromItem надёлен. Возвращает offset контейнера от верха вьюпорта (может быть <0).
    private (QaPair? item, double offset) FindFirstVisibleItem()
    {
        try
        {
            foreach (var it in History)
            {
                if (HistoryList.ItemContainerGenerator.ContainerFromItem(it) is not FrameworkElement c)
                    continue; // контейнер ещё не создан (только что добавлен) — пропускаем
                var top = c.TransformToVisual(HistoryScroll).Transform(new System.Windows.Point(0, 0)).Y;
                if (top + c.ActualHeight > 0)
                    return (it, top);
            }
        }
        catch (Exception ex) { App.Current?.Logger?.Debug($"FindFirstVisibleItem: {ex.Message}", "UI"); }
        return (null, 0);
    }

    // Вернёт true, если якорь был и позиция исправлена/подтверждена. Ложь — якоря нет
    // или он не реализован; вызывающий решает через ShouldRecaptureAnchor, переснимать ли.
    private bool RestoreAnchorIfNeeded()
    {
        var anchor = _scrollAnchorItem;
        if (anchor == null) return false;
        if (!History.Contains(anchor) ||
            HistoryList.ItemContainerGenerator.ContainerFromItem(anchor) is not FrameworkElement c)
            return false;
        var top = c.TransformToVisual(HistoryScroll).Transform(new System.Windows.Point(0, 0)).Y;
        var delta = top - _scrollAnchorOffset;
        if (Math.Abs(delta) <= 0.5) return true;
        // ScrollToVerticalOffset → ScrollChanged приходит отложенно, после
        // measure/arrange — try/finally его может не покрыть. Отдельно метим
        // tick, чтобы отложенное событие тоже классифицировалось программным.
        _suppressAnchorRecapture = true;
        _lastProgrammaticScrollTick = Environment.TickCount;
        try { HistoryScroll.ScrollToVerticalOffset(HistoryScroll.VerticalOffset + delta); }
        finally { _suppressAnchorRecapture = false; }
        return true;
    }

    // Трим History до целевого размера: при выкл. автоскролле якорь и все элементы
    // ниже него защищены БЕЗ ограничения потолком — читаемый элемент не удаляется
    // никогда (§4: раньше hardCap мог срезать якорь и сдвинуть читаемое место).
    private void TrimHistory()
    {
        int firstProtected = History.Count; // по умолчанию защищённых нет
        if (!_autoScroll)
        {
            if (_scrollAnchorItem is { } a && History.IndexOf(a) is int ai && ai >= 0)
                firstProtected = ai;
            else
            {
                var (vis, _) = FindFirstVisibleItem();
                if (vis != null && History.IndexOf(vis) is int vi && vi >= 0)
                    firstProtected = vi;
            }
        }
        int remove = OverlayHistoryTrim.ComputeHeadRemoveCount(
            History.Count, HistoryTargetCap, HistoryHardCap, firstProtected);
        if (remove <= 0) return;
        for (int i = 0; i < remove; i++) History.RemoveAt(0);
    }

    // transcriptOnly («LLM выкл»): пузырь чистого транскрибатора — без строки «ожидание»,
    // сразу Answered с пустым ответом (блок «ОТВЕТ» прячет AnswerSectionVisibility),
    // LLM-блинк не зажигаем — отправки не будет.
    // ocrLatencyMs > 0 → источник OCR (экран): под вопросом показываем «OCR Xs»,
    // а STT-времена должны приходить null (STT в этом потоке не участвовал).
    public QaPair AddSendingItem(string question, double audioSeconds,
        DateTime chunkReadyAt, DateTime? sttSentAt, DateTime? sttReceivedAt, double sttLatencyMs,
        bool transcriptOnly = false, double ocrLatencyMs = 0, string? speakerId = null,
        IReadOnlyList<ChatImageTileVm>? imageTiles = null)
    {
        QaPair? item = null;
        Dispatcher.Invoke(() =>
        {
            item = new QaPair
            {
                Question = question,
                Answer = transcriptOnly ? "" : App.Current.T("L_WaitingLlm"),
                Status = transcriptOnly ? QaStatus.Answered : QaStatus.Sending,
                AudioChunkSeconds = audioSeconds,
                ChunkReadyAt = chunkReadyAt,
                SttSentAt = sttSentAt,
                SttReceivedAt = sttReceivedAt,
                SttLatencyMs = sttLatencyMs,
                OcrLatencyMs = ocrLatencyMs,
                SpeakerId = speakerId,
                ImageTiles = imageTiles?.ToList() ?? new(),
                ShowMeetingSources = _uiConfig.ShowMeetingSources,
            };
            // Чипы всех спикеров пузыря: метки «[S<n>]» из текста + доминантный.
            item.SetSpeakers(question, speakerId, App.Current.GetSpeakerAlias);
            item.SpeakerAlias = App.Current.GetSpeakerAlias(speakerId);
            History.Add(item);
            lock (_fullHistory) _fullHistory.Add(item);
            TrimHistory();
            if (!transcriptOnly) _llmBlink = true;
            if (_autoScroll) ProgrammaticScrollToEnd();
        });
        return item!;
    }


    // Развязка STT↔LLM: перегрузка, помечающая КОНКРЕТНЫЙ пузырь (несколько могут быть «в полёте»).
    public void MarkLlmSent(QaPair item, DateTime llmSentAt)
    {
        Dispatcher.Invoke(() =>
        {
            item.LlmSentAt = llmSentAt;
            item.OnPropertyChanged("LlmSentText");
        });
    }

    // Debounce: drag слайдера генерирует десятки ValueChanged/сек — каждый тик дёргал
    // UpdateConfig (lock с аудио-потоком + лог-спам). Применяем один раз, 300мс после
    // последнего изменения.
    private DispatcherTimer? _sliderDebounce;

    private void ScheduleAudioConfigUpdate()
    {
        if (_sliderDebounce == null)
        {
            _sliderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _sliderDebounce.Tick += (s, e) =>
            {
                _sliderDebounce!.Stop();
                var cfg = App.Current.Config.Audio;
                App.Current.AudioEngine.UpdateConfig(cfg);
                App.Current.Logger.Info($"Overlay: sliders applied → silence={cfg.SilenceSeconds:F1}s, chunk={cfg.ChunkMaxSeconds}s", "UI");
            };
        }
        _sliderDebounce.Stop();
        _sliderDebounce.Start();
    }

    private void OnSilenceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        App.Current.Config.Audio.SilenceSeconds = Math.Round(e.NewValue, 1);
        ScheduleAudioConfigUpdate();
    }

    private void OnChunkSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        App.Current.Config.Audio.ChunkMaxSeconds = (int)Math.Round(e.NewValue);
        ScheduleAudioConfigUpdate();
    }

    private void OnResetSpeed(object sender, RoutedEventArgs e)
    {
        var cfg = App.Current.Config.Audio;
        cfg.SilenceSeconds = 1.8;
        cfg.ChunkMaxSeconds = 60;
        ApplySilenceDisplay(cfg);
        App.Current.AudioEngine.UpdateConfig(cfg);
        App.Current.Logger.Info("Overlay: reset speed to defaults (silence=1.8s, chunk=60s)", "UI");
    }


    // Заполняет КОНКРЕТНЫЙ пузырь результатом мульти-агента (развязка STT↔LLM).
    public void ShowMultiAgent(QaPair item, List<BrainstormBuddy.Ai.AgentResponse> responses)
    {
        var (answerText, hrdAdvice, stressDefense) = ParseMultiAgent(responses);
        ShowMultiAgent(item, answerText, hrdAdvice, stressDefense);
    }

    // Разбор ответов агентов на блоки [СОВЕТ]/[ЗАЩИТА] (вынесено, чтобы обе перегрузки делили логику).
    private (string answerText, string hrdAdvice, string stressDefense) ParseMultiAgent(
        List<BrainstormBuddy.Ai.AgentResponse> responses)
    {
        var techLead = responses.FirstOrDefault(r => r.AgentId == "tech_lead" && !r.IsSilent);
        var hrd = responses.FirstOrDefault(r => r.AgentId == "hrd" && !r.IsSilent);
        var answerText = techLead?.Text ?? "";
        var hrdAdvice = "";
        var stressDefense = "";

        const string adviceMarker = "[СОВЕТ]";
        const string defenseMarker = "[ЗАЩИТА]";
        if (hrd != null)
        {
            var text = hrd.Text;
            var defenseIdx = text.IndexOf(defenseMarker, StringComparison.Ordinal);
            var adviceIdx = text.IndexOf(adviceMarker, StringComparison.Ordinal);

            if (adviceIdx >= 0)
            {
                if (defenseIdx > adviceIdx)
                    hrdAdvice = text.Substring(adviceIdx + adviceMarker.Length, defenseIdx - adviceIdx - adviceMarker.Length).Trim().TrimStart(':').Trim();
                else
                    hrdAdvice = text.Substring(adviceIdx + adviceMarker.Length).Trim().TrimStart(':').Trim();
            }

            if (defenseIdx >= 0)
                stressDefense = text.Substring(defenseIdx + defenseMarker.Length).Trim().TrimStart(':').Trim();
        }

        if (techLead != null && string.IsNullOrWhiteSpace(stressDefense))
        {
            var defenseIdx = techLead.Text.IndexOf(defenseMarker, StringComparison.Ordinal);
            if (defenseIdx >= 0)
            {
                answerText = techLead.Text.Substring(0, defenseIdx).Trim();
                stressDefense = techLead.Text.Substring(defenseIdx + defenseMarker.Length).Trim().TrimStart(':').Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(stressDefense) &&
            (stressDefense == hrdAdvice || stressDefense == answerText))
        {
            App.Current.Logger.Debug($"HRD duplicated advice into defense block — suppressed (raw: '{hrd?.Text?.Substring(0, Math.Min(120, hrd.Text.Length))}…')", "Agent");
            stressDefense = "";
        }
        return (answerText, hrdAdvice, stressDefense);
    }

    private void ShowMultiAgent(QaPair item, string answerText, string hrdAdvice, string stressDefense)
    {
        Dispatcher.Invoke(() =>
        {
            ApplyAnswerCharLimit(item, answerText);
            item.HrdAdvice = hrdAdvice;
            item.StressDefense = stressDefense;
            item.LlmReceivedAt = DateTime.Now;
            item.Status = QaStatus.Answered;
            item.OnPropertyChanged("LlmReceivedText");
            item.OnPropertyChanged("TotalLatencyText");
            item.OnPropertyChanged("LatencyBrush");
            _llmBlink = History.Any(h => h.Status == QaStatus.Sending);
        });
    }


    // Развязка STT↔LLM: подставляет ответ в КОНКРЕТНЫЙ пузырь (заменяет «ожидание»).
    public void MarkAnswered(QaPair item, string answer, DateTime llmReceivedAt, double llmLatencyMs)
    {
        Dispatcher.Invoke(() =>
        {
            ApplyAnswerCharLimit(item, answer);
            item.LlmReceivedAt = llmReceivedAt;
            item.LlmLatencyMs = llmLatencyMs;
            item.Status = QaStatus.Answered;
            item.OnPropertyChanged("LlmReceivedText");
            item.OnPropertyChanged("TotalLatencyText");
            item.OnPropertyChanged("LatencyBrush");
            _llmBlink = History.Any(h => h.Status == QaStatus.Sending);
        });
    }


    // Стриминг (SSE): дописывает дельту в ответ КОНКРЕТНОГО пузыря — без пересоздания
    // (INPC на QaPair.Answer). BeginInvoke: дельты идут часто с фонового потока,
    // блокировать SSE-ридер синхронным Invoke незачем. Автоскролл — как у Add*Item.
    public void AppendAnswerDelta(QaPair pair, string delta)
    {
        if (pair == null || string.IsNullOrEmpty(delta)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            pair.AppendAnswerDelta(delta);
            if (_autoScroll) ProgrammaticScrollToEnd();
        }));
    }


    public void MarkFailed(QaPair item, string error, DateTime llmReceivedAt, double llmLatencyMs)
    {
        Dispatcher.Invoke(() =>
        {
            item.Answer = string.Format(_localizer("L_Ovl_ErrorFmt"), error);
            item.IsAnswerTruncated = false; // ошибка — не «обрезанный ответ», подсказку не показываем
            item.LlmReceivedAt = llmReceivedAt;
            item.LlmLatencyMs = llmLatencyMs;
            item.Status = QaStatus.Failed;
            item.OnPropertyChanged("LlmReceivedText");
            item.OnPropertyChanged("TotalLatencyText");
            item.OnPropertyChanged("LatencyBrush");
            _llmBlink = History.Any(h => h.Status == QaStatus.Sending);
        });
    }

    public void ClearHistory()
    {
        Dispatcher.Invoke(() =>
        {
            History.Clear();
            // Ссылки на удалённые пузыри в активных запросах держать нельзя — течь.
            foreach (var kv in _pendingRequests) { try { kv.Value.Cts.Dispose(); } catch { } }
            _pendingRequests.Clear();
            _scrollAnchorItem = null;
            _llmBlink = false;
        });
    }

    /// <summary>Отменить запрос (кнопка «Остановить»): Cancel по CTS. Пузырь помечает
    /// worker по исходу (MarkCancelled/MarkFailed) — здесь UI не трогаем.</summary>
    public void CancelRequest(int requestId)
    {
        if (_pendingRequests.TryGetValue(requestId, out var req))
        {
            try { req.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Запрос отменён: пузырь → Cancelled (приглушённый статус), для ручного
    /// вопроса открывается «Повторить». Замещает «ожидание» локализованной меткой.</summary>
    public void MarkCancelled(QaPair item)
    {
        Dispatcher.Invoke(() =>
        {
            item.Answer = T2("L_WaitingCancelled", "Остановлено");
            item.IsAnswerTruncated = false;
            item.Status = QaStatus.Cancelled;
            item.OnPropertyChanged("LlmReceivedText");
            item.OnPropertyChanged("TotalLatencyText");
            item.OnPropertyChanged("LatencyBrush");
            _llmBlink = History.Any(h => h.Status == QaStatus.Sending);
        });
    }

    /// <summary>Сменить текст ожидания у Sending-пузыря (смена фазы: «думаю», «сводка…»).</summary>
    public void SetWaitingText(QaPair item, string text)
    {
        Dispatcher.Invoke(() =>
        {
            if (item.Status == QaStatus.Sending) item.Answer = text;
        });
    }

    /// <summary>«Повторить»: Failed/Cancelled-пузырь возвращается в Sending —
    /// App.RetryAnswer шлёт повторный запрос в ТОТ ЖЕ пузырь.</summary>
    public void ReopenForRetry(QaPair item)
    {
        Dispatcher.Invoke(() =>
        {
            item.Answer = T2("L_WaitingLlm", "Ожидание ответа…");
            item.IsAnswerTruncated = false;
            item.LlmReceivedAt = null;
            item.LlmLatencyMs = 0;
            item.Status = QaStatus.Sending;
            item.OnPropertyChanged("LlmReceivedText");
            item.OnPropertyChanged("TotalLatencyText");
            item.OnPropertyChanged("LatencyBrush");
            _llmBlink = true;
        });
    }

    // Кнопки пузыря: «Остановить» у Sending, «Повторить» у ручного Failed/Cancelled.
    private void OnStopAnswerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is QaPair pair && pair.RequestId != 0)
            App.Current.CancelAnswer(pair.RequestId);
    }

    private void OnRetryAnswerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is QaPair pair)
            App.Current.RetryAnswer(pair);
    }

    /// <summary>Старый API счётчика: только встреча. §8.1 — делегирует в
    /// <see cref="SetTokensDetailed"/> (фоновый счётчик остаётся скрытым).</summary>
    public void SetTokens(int total, int prompt, int completion)
        => SetTokensDetailed(false, total, prompt, completion, 0);

    /// <summary>
    /// §8.1 аудита 15.10: раздельные счётчики футера. «Встреча» — траты
    /// пользователя на ответы; «Документы и память» — фоновый LLM-расход
    /// (извлечение фактов, консолидация памяти, сжатие контекста).
    /// <para><paramref name="usageNa"/> = true: ответы есть, но провайдер не
    /// сообщил usage — показываем «расход недоступен», а НЕ ложный ноль.</para>
    /// </summary>
    public void SetTokensDetailed(bool usageNa, int meetingTokens, int prompt, int completion, int bgTokens)
    {
        Dispatcher.Invoke(() =>
        {
            if (usageNa)
            {
                TokenText.Text = T2("L_Stats_UsageNA", "Расход: провайдер не сообщил данные");
                BgTokensText.Text = "";
                BgTokensText.Visibility = Visibility.Collapsed;
                return;
            }
            TokenText.Text = string.Format(
                T2("L_Stats_MeetingFmt", "Встреча: {0:N0}  (in {1:N0} / out {2:N0})"),
                meetingTokens, prompt, completion);
            if (bgTokens > 0)
            {
                BgTokensText.Text = string.Format(T2("L_Stats_BgFmt", "Документы и память: {0:N0}"), bgTokens);
                BgTokensText.Visibility = Visibility.Visible;
            }
            else
            {
                BgTokensText.Text = "";
                BgTokensText.Visibility = Visibility.Collapsed;
            }
        });
    }

    /// <summary>«Контекст: ≈X/Y» рядом с токен-счётчиком (фоновая сводка lead'а).
    /// summarizing → «Обновляю контекст…»; null/пусто — строка скрыта, layout не дёргается.
    /// <paramref name="showProvidedText"/> — вызывающий уже передал человеческий
    /// прогресс (map-reduce документа, MCP tool-loop): показываем его, а не
    /// общую заглушку «Обновляю контекст…».</summary>
    public void SetContextInfo(string? text, bool summarizing, bool showProvidedText = false)
    {
        Dispatcher.Invoke(() =>
        {
            if (ContextInfoText == null) return;
            if (summarizing)
            {
                ContextInfoText.Text = showProvidedText && !string.IsNullOrEmpty(text)
                    ? text
                    : T2("L_ContextSummarizing", "Обновляю контекст…");
                ContextInfoText.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrEmpty(text))
            {
                // Вызывающая сторона передаёт ГОТОВУЮ строку (уже локализованную
                // и отформатированную) — повторный Format добавлял второй
                // префикс «Контекст: ≈» (баг 2.29.2).
                ContextInfoText.Text = text;
                // Строка могла стартовать скрытой (первый план без сводки):
                // без этого текст записывался в Collapsed-элемент и не был виден.
                ContextInfoText.Visibility = Visibility.Visible;
            }
            else
            {
                ContextInfoText.Visibility = Visibility.Collapsed;
            }
        });
    }

    // Локализация с RU-fallback: ключ может ещё не существовать в Strings (добавит lead),
    // тогда показываем русский дефолт вместо самого ключа.
    private static string T2(string key, string fallback) =>
        System.Windows.Application.Current.TryFindResource(key) as string ?? fallback;

    // ── MCP-аппрувал (спека §491: карточка в оверлее, не модалка) ─────────────
    // TCS живёт, пока tool-loop ждёт решения. Второй запрос поверх нерешённого
    // снимает первый отказом — два одновременных аппрувала в одном окне невозможны.
    private TaskCompletionSource<bool>? _mcpApproval;
    // Пока карточка подтверждения видна, клик-проскок приостановлен: иначе
    // кнопки «Разрешить/Отклонить» недоступны (окно не получает клик).
    private bool _approvalInteractive;

    /// <summary>Показать карточку подтверждения опасного MCP-вызова и дождаться
    /// нажатия «Разрешить»/«Запретить». Потокобезопасно: вызывается с фонового
    /// потока tool-loop. null overlay → вызывающая сторона сама делает fallback.</summary>
    public Task<bool> RequestMcpApproval(string summary, string argsTruncated)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            // Нерешённый предыдущий запрос отклоняем: показать сразу две карточки нельзя.
            if (_mcpApproval is { Task.IsCompleted: false } prev)
                prev.TrySetResult(false);

            McpApprovalText.Text = summary;
            McpApprovalArgs.Text = argsTruncated;
            McpApprovalArgs.Visibility = string.IsNullOrWhiteSpace(argsTruncated)
                ? Visibility.Collapsed : Visibility.Visible;
            McpApprovalCard.Visibility = Visibility.Visible;
            // Карточка кликабельна — снимаем клик-проскок до её закрытия.
            SuspendClickThroughForApproval();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mcpApproval = tcs;
            return tcs.Task;
        }).Task.Unwrap();
    }

    /// <summary>Если карточка открыта — отклонить (окно прячется/закрывается).</summary>
    public void CancelMcpApproval()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mcpApproval is { Task.IsCompleted: false } p)
                p.TrySetResult(false);
            _mcpApproval = null;
            McpApprovalCard.Visibility = Visibility.Collapsed;
            ResumeClickThroughAfterApproval();
        });
    }

    private void OnMcpApproveAllow(object sender, RoutedEventArgs e) => ResolveMcpApproval(true);
    private void OnMcpApproveDeny(object sender, RoutedEventArgs e) => ResolveMcpApproval(false);

    private void ResolveMcpApproval(bool allow)
    {
        var p = _mcpApproval;
        _mcpApproval = null;
        McpApprovalCard.Visibility = Visibility.Collapsed;
        ResumeClickThroughAfterApproval();
        p?.TrySetResult(allow);
        App.Current.Logger.Info($"MCP approval {(allow ? "ALLOWED" : "DENIED")} via overlay card", "Mcp");
    }

    /// <summary>
    /// На время карточки подтверждения окно принимает клики: нативный
    /// click-through снимается, WndProc перестаёт отдавать HTTRANSPARENT.
    /// Пользовательский режим плаща не меняется — он вернётся после карточки.
    /// </summary>
    private void SuspendClickThroughForApproval()
    {
        if (!_clickThroughApplied || _approvalInteractive) return;
        WindowHelper.RemoveClickThrough(this);
        _approvalInteractive = true;
    }

    /// <summary>Карточка закрыта — клик-проскок возвращается (если был включён).</summary>
    private void ResumeClickThroughAfterApproval()
    {
        if (!_approvalInteractive) return;
        _approvalInteractive = false;
        if (_clickThroughApplied) WindowHelper.ApplyClickThrough(this);
    }

    private static readonly SolidColorBrush _brushRecText = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xFF, 0x97, 0xA2))); // REC текст #ff97a2 (макет)
    private static readonly SolidColorBrush _brushLlmText = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xA9, 0xBC, 0xFF))); // LLM текст #a9bcff (макет)

    // Нижняя живая волна: 24 бара, ширина 3, gap 3 (как в макете 6a)
    private System.Windows.Shapes.Rectangle[]? _liveWaveBars;
    private static readonly SolidColorBrush _waveLow = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x4a, 0x55, 0x78)));
    private static readonly SolidColorBrush _waveMid = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x5b, 0x68, 0xa0)));
    private static readonly SolidColorBrush _waveHi = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x6f, 0x8c, 0xff)));
    private static readonly SolidColorBrush _waveTop = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x8f, 0xa8, 0xff)));
    // «Штрихпунктир» паузы: приглушённее самого тихого живого цвета, чтобы читалось «выключено».
    private static readonly SolidColorBrush _waveDash = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x3d, 0x46, 0x60)));
    private readonly Random _waveRnd = new(12345);

    private void EnsureLiveWave()
    {
        if (_liveWaveBars != null) return;
        _liveWaveBars = new System.Windows.Shapes.Rectangle[24];
        for (int i = 0; i < _liveWaveBars.Length; i++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = 3, RadiusX = 2, RadiusY = 2, Height = 4,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = _waveLow,
            };
            _liveWaveBars[i] = bar;
            LiveWave.Children.Add(bar);
        }
    }

    // Статичная огибающая (как «дефолтный» рисунок волны в макете 6a) — множители высоты по барам
    private static readonly double[] _waveEnvelope =
    {
        0.30, 0.55, 0.80, 0.45, 0.65, 0.90, 0.40, 0.70, 0.50, 0.85, 0.35, 0.60,
        0.75, 0.42, 0.68, 0.52, 0.88, 0.38, 0.62, 0.78, 0.48, 0.72, 0.44, 0.58,
    };
    private double _wavePhase;

    private void UpdateLiveWave()
    {
        if (_liveWaveBars == null) return;

        // Захват на паузе (кнопка-микрофон выключена): никакой «дежурной» анимации —
        // статичная штрихпунктирная линия по центру (бары 3px и есть короткие штрихи,
        // высота 2px + приглушённый цвет читаются как «выключено»).
        if (App.Current.IsPaused)
        {
            for (int i = 0; i < _liveWaveBars.Length; i++)
            {
                _liveWaveBars[i].Height = 2;
                _liveWaveBars[i].Fill = _waveDash;
            }
            return;
        }

        float rms = App.Current.GetCurrentRms();
        double level = Math.Min(1.0, rms / 0.05);              // 0..1 живого сигнала
        // Тишина (ниже живого порога VAD): «дыхание» в ~40% высоты, чтобы не читалось как речь.
        // При живом голосе (rms >= порога) — прежняя полная амплитуда, поведение не менялось.
        double quiet = rms < (float)App.Current.GetCurrentEffectiveThreshold() ? 0.4 : 1.0;
        _wavePhase += 1;                                        // лёгкое «дыхание» волны
        for (int i = 0; i < _liveWaveBars.Length; i++)
        {
            double env = _waveEnvelope[i % _waveEnvelope.Length];
            // Базовая форма всегда видна (env*0.55), живой сигнал усиливает (level) + бегущая волна
            double wobble = 0.85 + 0.15 * Math.Sin((i + _wavePhase) * 0.6);
            double hFrac = Math.Min(1.0, env * (0.55 + 0.9 * level) * wobble) * quiet;
            double h = 3 + hFrac * 30;                          // 3..33 px в пилюле высотой 36
            _liveWaveBars[i].Height = h;
            _liveWaveBars[i].Fill = hFrac > 0.72 ? _waveTop : hFrac > 0.5 ? _waveHi : hFrac > 0.3 ? _waveMid : _waveLow;
        }
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        // На границе закрытия тик может проскочить до отработки OnClosed — гасим.
        if (!IsLoaded) return;
        _blinkOn = !_blinkOn;
        try
        {
            var recActive = !App.Current.IsPaused && IsVisible;
            // Чип целиком «живёт»: точка мигает, рамка и подпись держат цвет статуса
            RecIndicator.Fill = recActive ? (_blinkOn ? _brushRed : _brushDarkRed) : _brushGray;
            RecChip.BorderBrush = recActive ? _brushRecText : (Brush)FindResource("BorderBrush");
            RecChipText.Foreground = recActive ? _brushRecText : (Brush)FindResource("TextDimBrush");

            LlmIndicator.Fill = _llmBlink ? (_blinkOn ? _brushBlue : _brushDarkBlue) : _brushGray;
            LlmChip.BorderBrush = _llmBlink ? _brushLlmText : (Brush)FindResource("BorderBrush");
            LlmChipText.Foreground = _llmBlink ? _brushLlmText : (Brush)FindResource("TextDimBrush");

            EnsureLiveWave();
            UpdateLiveWave();
            UpdateSttQueueIndicator();
        }
        catch (Exception ex) { App.Current.Logger.Error("OnBlinkTick failed", ex, "UI"); }
    }

    // Инженерный индикатор у статуса: глубина очереди STT + секунды аудио, накопленные
    // в VAD-буферах и ещё не отправленные. Обновляется тем же таймером, что и блинк (500мс).
    private void UpdateSttQueueIndicator()
    {
        if (SttQueueText == null || SttQueueText.Visibility != Visibility.Visible) return;
        var depth = App.Current.SttQueueDepth;
        var pending = App.Current.PendingAudioSeconds;
        SttQueueText.Text = string.Format(App.Current.T("L_SttQueueFmt"), depth, pending);
    }

    public void ApplyUiConfig(UiConfig ui)
    {
        _uiConfig = ui;
        Opacity = 1.0;
        BgLayer.Opacity = ui.WindowOpacity;
        // Строка источников под ответом: общий тумблер Ui.ShowMeetingSources —
        // применяем к уже отрисованным пузырям сразу, без рестарта. Новые пузыри
        // получают флаг в конструкторе (AddHistoryItem/AddSendingItem).
        foreach (var pair in History) pair.ShowMeetingSources = ui.ShowMeetingSources;
        RestoreSavedGeometry(ui);
        ApplyPosition(ui.WindowPosition);
        _lastNormalWidth = Width;
        _lastNormalHeight = Height;
        UpdateBorder();
        UpdateFontSize();
        UpdateBubblePadding();
        // Футер-статистика «Токены/Контекст/часы» — отключаемая настройкой
        // Ui.ShowSessionStats (2.29.3): выкл = чистая нижняя панель.
        SessionStatsRow.Visibility = ui.ShowSessionStats ? Visibility.Visible : Visibility.Collapsed;
        ApplyEngineerMode();
        // Синк click-through с конфигом: снятие галки «Сквозной режим» в настройках
        // (Save → ApplyLiveConfigChanges → сюда) должно снимать плащ без рестарта,
        // а включённая галка — ставить его при сохранении настроек.
        SetCloak(ui.ClickThrough);
        App.Current.Logger.Info($"Overlay.ApplyUiConfig: opacity={ui.WindowOpacity:F2}, position={ui.WindowPosition}, fontSize={ui.FontSize}, engineer={ui.EngineerMode}", "UI");
    }

    // Инженерный режим: тонкие настройки на главном экране (слайдеры паузы/чанка,
    // пресеты скорости речи, эквалайзер). Выключен → чистый главный экран.
    private void ApplyEngineerMode()
    {
        var vis = _uiConfig.EngineerMode ? Visibility.Visible : Visibility.Collapsed;
        SlidersRow.Visibility = vis;
        SpeedPresetGroup.Visibility = vis;
        EqualizerSection.Visibility = vis;
        SttQueueText.Visibility = vis; // очередь STT у статуса — тоже инженерная метрика
    }

    private void UpdateFontSize()
    {
        // Размер текста вопроса/ответа управляется настройкой Ui.FontSize (10..22).
        // Обновляем App-ресурсы — DynamicResource-биндинги в шаблоне подхватят вживую.
        double baseSize = _uiConfig.FontSize <= 0 ? 15 : _uiConfig.FontSize;
        App.Current.Resources["OverlayAnswerFontSize"] = baseSize;
        // Высота строки ответа: round(F × 1.45) по спецификации волны 01
        App.Current.Resources["OverlayAnswerLineHeight"] = Math.Round(baseSize * 1.45);
        // Вопрос: Inter 14/20 при дефолтном F=15 → сдвиг -1 даёт 14; line-height 20/14 пропорционально
        double questionSize = Math.Max(9, baseSize - 1);
        App.Current.Resources["OverlayQuestionFontSize"] = questionSize;
        App.Current.Resources["OverlayQuestionLineHeight"] = Math.Round(questionSize * 20.0 / 14.0);
        // Тех-строка rec/STT/LLM (справа от «ОТВЕТ») — вкл/выкл по Ui.ShowTimingLine
        App.Current.Resources["TimingLineVisibility"] =
            _uiConfig.ShowTimingLine ? Visibility.Visible : Visibility.Collapsed;
        // Тех-метки STT/OCR/аудио у вопроса (точка-статус + mono-строка) — отдельный
        // флаг Ui.ShowQuestionTimingLine; speaker-чипы им не глушатся.
        App.Current.Resources["QuestionTimingLineVisibility"] =
            _uiConfig.ShowQuestionTimingLine ? Visibility.Visible : Visibility.Collapsed;
    }

    // Внутренние отступы текста в пузырях вопроса/ответа — настройка Ui.BubblePadding (px).
    // Пропорция исходного макета: горизонталь = padding*1.25, вертикаль = padding
    // (при дефолте 12 → 15,12 — близко к бывшим 15,11/16,14, визуально без изменений).
    // App-ресурс + DynamicResource в QaPairTemplate → применяется вживую и при создании баблов.
    private void UpdateBubblePadding()
    {
        double p = _uiConfig.BubblePadding;
        if (p < 0) p = 12;
        App.Current.Resources["OverlayBubblePadding"] = new Thickness(p * 1.25, p, p * 1.25, p);
    }

    /// <summary>
    /// Восстановить Width/Height и (для Custom) Left/Top из сохранённой геометрии.
    /// Старый конфиг без WindowGeometry/RememberWindowGeometry оставляет XAML-размер
    /// и прежний пресет позиции. Невалидные/уехавшие данные отбрасываются helper'ом.
    /// </summary>
    private void RestoreSavedGeometry(UiConfig ui)
    {
        _restoredGeometry = null;
        if (!ui.RememberWindowGeometry) return;
        if (!WindowGeometryHelper.TryGetRestoreRect(
                ui.WindowGeometry, MinWidth, MinHeight, out var rect)) return;
        _restoringGeometry = true;
        try
        {
            Width = rect.Width;
            Height = rect.Height;
        }
        finally
        {
            _restoringGeometry = false;
        }
        _restoredGeometry = rect;
    }

    /// <summary>Ресайз пользователем (не Minimize/Maximize и не восстановление конфига).</summary>
    private void OnGeometrySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_geometryReady || _restoringGeometry || WindowState != WindowState.Normal)
            return;
        // Первый layout-проход до Initialize: PreviousSize пустой — не трогаем конфиг.
        if (e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) return;
        if (!_uiConfig.RememberWindowGeometry) return;
        if (Math.Abs(Width - _lastNormalWidth) < 0.5 &&
            Math.Abs(Height - _lastNormalHeight) < 0.5) return;

        _lastNormalWidth = Width;
        _lastNormalHeight = Height;
        // Новый размер при прежнем пресете уехал бы после рестарта: фиксируем
        // реальную геометрию как Custom.
        _uiConfig.WindowPosition = "Custom";
        App.Current.SettingsVm?.NotifyWindowPositionChanged();
        QueueGeometrySave();
    }

    private void QueueGeometrySave()
    {
        if (!_geometryReady || _restoringGeometry || !_uiConfig.RememberWindowGeometry)
            return;
        if (_geometrySaveTimer == null) return;
        _geometrySaveTimer.Stop();
        _geometrySaveTimer.Start();
    }

    private void OnGeometrySaveTick(object? sender, EventArgs e)
    {
        _geometrySaveTimer?.Stop();
        SaveGeometryNow();
    }

    /// <summary>
    /// Снять текущую валидную геометрию в конфиг и записать его существующим
    /// SaveConfigSafe. Свёрнутое/невалидное окно игнорируется.
    /// </summary>
    private void SaveGeometryNow()
    {
        _geometrySaveTimer?.Stop();
        if (!_geometryReady || !_uiConfig.RememberWindowGeometry) return;
        _uiConfig.WindowGeometry ??= new WindowGeometryConfig();
        if (!WindowGeometryHelper.TryCapture(
                this, _uiConfig.WindowGeometry, MinWidth, MinHeight)) return;
        _uiConfig.WindowPositionX = Left;
        _uiConfig.WindowPositionY = Top;
        _lastNormalWidth = Width;
        _lastNormalHeight = Height;
        App.Current.SaveConfigSafe();
    }

    private void ApplyPosition(string position)
    {
        var workArea = SystemParameters.WorkArea;
        const double margin = 20;
        switch (position)
        {
            case "TopLeft":
                Left = workArea.Left + margin; Top = workArea.Top + margin; break;
            case "TopRight":
                Left = workArea.Right - Width - margin; Top = workArea.Top + margin; break;
            case "BottomLeft":
                Left = workArea.Left + margin; Top = workArea.Bottom - Height - margin; break;
            case "BottomRight":
                Left = workArea.Right - Width - margin; Top = workArea.Bottom - Height - margin; break;
            case "Center":
                Left = (workArea.Width - Width) / 2; Top = (workArea.Height - Height) / 2; break;
            case "Custom":
                // Новая сохранённая геометрия (валидирована и подтянута в экран)
                // приоритетнее старых WindowPositionX/Y; старый конфиг без неё
                // продолжает работать по-прежнему.
                if (_restoredGeometry is { } saved)
                {
                    Left = saved.Left;
                    Top = saved.Top;
                }
                else
                {
                    Left = _uiConfig.WindowPositionX > 0 ? _uiConfig.WindowPositionX : workArea.Right - Width - margin;
                    Top = _uiConfig.WindowPositionY > 0 ? _uiConfig.WindowPositionY : workArea.Top + margin;
                }
                break;
            default:
                Left = workArea.Right - Width - margin; Top = workArea.Top + margin; break;
        }
    }

    private void UpdateBorder()
    {
        if (_uiConfig.ShowCompatibilityBorder)
        {
            RootBorder.BorderBrush = Brushes.Black;
            RootBorder.BorderThickness = new Thickness(1);
        }
        else
        {
            // Макет 6a: окно без рамки (только градиентный фон и скругление 26)
            RootBorder.BorderBrush = Brushes.Transparent;
            RootBorder.BorderThickness = new Thickness(0);
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (WindowState != WindowState.Normal || !_geometryReady || _restoringGeometry)
            return;
        _uiConfig.WindowPositionX = Left;
        _uiConfig.WindowPositionY = Top;
        // DragMove двигает окно десятками событий — пишем конфиг один раз
        // дебаунсом; финальную точку сохраняет OnDragHandleMouseDown.
        if (string.Equals(_uiConfig.WindowPosition, "Custom", StringComparison.OrdinalIgnoreCase))
            QueueGeometrySave();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Последняя валидная геометрия: OnClosing ещё видит IsLoaded=true.
        // Без SaveConfigSafe — App.OnExit запишет конфиг сам; здесь только
        // обновляем поля, чтобы не потерять окно при закрытии сразу после
        // перетаскивания/ресайза (до тика дебаунса).
        _uiConfig.WindowGeometry ??= new WindowGeometryConfig();
        if (_uiConfig.RememberWindowGeometry &&
            WindowGeometryHelper.TryCapture(this, _uiConfig.WindowGeometry, MinWidth, MinHeight))
        {
            _uiConfig.WindowPositionX = Left;
            _uiConfig.WindowPositionY = Top;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Окно при выходе приложения умирает вместе с диспетчером без явного Close
        // из App.OnExit — тики 500мс-таймеров успевали влететь в разрушенное
        // состояние (NRE в OnBlinkTick/OnTimelineTick → FATAL в логе shutdown).
        // Отписываемся ДО base, пока диспетчер ещё принимает отмену.
        _blinkTimer.Stop();
        _blinkTimer.Tick -= OnBlinkTick;
        _timelineTimer.Stop();
        _timelineTimer.Tick -= OnTimelineTick;
        if (_geometrySaveTimer != null)
        {
            _geometrySaveTimer.Stop();
            _geometrySaveTimer.Tick -= OnGeometrySaveTick;
        }
        // Висящий MCP-аппрувал: окно умирает → tool-loop не должен ждать вечно.
        if (_mcpApproval is { Task.IsCompleted: false } p) p.TrySetResult(false);
        _mcpApproval = null;
        base.OnClosed(e);
    }
}
// OverlayHistoryTrim вынесен в OverlayHistoryTrim.cs — чистая логика линкуется в Tests.


public enum QaStatus { Pending, Sending, Answered, Failed, Cancelled }

public class QaPair : INotifyPropertyChanged
{
    private string _question = string.Empty;
    private string _answer = string.Empty;
    private string _expandedAnswer = string.Empty;
    private QaStatus _status = QaStatus.Pending;
    private bool _isFocused;
    private bool _answerStreaming;
    private string? _speakerId;

    // === Тайминги (локальное время юзера, DateTime.Now) ===
    public DateTime? ChunkReadyAt { get; set; }    // когда VAD выдал чанк
    public DateTime? SttSentAt { get; set; }        // когда POST в STT отправлен
    public DateTime? SttReceivedAt { get; set; }    // когда STT ответ пришёл
    public DateTime? LlmSentAt { get; set; }        // когда POST в LLM отправлен
    public DateTime? LlmReceivedAt { get; set; }    // когда LLM ответ пришёл
    public double SttLatencyMs { get; set; }        // STT: от POST до ответа
    public double LlmLatencyMs { get; set; }        // LLM: от POST до ответа
    public double OcrLatencyMs { get; set; }        // OCR: полный проход (захват+препроц+движок)
    public double AudioChunkSeconds { get; set; }   // длина аудио чанка (rec время)
    /// <summary>Идентификатор спикера диаризации ("S1".."S8", "S?" = не определён, null = фича выкл).</summary>
    public string? SpeakerId
    {
        get => _speakerId;
        set
        {
            _speakerId = value;
            OnPropertyChanged();
        }
    }

    public string Question { get => _question; set { _question = value; OnPropertyChanged(); } }
    // Ответ хранится КАК ЕСТЬ, вместе с markdown-разметкой: форматирование применяется
    // только при отрисовке (MarkdownView). Схлопывание пустых строк делает компонент
    // отображения — в обычном тексте оно даёт тот же вид, что и раньше (\n{2,} -> \n),
    // а в разметке пустые строки несут смысл (абзацы, разделители) и теряться не должны.
    public string Answer
    {
        get => _answer;
        set
        {
            _answerStreaming = false;
            _answer = value == null ? string.Empty : value.Replace("\r\n", "\n").Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AnswerSectionVisibility));
        }
    }

    // Стриминг (SSE): дельта дописывается в _answer БЕЗ нормализации сеттера —
    // иначе Trim на каждом тике съедал бы пробелы и переносы на границах дельт.
    // Финальную нормализацию (Trim + \r\n -> \n) делает MarkAnswered (Answer=...).
    // Первая дельта заменяет плейсхолдер «ожидание» (статус ещё Sending).
    public void AppendAnswerDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        if (_answerStreaming || _status != QaStatus.Sending)
            _answer += delta;
        else
            _answer = delta;
        _answerStreaming = true;
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(AnswerSectionVisibility));
    }
    public QaStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(AnswerSectionVisibility));
            OnPropertyChanged(nameof(StopButtonVisibility));
            OnPropertyChanged(nameof(RetryButtonVisibility));
        }
    }

    // Пузырь «чистого транскрибатора» (режим «LLM выкл»): ответа нет и не будет — блок «ОТВЕТ»
    // прячем целиком. Answered + пустой Answer в обычных потоках не встречается (там либо
    // «ожидание», либо текст/ошибка), так что сочетание однозначно маркирует транскрипт.
    public Visibility AnswerSectionVisibility =>
        _status == QaStatus.Answered && string.IsNullOrEmpty(_answer) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Финальный ответ превысил Ui.MaxAnswerChars и обрезан — под баблём
    /// видна строка-ссылка «увеличьте лимит символов» (открывает настройки на вкладке «Оверлей»).</summary>
    public bool IsAnswerTruncated
    {
        get => _isAnswerTruncated;
        set { _isAnswerTruncated = value; OnPropertyChanged(); OnPropertyChanged(nameof(AnswerTruncatedVisibility)); }
    }
    private bool _isAnswerTruncated;
    public Visibility AnswerTruncatedVisibility =>
        _isAnswerTruncated ? Visibility.Visible : Visibility.Collapsed;

    // === Speaker chips (диаризация) ===
    /// <summary>
    /// Все спикеры пузыря в порядке появления: метки «[S1]»…«[S<n>]» из текста вопроса
    /// (внутричанковая разметка) + доминантный <see cref="SpeakerId"/>, если он не вошёл.
    /// </summary>
    public IReadOnlyList<string> SpeakerIds { get; private set; } = Array.Empty<string>();

    /// <summary>Чипы спикеров под вопросом — по одному на каждый id кроме «S?».</summary>
    public IReadOnlyList<SpeakerChipVm> SpeakerChips { get; private set; } = Array.Empty<SpeakerChipVm>();

    /// <summary>Строка чипов видима, только если есть хоть один реально определённый спикер («S?» не считается).</summary>
    public Visibility SpeakerChipsVisibility =>
        SpeakerIds.Count > 0 && SpeakerIds.Any(id => id != "S?") ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Алиас доминантного спикера («S1» → «Иван»); назначается из контекстного меню чипа.</summary>
    public string? SpeakerAlias
    {
        get => _speakerAlias;
        set
        {
            _speakerAlias = value;
            OnPropertyChanged();
            // Доминантный чип этого пузыря следует за алиасом (SetSpeakerAlias обновляет пары через это свойство).
            if (!string.IsNullOrEmpty(SpeakerId))
                ApplySpeakerAlias(SpeakerId, value);
        }
    }
    private string? _speakerAlias;

    // === Привязка к LLM-запросу (гонка _sendingItem убрана: каждый пузырь знает свой запрос) ===
    /// <summary>Id запроса из OverlayWindow.BeginRequest; 0 = запрос не привязан.</summary>
    public int RequestId { get; set; }
    /// <summary>Ручной вопрос (поле ввода) — после Failed/Cancelled доступен «Повторить».</summary>
    public bool IsManual
    {
        get => _isManual;
        set { _isManual = value; OnPropertyChanged(); OnPropertyChanged(nameof(RetryButtonVisibility)); }
    }
    private bool _isManual;
    /// <summary>Исходный текст вопроса для «Повторить» (без меток канала).</summary>
    public string? RequestQuestion { get; set; }

    /// <summary>Кнопка «Остановить» видна только у ожидающего ответа пузыря.</summary>
    public Visibility StopButtonVisibility =>
        _status == QaStatus.Sending ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>«Повторить» — только для ручного вопроса после сбоя/остановки.</summary>
    public Visibility RetryButtonVisibility =>
        IsManual && (_status == QaStatus.Failed || _status == QaStatus.Cancelled)
            ? Visibility.Visible : Visibility.Collapsed;

    // === §7: ссылки-источники по прошлым встречам ===
    /// <summary>Хиты поиска по журналам прошлых встреч, подмешанные в промпт этого
    /// ответа (§7). Назначает App после ответа LLM; строка «Источник: …» под пузырём
    /// открывает MeetingHistoryWindow на встрече с подсветкой строки.</summary>
    public IReadOnlyList<MeetingSearchHit> PastMeetingHits
    {
        get => _pastMeetingHits;
        set
        {
            _pastMeetingHits = value ?? Array.Empty<MeetingSearchHit>();
            // Сеттер PastMeetingSources сам поднимает PastMeetingSources +
            // PastMeetingsVisibility — видимость пересчитывается после каждого назначения.
            PastMeetingSources = _pastMeetingHits
                .GroupBy(h => h.MeetingId)
                .Select(g => g.OrderByDescending(h => h.Score).First())
                .Take(3)
                .ToList();
            OnPropertyChanged();
        }
    }
    private IReadOnlyList<MeetingSearchHit> _pastMeetingHits = Array.Empty<MeetingSearchHit>();

    /// <summary>Топ-хит каждой встречи (≤3) для строки источников.</summary>
    public IReadOnlyList<MeetingSearchHit> PastMeetingSources
    {
        get => _pastMeetingSources;
        private set
        {
            _pastMeetingSources = value ?? Array.Empty<MeetingSearchHit>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PastMeetingsVisibility));
        }
    }
    private IReadOnlyList<MeetingSearchHit> _pastMeetingSources = Array.Empty<MeetingSearchHit>();

    // Глобальный тумблер Ui.ShowMeetingSources: строка источников скрывается и при
    // наличии хитов. Применяется к живым пузырям из OverlayWindow.ApplyUiConfig.
    private bool _showMeetingSources = true;
    public bool ShowMeetingSources
    {
        get => _showMeetingSources;
        set
        {
            if (_showMeetingSources == value) return;
            _showMeetingSources = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PastMeetingsVisibility));
        }
    }

    public Visibility PastMeetingsVisibility =>
        _showMeetingSources && PastMeetingSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // === Картинки вопроса (feature-chat-images, ТЗ §4) ===
    /// <summary>Тайлы изображений, отправленных с вопросом; заполняет App при отправке.</summary>
    public List<ChatImageTileVm> ImageTiles { get; set; } = new();

    /// <summary>Пустой Question не рисует пустую строку в пузыре (вопрос только из картинок).</summary>
    public Visibility QuestionVisibility =>
        string.IsNullOrEmpty(_question) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ImageTilesVisibility =>
        ImageTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>1 картинка — 1 колонка; ≥2 — 2 колонки (ячейки ≤156×120 заданы в шаблоне).</summary>
    public int ImageGridColumns => ImageTiles.Count > 1 ? 2 : 1;

    /// <summary>Применить алиас ко всем чипам пузыря с этим id (доминантному и вторичным).</summary>
    public void ApplySpeakerAlias(string speakerId, string? alias)
    {
        foreach (var chip in SpeakerChips)
            if (chip.Id == speakerId)
                chip.Alias = alias;
    }

    // Детерминированная палитра: один спикер — один цвет в течение всей сессии
    // (S-номера сбрасываются ResetConversation'ом вместе с кластером).
    private static readonly Brush[] SpeakerPalette =
    {
        new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)), // S1 blue
        new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)), // S2 pink
        new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), // S3 green
        new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), // S4 amber
        new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)), // S5 violet
        new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)), // S6 red
        new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), // S7 cyan
        new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C)), // S8 orange
    };
    private static readonly Brush SpeakerBrushFallback = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    /// <summary>Цвет чипа по id спикера: «S1»→палитра[0] и т.д., нераспознанный id → серый.</summary>
    public static Brush SpeakerBrushFor(string? id)
    {
        if (!string.IsNullOrEmpty(id) && id[0] == 'S'
            && int.TryParse(id[1..], out var n) && n >= 1)
            return SpeakerPalette[(n - 1) % SpeakerPalette.Length];
        return SpeakerBrushFallback;
    }

    private static readonly System.Text.RegularExpressions.Regex SpeakerTagRe = new(@"\[S(\d+)\]");

    /// <summary>
    /// Заполняет SpeakerIds/SpeakerChips: уникальные метки «[S<n>]» из question в порядке
    /// появления + доминантный id, если задан и не вошёл. «[Микрофон]»/«[Динамик]»/«[screen]»/
    /// «[музыка]» не матчатся (только S+цифры); «S?» чипом не становится.
    /// </summary>
    public void SetSpeakers(string? question, string? dominantSpeakerId, Func<string, string?>? aliasResolver = null)
    {
        List<string>? ids = null;
        if (!string.IsNullOrEmpty(question))
        {
            foreach (System.Text.RegularExpressions.Match m in SpeakerTagRe.Matches(question))
            {
                var id = "S" + m.Groups[1].Value;
                ids ??= new List<string>();
                if (!ids.Contains(id)) ids.Add(id);
            }
        }
        if (!string.IsNullOrEmpty(dominantSpeakerId)
            && (ids == null || !ids.Contains(dominantSpeakerId)))
        {
            (ids ??= new List<string>()).Add(dominantSpeakerId);
        }
        SpeakerIds = ids ?? (IReadOnlyList<string>)Array.Empty<string>();

        var chips = new List<SpeakerChipVm>(SpeakerIds.Count);
        foreach (var id in SpeakerIds)
        {
            if (id == "S?") continue; // «спикер не определён» — чип не показываем
            chips.Add(new SpeakerChipVm(id, aliasResolver?.Invoke(id)));
        }
        SpeakerChips = chips;
    }
    public Brush StatusBrush => _status switch
    {
        QaStatus.Sending => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        QaStatus.Answered => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
        QaStatus.Failed => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B)), // Pending + Cancelled — приглушённый
    };

    // === Computed для UI ===

    public string ChunkReadyText => ChunkReadyAt.HasValue ? $"rec {ChunkReadyAt.Value:HH:mm:ss.fff}" : "";
    public string SttSentText => SttSentAt.HasValue ? $"→STT {SttSentAt.Value:HH:mm:ss.fff}" : "";
    public string SttReceivedText => SttReceivedAt.HasValue ? $"←STT {SttReceivedAt.Value:HH:mm:ss.fff}" : "";
    public string LlmSentText => LlmSentAt.HasValue ? $"→LLM {LlmSentAt.Value:HH:mm:ss.fff}" : "";
    public string LlmReceivedText => LlmReceivedAt.HasValue ? $"←LLM {LlmReceivedAt.Value:HH:mm:ss.fff}" : "";

    public string AudioDurationText => AudioChunkSeconds > 0 ? $"aud {AudioChunkSeconds:F1}s" : "";

    public string SttLatencyText => SttLatencyMs > 0 ? $"STT {SttLatencyMs/1000:F1}s" : "";

    public string OcrLatencyText => OcrLatencyMs > 0 ? $"OCR {OcrLatencyMs/1000:F1}s" : "";

    // Manual and OCR questions have no STT timestamp; start at the LLM request.
    private double? TotalLatencyMs =>
        LlmReceivedAt is { } end && (SttReceivedAt ?? LlmSentAt) is { } start
            ? Math.Max(0, (end - start).TotalMilliseconds)
            : null;

    public string TotalLatencyText => TotalLatencyMs is { } total ? $"Σ {total/1000:F1}s" : "";

    public Brush LatencyBrush
    {
        get
        {
            if (TotalLatencyMs is not { } total) return Brushes.Gray;
            if (total < 1500) return FastLatencyBrush;
            if (total < 3500) return MediumLatencyBrush;
            return SlowLatencyBrush;
        }
    }

    private static readonly Brush FastLatencyBrush = CreateLatencyBrush(0x4A, 0xDE, 0x80);
    private static readonly Brush MediumLatencyBrush = CreateLatencyBrush(0xFB, 0xBF, 0x24);
    private static readonly Brush SlowLatencyBrush = CreateLatencyBrush(0xEF, 0x44, 0x44);

    private static Brush CreateLatencyBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    // === ExpandAnswer (gold block) ===
    public string ExpandedAnswer
    {
        get => _expandedAnswer;
        set { _expandedAnswer = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasExpandedAnswer)); OnPropertyChanged(nameof(ExpandedVisibility)); }
    }
    public bool HasExpandedAnswer => !string.IsNullOrWhiteSpace(_expandedAnswer);
    public Visibility ExpandedVisibility => HasExpandedAnswer ? Visibility.Visible : Visibility.Collapsed;

    // === Multi-agent: HRD совет ===
    private string _hrdAdvice = string.Empty;
    public string HrdAdvice
    {
        get => _hrdAdvice;
        set { _hrdAdvice = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasHrdAdvice)); OnPropertyChanged(nameof(HrdAdviceVisibility)); }
    }
    public bool HasHrdAdvice => !string.IsNullOrWhiteSpace(_hrdAdvice);
    public Visibility HrdAdviceVisibility => HasHrdAdvice ? Visibility.Visible : Visibility.Collapsed;

    // === Multi-agent: Stress-Defense ===
    private string _stressDefense = string.Empty;
    public string StressDefense
    {
        get => _stressDefense;
        set { _stressDefense = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStressDefense)); OnPropertyChanged(nameof(StressDefenseVisibility)); }
    }
    public bool HasStressDefense => !string.IsNullOrWhiteSpace(_stressDefense);
    public Visibility StressDefenseVisibility => HasStressDefense ? Visibility.Visible : Visibility.Collapsed;

    // === Focus border (green frame) ===
    public bool IsFocused
    {
        get => _isFocused;
        set { _isFocused = value; OnPropertyChanged(); OnPropertyChanged(nameof(FocusBorderVisibility)); }
    }
    public Visibility FocusBorderVisibility => _isFocused ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}

/// <summary>
/// Чип спикера под пузырём вопроса (внутричанковая диаризация): Id («S1»),
/// Text — алиас ?? id, Brush — детерминированный цвет из палитры QaPair.
/// </summary>
public class SpeakerChipVm : INotifyPropertyChanged
{
    public SpeakerChipVm(string id, string? alias)
    {
        Id = id;
        _alias = alias;
        Brush = QaPair.SpeakerBrushFor(id);
    }

    public string Id { get; }
    public Brush Brush { get; }

    /// <summary>Ручной алиас спикера; при смене обновляет Text чипа.</summary>
    public string? Alias
    {
        get => _alias;
        set { _alias = value; OnPropertyChanged(); OnPropertyChanged(nameof(Text)); }
    }
    private string? _alias;

    public string Text => _alias ?? Id;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}

/// <summary>
/// Чип прикреплённого документа в строке над полем ввода: имя файла, текст статуса
/// («В обработке» / «Готов» / «Не удалось прочитать» / «Факты: …»), ид документа
/// для удаления из встречи. Наполняет App.RefreshAttachmentChips.
/// </summary>
public sealed class AttachmentChipVm
{
    public required string DocumentId { get; init; }
    public required string DisplayName { get; init; }
    public string StatusText { get; init; } = "";
    public string ToolTip { get; init; } = "";
    /// <summary>Импорт ещё идёт и связи с встречей нет — крестик на таком чипе скрыт.</summary>
    public bool Pending { get; init; }
}
