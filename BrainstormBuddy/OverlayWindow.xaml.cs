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
using Key = System.Windows.Input.Key;

namespace BrainstormBuddy;

public enum TimelineState { Idle, Recording, Sending, Received, ChunkSent }

public partial class OverlayWindow : Window
{
    private bool _clickThroughApplied;
    private bool _excludeFromCaptureApplied;
    private bool _screenCaptureVisible;
    private UiConfig _uiConfig = new();
    private bool _initialized;
    private DispatcherTimer _blinkTimer;
    private bool _blinkOn;
    private bool _recBlink;
    private bool _llmBlink;
    private QaPair? _sendingItem;

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
    private static readonly SolidColorBrush _brushRulerTick = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
    private static readonly SolidColorBrush _brushRulerLabel = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));
    private static readonly System.Windows.Media.FontFamily _rulerFont = new("Cascadia Mono, Consolas");

    private static SolidColorBrush FreezeBrush(SolidColorBrush b) { b.Freeze(); return b; }

    public ObservableCollection<QaPair> History { get; } = new();

    public OverlayWindow()
    {
        InitializeComponent();
        HistoryList.ItemsSource = History;
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
    }

    // Фиолетовая отсечка «чанк ушёл в STT» (инженерная фича): MainLoop дёргает PulseChunkMarker
    // с фонового потока, ближайший тик таймлайна рисует один столбик на всю высоту. Удобно на
    // длинной речи: видно, ушло ли уже что-то в распознавание.
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
                        FontSize = 9,
                        FontFamily = _rulerFont,
                    };
                    Canvas.SetTop(label, 3);
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

        for (int i = 0; i < 12; i++)
        {
            if (_rulerTicks[i] == null || _rulerLabels[i] == null) continue;
            int sec = (i + 1) * 10;
            double x = w - (sec / totalSec) * w;
            bool visible = x >= 0 && x <= w;
            _rulerTicks[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _rulerLabels[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_rulerTicks[i], x);
            Canvas.SetLeft(_rulerLabels[i], x - 16);
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
        PresetSelector.ItemsSource = presets;
        PresetSelector.DisplayMemberPath = "Name";
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
        ApplyPosition(ui.WindowPosition);
        UpdateBorder();
        UpdateFontSize();
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
                    _excludeFromCaptureApplied = true;
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

        // В сквозном режиме окно полностью прозрачно для кликов
        if (_clickThroughApplied)
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
                el is System.Windows.Controls.Primitives.Thumb)
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
        if (Left != _uiConfig.WindowPositionX || Top != _uiConfig.WindowPositionY)
        {
            _uiConfig.WindowPositionX = Left;
            _uiConfig.WindowPositionY = Top;
            App.Current.Logger.Info($"Overlay dragged to ({Left:F0},{Top:F0})", "UI");
        }
    }

    private void OnAudioPauseClick(object sender, RoutedEventArgs e)
    {
        App.Current.TogglePause();
        RefreshAudioPauseButton();
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

    public void ToggleCloak()
    {
        if (_clickThroughApplied)
        {
            WindowHelper.RemoveClickThrough(this);
            _clickThroughApplied = false;
            CloakButton.Content = FindResource("IconCloak");
            CloakButton.Background = Brushes.Transparent;
            CloakButton.Opacity = 1.0;
            CloakButton.ToolTip = "Сквозной режим: выключен (клики обрабатываются)";
            App.Current.Logger.Info("Overlay: cloak OFF (interactive)", "UI");
        }
        else
        {
            WindowHelper.ApplyClickThrough(this);
            _clickThroughApplied = true;
            CloakButton.Content = FindResource("IconCloak");
            CloakButton.SetResourceReference(BackgroundProperty, "HoverBrush");
            CloakButton.Opacity = 0.6;
            CloakButton.ToolTip = "Сквозной режим: включен (клики проходят насквозь)";
            App.Current.Logger.Info("Overlay: cloak ON (click-through)", "UI");
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
            ScreenShotButton.Resources["IconBrush"] = FindResource("WarnBrush"); // жёлтый = режим скриншота активен
            ScreenShotButton.ToolTip = "Окно видно в захвате экрана — щёлкните чтобы скрыть (Ctrl+Shift+C)";
            App.Current.Logger.Info("Overlay: screen capture VISIBLE (for screenshots)", "UI");
        }
        else
        {
            WindowHelper.ApplyExcludeFromCapture(this);
            if (_clickThroughApplied) WindowHelper.ApplyClickThrough(this);
            ScreenShotButton.Opacity = 1.0;
            ScreenShotButton.Resources.Remove("IconBrush"); // вернуть штатный цвет иконки
            ScreenShotButton.ToolTip = "Режим скриншота: показать окно для захвата (Ctrl+Shift+C)";
            App.Current.Logger.Info("Overlay: screen capture HIDDEN", "UI");
        }
    }

    private void OnAutoScrollClick(object sender, RoutedEventArgs e)
    {
        _autoScroll = !_autoScroll;
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
            AutoScrollButton.Resources["IconBrush"] = FindResource("WarnBrush");
            AutoScrollButton.ToolTip = "Автоскролл: ВКЛ (щёлкните чтобы выключить)";
        }
        else
        {
            AutoScrollButton.Resources.Remove("IconBrush");
            AutoScrollButton.ToolTip = "Автоскролл: ВЫКЛ (щёлкните чтобы включить)";
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
        if (string.IsNullOrWhiteSpace(text)) return;
        ChatInputBox.Text = "";
        await App.Current.SendSecretMessage(text);
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
            ExportIconHost.Content = FindResource("IconExportDone");
            await Task.Delay(1500);
            ExportIconHost.Content = FindResource("IconExport");
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
            sb.AppendLine(p.Question).AppendLine("---").AppendLine(p.Answer).AppendLine();
        CopyToClipboard(sb.ToString().TrimEnd());
    }

    // Полная история сессии (экран держит ~30 пар, старые уезжают — тут ВСЁ с запуска).
    private void OnCopyFullHistoryClick(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        lock (_fullHistory)
        {
            foreach (var p in _fullHistory)
                sb.AppendLine(p.Question).AppendLine("---").AppendLine(p.Answer).AppendLine();
        }
        CopyToClipboard(sb.ToString().TrimEnd());
    }

    // Пары за ВСЮ сессию (ссылки на те же QaPair — ответы дозаполняются сами). Не тримится.
    private readonly List<QaPair> _fullHistory = new();

    private void OnCopyQuestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QaPair pair)
            CopyToClipboard(pair.Question);
    }

    private void OnCopyAnswerClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is QaPair pair)
            CopyToClipboard(pair.Answer);
    }

    private static void CopyToClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (Exception ex) { App.Current?.Logger?.Error("CopyToClipboard failed", ex, "UI"); }
    }

    // Пауза аудио живёт на микрофоне в нижней панели: жёлтая иконка = на паузе.
    public void RefreshAudioPauseButton()
    {
        try
        {
            if (MicPauseButton == null) return;
            if (App.Current.IsPaused)
            {
                MicPauseButton.Resources["IconBrush"] = FindResource("WarnBrush"); // микрофон жёлтый = аудио на паузе
                MicPauseButton.ToolTip = "Аудио на паузе — щёлкните, чтобы возобновить (Ctrl+Shift+P)";
            }
            else
            {
                MicPauseButton.Resources.Remove("IconBrush"); // вернуть штатный цвет иконки
                MicPauseButton.ToolTip = "Поставить аудио на паузу (Ctrl+Shift+P)";
            }
            UpdateCombinedStatus();
        }
        catch (Exception ex) { App.Current.Logger.Error("RefreshAudioPauseButton failed", ex, "UI"); }
    }

    // LLM-пауза больше не имеет иконки в шапке (убрана по фидбеку). Функция доступна по
    // хоткею Ctrl+Shift+Y; состояние отражается в чипе LLM и общем статусе.
    public void RefreshLlmPauseButton()
    {
        try { UpdateCombinedStatus(); }
        catch (Exception ex) { App.Current.Logger.Error("RefreshLlmPauseButton failed", ex, "UI"); }
    }

    // «LLM выкл» (чистый транскрибатор): активное состояние — золотая иконка, как у микрофона.
    public void RefreshLlmOffButton()
    {
        try
        {
            if (LlmOffButton == null) return;
            if (App.Current.Config.Ui.LlmDisabled)
            {
                LlmOffButton.Resources["IconBrush"] = FindResource("WarnBrush"); // золотой = LLM выключен
                LlmOffButton.ToolTip = "LLM выключен — чистый транскрибатор (щёлкните, чтобы включить)";
            }
            else
            {
                LlmOffButton.Resources.Remove("IconBrush"); // вернуть штатный цвет иконки
                LlmOffButton.ToolTip = FindResource("L_TT_LlmOff");
            }
        }
        catch (Exception ex) { App.Current.Logger.Error("RefreshLlmOffButton failed", ex, "UI"); }
    }

    // «Динамик выкл» (мьют loopback): активное состояние — та же золотая подсветка.
    public void RefreshSpeakerMuteButton()
    {
        try
        {
            if (SpeakerMuteButton == null) return;
            if (App.Current.Config.Ui.LoopbackMuted)
            {
                SpeakerMuteButton.Resources["IconBrush"] = FindResource("WarnBrush"); // золотой = динамик заглушен
                SpeakerMuteButton.ToolTip = "Динамик выключен — звук системы не распознаётся (щёлкните, чтобы включить)";
            }
            else
            {
                SpeakerMuteButton.Resources.Remove("IconBrush");
                SpeakerMuteButton.ToolTip = FindResource("L_TT_SpeakerMute");
            }
        }
        catch (Exception ex) { App.Current.Logger.Error("RefreshSpeakerMuteButton failed", ex, "UI"); }
    }

    private void UpdateCombinedStatus()
    {
        if (App.Current.IsPaused) { StatusText.Text = App.Current.T("L_St_AudioPaused"); return; }
        if (App.Current.IsLlmPaused) { StatusText.Text = App.Current.T("L_St_LlmPaused"); return; }
        StatusText.Text = App.Current.T("L_Listening");
    }

    public void SetStatus(string text)
    {
        App.Current.Logger.Debug($"Overlay.Status = '{text}'", "UI");
        Dispatcher.Invoke(() =>
        {
            if (App.Current.IsPaused) return;
            if (App.Current.IsLlmPaused) return;
            StatusText.Text = text;
        });
    }

    // Кратко подсветить статус (напр. «✓ сохранено …») зелёным и вернуть «Слушаю…».
    private int _flashToken;
    public void FlashStatus(string text, int seconds = 4)
    {
        Dispatcher.Invoke(async () =>
        {
            var myToken = ++_flashToken;
            StatusText.Text = text;
            StatusText.SetResourceReference(ForegroundProperty, "SuccessBrush");
            await Task.Delay(seconds * 1000);
            if (myToken != _flashToken) return; // перезаписано новым flash — не трогаем
            StatusText.SetResourceReference(ForegroundProperty, "TextMutedBrush");
            StatusText.Text = (string)FindResource("L_Listening");
        });
    }

    public QaPair AddHistoryItem(string question, string answer)
    {
        QaPair? item = null;
        Dispatcher.Invoke(() =>
        {
            item = new QaPair { Question = question, Answer = answer, Status = QaStatus.Answered };
            History.Add(item);
            lock (_fullHistory) _fullHistory.Add(item);
            while (History.Count > 30) History.RemoveAt(0);
            if (_autoScroll) HistoryScroll.ScrollToEnd();
        });
        return item!;
    }

    // transcriptOnly («LLM выкл»): пузырь чистого транскрибатора — без строки «ожидание»,
    // сразу Answered с пустым ответом (блок «ОТВЕТ» прячет AnswerSectionVisibility),
    // LLM-блинк не зажигаем — отправки не будет.
    public QaPair AddSendingItem(string question, double audioSeconds,
        DateTime chunkReadyAt, DateTime sttSentAt, DateTime sttReceivedAt, double sttLatencyMs,
        bool transcriptOnly = false)
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
            };
            History.Add(item);
            lock (_fullHistory) _fullHistory.Add(item);
            while (History.Count > 30) History.RemoveAt(0);
            if (!transcriptOnly)
            {
                _sendingItem = item;
                _llmBlink = true;
            }
            if (_autoScroll) HistoryScroll.ScrollToEnd();
        });
        return item!;
    }

    public void MarkLlmSent(DateTime llmSentAt)
    {
        if (_sendingItem != null) MarkLlmSent(_sendingItem, llmSentAt);
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

    public void ShowMultiAgent(List<BrainstormBuddy.Ai.AgentResponse> responses)
    {
        var target = _sendingItem;
        if (target == null) return;
        _sendingItem = null;
        ShowMultiAgent(target, responses);
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
            item.Answer = answerText;
            item.HrdAdvice = hrdAdvice;
            item.StressDefense = stressDefense;
            item.LlmReceivedAt = DateTime.Now;
            item.Status = QaStatus.Answered;
            item.OnPropertyChanged("LlmReceivedText");
            item.OnPropertyChanged("LlmLatencyText");
            item.OnPropertyChanged("TotalLatencyText");
            item.OnPropertyChanged("LatencyBrush");
            _llmBlink = History.Any(h => h.Status == QaStatus.Sending);
        });
    }

    public void MarkAnswered(string answer, DateTime llmReceivedAt, double llmLatencyMs)
    {
        var item = _sendingItem;
        if (item == null) return;
        _sendingItem = null;
        MarkAnswered(item, answer, llmReceivedAt, llmLatencyMs);
    }

    // Развязка STT↔LLM: подставляет ответ в КОНКРЕТНЫЙ пузырь (заменяет «ожидание»).
    public void MarkAnswered(QaPair item, string answer, DateTime llmReceivedAt, double llmLatencyMs)
    {
        Dispatcher.Invoke(() =>
        {
            item.Answer = answer;
            item.LlmReceivedAt = llmReceivedAt;
            item.LlmLatencyMs = llmLatencyMs;
            item.Status = QaStatus.Answered;
            item.OnPropertyChanged("LlmReceivedText");
            item.OnPropertyChanged("LlmLatencyText");
            item.OnPropertyChanged("TotalLatencyText");
            item.OnPropertyChanged("LatencyBrush");
            _llmBlink = History.Any(h => h.Status == QaStatus.Sending);
        });
    }

    public void MarkFailed(string error, DateTime llmReceivedAt, double llmLatencyMs)
    {
        var item = _sendingItem;
        if (item == null) return;
        _sendingItem = null;
        MarkFailed(item, error, llmReceivedAt, llmLatencyMs);
    }

    public void MarkFailed(QaPair item, string error, DateTime llmReceivedAt, double llmLatencyMs)
    {
        Dispatcher.Invoke(() =>
        {
            item.Answer = $"[ошибка] {error}";
            item.LlmReceivedAt = llmReceivedAt;
            item.LlmLatencyMs = llmLatencyMs;
            item.Status = QaStatus.Failed;
            item.OnPropertyChanged("LlmReceivedText");
            item.OnPropertyChanged("LlmLatencyText");
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
            _sendingItem = null;
            _llmBlink = false;
        });
    }

    public void SetTokens(int total, int prompt, int completion)
    {
        Dispatcher.Invoke(() =>
        {
            TokenText.Text = string.Format(App.Current.T("L_TokensFmt"), total, prompt, completion);
        });
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
        ApplyPosition(ui.WindowPosition);
        UpdateBorder();
        UpdateFontSize();
        ApplyEngineerMode();
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
        App.Current.Resources["OverlayQuestionFontSize"] = Math.Max(9, baseSize - 1.5);
        // Тех-строка rec/STT/LLM (справа от «ОТВЕТ») — вкл/выкл по Ui.ShowTimingLine
        App.Current.Resources["TimingLineVisibility"] =
            _uiConfig.ShowTimingLine ? Visibility.Visible : Visibility.Collapsed;
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
                Left = _uiConfig.WindowPositionX > 0 ? _uiConfig.WindowPositionX : workArea.Right - Width - margin;
                Top = _uiConfig.WindowPositionY > 0 ? _uiConfig.WindowPositionY : workArea.Top + margin;
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
        if (WindowState == WindowState.Normal)
        {
            _uiConfig.WindowPositionX = Left;
            _uiConfig.WindowPositionY = Top;
        }
    }
}

public enum QaStatus { Pending, Sending, Answered, Failed }

public class QaPair : INotifyPropertyChanged
{
    private string _question = string.Empty;
    private string _answer = string.Empty;
    private string _expandedAnswer = string.Empty;
    private QaStatus _status = QaStatus.Pending;
    private bool _isFocused;

    // === Тайминги (локальное время юзера, DateTime.Now) ===
    public DateTime? ChunkReadyAt { get; set; }    // когда VAD выдал чанк
    public DateTime? SttSentAt { get; set; }        // когда POST в STT отправлен
    public DateTime? SttReceivedAt { get; set; }    // когда STT ответ пришёл
    public DateTime? LlmSentAt { get; set; }        // когда POST в LLM отправлен
    public DateTime? LlmReceivedAt { get; set; }    // когда LLM ответ пришёл
    public double SttLatencyMs { get; set; }        // STT: от POST до ответа
    public double LlmLatencyMs { get; set; }        // LLM: от POST до ответа
    public double AudioChunkSeconds { get; set; }   // длина аудио чанка (rec время)

    public string Question { get => _question; set { _question = value; OnPropertyChanged(); } }
    // Ответ нормализуем: схлопываем 2+ пустых строк в один перенос — иначе модель шлёт
    // \n\n между абзацами и в оверлее зияют большие вертикальные разрывы.
    public string Answer
    {
        get => _answer;
        set
        {
            _answer = value == null ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(value.Replace("\r\n", "\n"), "\n{2,}", "\n").Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AnswerSectionVisibility));
        }
    }
    public QaStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusBrush)); OnPropertyChanged(nameof(AnswerSectionVisibility)); }
    }

    // Пузырь «чистого транскрибатора» (режим «LLM выкл»): ответа нет и не будет — блок «ОТВЕТ»
    // прячем целиком. Answered + пустой Answer в обычных потоках не встречается (там либо
    // «ожидание», либо текст/ошибка), так что сочетание однозначно маркирует транскрипт.
    public Visibility AnswerSectionVisibility =>
        _status == QaStatus.Answered && string.IsNullOrEmpty(_answer) ? Visibility.Collapsed : Visibility.Visible;

    public Brush StatusBrush => _status switch
    {
        QaStatus.Sending => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        QaStatus.Answered => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
        QaStatus.Failed => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B)),
    };

    // === Computed для UI ===

    public string ChunkReadyText => ChunkReadyAt.HasValue ? $"rec {ChunkReadyAt.Value:HH:mm:ss.fff}" : "";
    public string SttSentText => SttSentAt.HasValue ? $"→STT {SttSentAt.Value:HH:mm:ss.fff}" : "";
    public string SttReceivedText => SttReceivedAt.HasValue ? $"←STT {SttReceivedAt.Value:HH:mm:ss.fff}" : "";
    public string LlmSentText => LlmSentAt.HasValue ? $"→LLM {LlmSentAt.Value:HH:mm:ss.fff}" : "";
    public string LlmReceivedText => LlmReceivedAt.HasValue ? $"←LLM {LlmReceivedAt.Value:HH:mm:ss.fff}" : "";

    public string AudioDurationText => AudioChunkSeconds > 0 ? $"aud {AudioChunkSeconds:F1}s" : "";

    public string SttLatencyText => SttLatencyMs > 0 ? $"STT {SttLatencyMs/1000:F1}s" : "";

    public string LlmLatencyText => LlmLatencyMs > 0 ? $"LLM {LlmLatencyMs/1000:F1}s" : "";

    public string TotalLatencyText
    {
        get
        {
            if (SttReceivedAt.HasValue && LlmReceivedAt.HasValue)
            {
                var total = (LlmReceivedAt.Value - SttReceivedAt.Value).TotalMilliseconds;
                if (total < 0) total = 0;
                return $"Σ {total/1000:F1}s";
            }
            return "";
        }
    }

    public Brush LatencyBrush
    {
        get
        {
            if (!LlmReceivedAt.HasValue || !SttReceivedAt.HasValue) return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            var total = (LlmReceivedAt.Value - SttReceivedAt.Value).TotalMilliseconds;
            if (total < 0) total = 0;
            if (total < 1500) return new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); // green
            if (total < 3500) return new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)); // yellow
            return new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));                    // red
        }
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
