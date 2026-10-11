using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrainstormBuddy.Native;

namespace BrainstormBuddy;

/// <summary>Состояния шара-индикатора экранного OCR.</summary>
public enum OcrIndicatorState
{
    /// <summary>Фича включена, работы нет — зелёный.</summary>
    Idle,
    /// <summary>Идёт распознавание экрана — синий, пульс.</summary>
    Scanning,
    /// <summary>Вопрос найден, идёт отправка в LLM — фиолетовый, пульс.</summary>
    Sending,
    /// <summary>Ошибка распознавания/отправки — красный (возврат в Idle по таймеру в App).</summary>
    Error,
}

/// <summary>
/// Полупрозрачный шар состояния экранного OCR в правом нижнем углу primary-монитора
/// (24px от правого края, 96px от низа — над таскбаром/часами). Паттерн окна — как у
/// KaraokeWindow/OverlayWindow: Topmost, без таскбара, прозрачный фон, клики проходят
/// сквозь (WS_EX_TRANSPARENT | TOOLWINDOW | NOACTIVATE + HTTRANSPARENT) и окно скрыто
/// от захвата экрана (WDA_EXCLUDEFROMCAPTURE).
/// Цвет состояния — замороженные кисти; пульс — один переиспользуемый Storyboard
/// на Opacity 0.9↔0.5 (0.8 с, AutoReverse) для Scanning/Sending, без аллокаций в тике.
/// </summary>
public partial class OcrStatusIndicatorWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    // Отступы от краёв primary-монитора (DIP): над часами таскбара.
    private const double RightMargin = 24;
    private const double BottomMargin = 96;

    // Замороженные кисти состояний: создаются один раз, без аллокаций на смену цвета.
    private static readonly SolidColorBrush IdleBrush = Frozen(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly SolidColorBrush ScanningBrush = Frozen(Color.FromRgb(0x42, 0x85, 0xF4));
    private static readonly SolidColorBrush SendingBrush = Frozen(Color.FromRgb(0x8A, 0x5C, 0xF6));
    private static readonly SolidColorBrush ErrorBrush = Frozen(Color.FromRgb(0xF0, 0x4D, 0x4D));

    private readonly Storyboard _pulse;
    private OcrIndicatorState _state;

    public OcrStatusIndicatorWindow()
    {
        InitializeComponent();
        Dot.Fill = IdleBrush;

        var anim = new DoubleAnimation(0.9, 0.5, new Duration(TimeSpan.FromMilliseconds(800)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(anim, Dot);
        Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
        _pulse = new Storyboard();
        _pulse.Children.Add(anim);
    }

    /// <summary>
    /// Текущее состояние. SetState маршаллится на UI-поток — события приходят
    /// с фоновых потоков (наблюдатель, Task.Run разового прохода).
    /// </summary>
    public OcrIndicatorState State => _state;

    public void SetState(OcrIndicatorState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetState(state));
            return;
        }
        if (_state == state) return;
        _state = state;

        Dot.Fill = state switch
        {
            OcrIndicatorState.Scanning => ScanningBrush,
            OcrIndicatorState.Sending => SendingBrush,
            OcrIndicatorState.Error => ErrorBrush,
            _ => IdleBrush,
        };

        // Пульс только в «рабочих» состояниях; на Idle/Error — ровный свет.
        // Stop возвращает Opacity к базовому 1.0 (Storyboard снимает HoldEnd).
        if (state is OcrIndicatorState.Scanning or OcrIndicatorState.Sending)
            _pulse.Begin(this, isControllable: true);
        else
            _pulse.Stop(this);
    }

    /// <summary>Показать шар. Позиция пересчитывается при каждом Show (мог смениться монитор/DPI).</summary>
    public new void Show()
    {
        PositionOnScreen();
        base.Show();
    }

    public new void Hide()
    {
        base.Hide();
        // Сброс в Idle: следующий показ не начнёт с устаревшего состояния.
        SetState(OcrIndicatorState.Idle);
    }

    private void PositionOnScreen()
    {
        // Якоримся к физическому низу primary-монитора, а не к WorkArea: шар должен
        // висеть НАД таскбаром (фиксированные 96px от низа), а не уезжать на его высоту.
        Left = SystemParameters.PrimaryScreenWidth - Width - RightMargin;
        Top = SystemParameters.PrimaryScreenHeight - Height - BottomMargin;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Хит-тест глушим на уровне сообщений — надёжнее одного WS_EX_TRANSPARENT.
        WindowHelper.InstallHwndSourceHook(this, WndProc);
        ApplyNativeModes();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            handled = true;
            return (IntPtr)HTTRANSPARENT;
        }
        return IntPtr.Zero;
    }

    private void ApplyNativeModes()
    {
        var hwnd = WindowHelper.GetHandle(this);
        if (hwnd == IntPtr.Zero) return;

        // Click-through + toolwindow + never-activate: шар чисто визуальный.
        WindowHelper.ApplyClickThrough(this);
        var exStyle = WindowHelper.GetWindowLong(hwnd, WindowHelper.GWL_EXSTYLE);
        WindowHelper.SetWindowLong(hwnd, WindowHelper.GWL_EXSTYLE,
            exStyle | WindowHelper.WS_EX_TOOLWINDOW | WindowHelper.WS_EX_NOACTIVATE);

        // Тестовый хук: BUDDY_CAPTURE_VISIBLE=1 оставляет окно видимым для скриншотов.
        if (Environment.GetEnvironmentVariable("BUDDY_CAPTURE_VISIBLE") != "1")
            WindowHelper.ApplyExcludeFromCapture(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pulse.Stop(this); // иначе бегущая анимация держит окно живым
        base.OnClosed(e);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
