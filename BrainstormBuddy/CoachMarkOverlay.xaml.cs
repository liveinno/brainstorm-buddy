using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;
using TabControl = System.Windows.Controls.TabControl;

namespace BrainstormBuddy;

/// <summary>Один шаг интерактивного обучения: что подсветить и что сказать.</summary>
public sealed class CoachStep
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    /// <summary>Реальный контрол, который надо подсветить (вычисляется после Prepare).</summary>
    public Func<FrameworkElement?> Target { get; init; } = () => null;
    /// <summary>Подготовка перед подсветкой: выбрать вкладку, проскроллить и т.п.</summary>
    public Action? Prepare { get; init; }
}

/// <summary>
/// Интерактивное обучение: полупрозрачный слой поверх окна, который РАМКОЙ подсвечивает
/// реальные кнопки/поля и показывает выноску с пояснением. НИКАКОГО затемнения экрана
/// (фон почти прозрачный) — только рамка + карточка. Слой «приклеен» к целевому окну.
/// </summary>
public partial class CoachMarkOverlay : Window
{
    private Window? _followed;
    private IList<CoachStep> _steps = Array.Empty<CoachStep>();
    private int _idx;

    /// <summary>Тур закончен. Аргумент: true = пройден до конца («Готово»), false = пропущен/прерван.</summary>
    public event EventHandler<bool>? Completed;

    public CoachMarkOverlay()
    {
        InitializeComponent();
        KeyDown += (s, e) => { if (e.Key == Key.Escape) Finish(false); };
        Loaded += (s, e) => Focus();
    }

    /// <summary>Запуск тура поверх окна <paramref name="followed"/>.</summary>
    public void Start(Window followed, IList<CoachStep> steps)
    {
        _followed = followed;
        _steps = steps;
        _idx = 0;
        Owner = followed;
        // Owned-окно и так рисуется над владельцем. Собственный Topmost="True" был багом:
        // настройки уходят под чужие окна (клик в проводник), а пузырь оставался поверх ВСЕГО.
        Topmost = followed.Topmost;
        Reposition();
        followed.LocationChanged += OnFollowedChanged;
        followed.SizeChanged += OnFollowedChanged;
        followed.StateChanged += OnFollowedChanged;
        followed.IsVisibleChanged += OnFollowedVisibleChanged;
        followed.Closed += OnFollowedClosed;
        Show();
        GoTo(0);
    }

    private void OnFollowedClosed(object? sender, EventArgs e) => Finish(false);

    // Hide() владельца (хоткей/трей прячут оверлей) не даёт ни StateChanged, ни Closed,
    // и Win32 НЕ скрывает owned-окна — без этого пузырь оставался сиротой поверх всего.
    private void OnFollowedVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_followed == null || _finished) return;
        if (!_followed.IsVisible) Hide();
        else { Show(); Reposition(); MeasureCurrent(); }
    }

    // Заголовок окна не накрываем — чтобы окно можно было таскать и сворачивать.
    private const double TitleBarInset = 34;

    private void OnFollowedChanged(object? sender, EventArgs e)
    {
        if (_followed?.WindowState == WindowState.Minimized)
        {
            Hide();          // свернули настройки → тур сворачивается вместе
            return;
        }
        if (!IsVisible) Show();
        Reposition();
        MeasureCurrent();
    }

    // Слой накрывает целевое окно НИЖЕ заголовка (та же система координат WPF — без DPI-магии).
    private void Reposition()
    {
        if (_followed == null) return;
        if (_followed.WindowState == WindowState.Maximized)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left; Top = wa.Top + TitleBarInset; Width = wa.Width; Height = Math.Max(0, wa.Height - TitleBarInset);
        }
        else if (!double.IsNaN(_followed.Left) && !double.IsNaN(_followed.Top))
        {
            Left = _followed.Left; Top = _followed.Top + TitleBarInset;
            Width = _followed.ActualWidth > 0 ? _followed.ActualWidth : _followed.Width;
            Height = Math.Max(0, (_followed.ActualHeight > 0 ? _followed.ActualHeight : _followed.Height) - TitleBarInset);
        }
    }

    private void GoTo(int index)
    {
        if (index < 0) index = 0;
        if (index >= _steps.Count) { Finish(true); return; }
        _idx = index;
        var step = _steps[_idx];
        try { step.Prepare?.Invoke(); } catch { /* подготовка best-effort */ }

        // дать раскладке устояться после смены вкладки/скролла, затем измерить
        var el = SafeTarget(step);
        if (el != null) EnsureVisible(el);
        MeasureCurrent();
        Dispatcher.BeginInvoke(new Action(MeasureCurrent), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MeasureCurrent()
    {
        if (_idx < 0 || _idx >= _steps.Count) return;
        var step = _steps[_idx];
        var el = SafeTarget(step);

        TitleText.Text = step.Title;
        BodyText.Text = step.Body;
        StepText.Text = $"{_idx + 1} / {_steps.Count}";
        BackBtn.Visibility = _idx > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Content = _idx == _steps.Count - 1 ? "Готово" : "Далее";
        Callout.Visibility = Visibility.Visible;

        Rect? r = GetElementRect(el);
        if (el != null && r is { Width: > 0, Height: > 0 })
        {
            try
            {
                var sTL = el.PointToScreen(new Point(0, 0));
                App.Current.Logger?.Debug(
                    $"Coach[{_idx}] '{step.Title}' targetScreen=({sTL.X:F0},{sTL.Y:F0}) size={el.ActualWidth:F0}x{el.ActualHeight:F0}", "Coach");
            }
            catch { }
        }
        if (r is { Width: > 0, Height: > 0 } rect)
        {
            const double pad = 6;
            var hx = rect.X - pad; var hy = rect.Y - pad;
            Highlight.Width = rect.Width + 2 * pad;
            Highlight.Height = rect.Height + 2 * pad;
            Canvas.SetLeft(Highlight, hx);
            Canvas.SetTop(Highlight, hy);
            Highlight.Visibility = Visibility.Visible;
            PlaceCallout(rect);
        }
        else
        {
            // цель не видна (например, скрыт GPU-пикер) — без рамки, карточка по центру
            Highlight.Visibility = Visibility.Collapsed;
            Callout.UpdateLayout();
            Canvas.SetLeft(Callout, Math.Max(10, (ActualWidth - Callout.ActualWidth) / 2));
            Canvas.SetTop(Callout, Math.Max(10, (ActualHeight - Callout.ActualHeight) / 2));
        }
    }

    // Размещает выноску рядом с целью: снизу → сверху → с клампом в границах слоя.
    private void PlaceCallout(Rect target)
    {
        Callout.UpdateLayout();
        double cw = Callout.ActualWidth, ch = Callout.ActualHeight;
        double cx = target.X;
        double cy = target.Bottom + 12;
        if (cy + ch > ActualHeight - 8) cy = target.Y - ch - 12;   // не влезло снизу → сверху
        if (cy < 8) cy = 8;
        if (cx + cw > ActualWidth - 8) cx = ActualWidth - cw - 8;
        if (cx < 8) cx = 8;
        Canvas.SetLeft(Callout, cx);
        Canvas.SetTop(Callout, cy);
    }

    private FrameworkElement? SafeTarget(CoachStep step)
    {
        try { return step.Target(); } catch { return null; }
    }

    // Экранный прямоугольник контрола → координаты слоя (учитывает DPI через Point*Screen).
    private Rect? GetElementRect(FrameworkElement? el)
    {
        if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0) return null;
        try
        {
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            var tlo = PointFromScreen(tl);
            var bro = PointFromScreen(br);
            return new Rect(tlo, bro);
        }
        catch { return null; }
    }

    // Выбирает вкладку, содержащую контрол, и подматывает его в область видимости.
    // ВАЖНО: идём по ЛОГИЧЕСКОМУ дереву — контент невыбранной вкладки TabControl
    // отсутствует в визуальном дереве, пока вкладку не выбрали.
    private void EnsureVisible(FrameworkElement el)
    {
        try
        {
            DependencyObject? d = el;
            while (d != null && d is not TabItem)
                d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d);

            if (d is TabItem ti)
            {
                var tc = ItemsControl.ItemsControlFromItemContainer(ti) as TabControl;
                if (tc == null)
                {
                    DependencyObject? p = LogicalTreeHelper.GetParent(ti);
                    while (p != null && p is not TabControl) p = LogicalTreeHelper.GetParent(p);
                    tc = p as TabControl;
                }
                if (tc != null) tc.SelectedItem = ti;
            }
            _followed?.UpdateLayout();
            el.BringIntoView();
            _followed?.UpdateLayout();
        }
        catch { /* best-effort */ }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_idx >= _steps.Count - 1) Finish(true);
        else GoTo(_idx + 1);
    }

    private void OnBack(object sender, RoutedEventArgs e) => GoTo(_idx - 1);
    // «Пропустить» ≠ «прошёл до конца»: раньше оба слали одинаковый Completed,
    // и после «Пропустить» немедленно стартовал ВТОРОЙ тур (по оверлею).
    private void OnSkip(object sender, RoutedEventArgs e) => Finish(false);

    private bool _finished;
    private void Finish(bool finishedAll)
    {
        if (_finished) return;
        _finished = true;
        if (_followed != null)
        {
            _followed.LocationChanged -= OnFollowedChanged;
            _followed.SizeChanged -= OnFollowedChanged;
            _followed.StateChanged -= OnFollowedChanged;
            _followed.IsVisibleChanged -= OnFollowedVisibleChanged;
            _followed.Closed -= OnFollowedClosed;
        }
        Completed?.Invoke(this, finishedAll);
        try { Close(); } catch { /* уже закрыто */ }
    }
}
