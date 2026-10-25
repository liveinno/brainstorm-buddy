// Просмотрщик изображений чата (ТЗ §4): немодальное окно, зум к курсору,
// пан левой кнопкой, навигация стрелками внутри набора одного источника.
// WDA_EXCLUDEFROMCAPTURE — как у оверлея (скриншоты/запись звонка его не видят).

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BrainstormBuddy.Native;

namespace BrainstormBuddy;

public partial class ImageViewerWindow : Window
{
    private IReadOnlyList<ChatImageTileVm> _tiles = Array.Empty<ChatImageTileVm>();
    private int _index;
    private ChatImageFlow? _flow;
    private readonly IInputElement? _returnFocus;

    private bool _panning;
    private System.Windows.Point _panStart;      // позиция мыши во вьюпорте на старте пана
    private System.Windows.Point _panOrigin;     // translate на старте пана
    private bool _fitMode = true;  // Fit-режим по умолчанию: ресайз окна пересчитывает вписывание
    private int _loadSeq;         // гонки подгрузки оригинала: последний вызов выигрывает

    private ImageViewerWindow(Window owner, IReadOnlyList<ChatImageTileVm> tiles,
        int index, ChatImageFlow? flow)
    {
        InitializeComponent();
        Owner = owner;
        _tiles = tiles;
        _index = Math.Clamp(index, 0, Math.Max(0, tiles.Count - 1));
        _flow = flow;
        _returnFocus = owner.IsActive ? Keyboard.FocusedElement : null;
    }

    /// <summary>Статическая фабрика (ТЗ §4): одно окно на owner — повторный клик
    /// переключает набор/индекс вместо накопления окон.</summary>
    public static void Show(Window? owner, IReadOnlyList<ChatImageTileVm> tiles,
        int index, ChatImageFlow? flow)
    {
        (System.Windows.Application.Current as App)?.Logger.Info("ChatImg", $"Viewer Show: tiles={tiles.Count}, index={index}, flow={(flow == null ? "null" : "ok")}");
        if (tiles.Count == 0) return;
        var existing = owner?.OwnedWindows.OfType<ImageViewerWindow>().FirstOrDefault()
            ?? System.Windows.Application.Current.Windows.OfType<ImageViewerWindow>().FirstOrDefault();
        ImageViewerWindow w;
        if (existing != null)
        {
            w = existing;
            w._tiles = tiles;
            w._index = Math.Clamp(index, 0, tiles.Count - 1);
            w._flow = flow;
        }
        else
        {
            w = new ImageViewerWindow(owner ?? System.Windows.Application.Current.MainWindow!, tiles, index, flow);
            w.Show();
        }
        w.ShowCurrent();
        w.Activate();
        w.Focus();
        (System.Windows.Application.Current as App)?.Logger.Info("ChatImg", $"Viewer shown: hwnd={new System.Windows.Interop.WindowInteropHelper(w).Handle}, visible={w.IsVisible}, opacity={w.Opacity}");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Тестовый хук: BUDDY_CAPTURE_VISIBLE=1 оставляет окно видимым для скриншотов (как оверлей).
        // В проде переменной нет → exclude-from-capture как у оверлея.
        if (Environment.GetEnvironmentVariable("BUDDY_CAPTURE_VISIBLE") != "1")
            WindowHelper.ApplyExcludeFromCapture(this); // та же невидимость, что у оверлея
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAtOwner();
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        // Viewport получил реальный размер только после layout — вписываем ещё раз.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_fitMode) Fit();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Размер min(1040×760, 90% WorkArea монитора owner'а), центровка по owner;
    /// MaxWidth/MaxHeight зажимают окно в рабочую область при ресайзе.</summary>
    private void PositionAtOwner()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(Owner ?? this).Handle;
            var wa = hwnd == IntPtr.Zero
                ? SystemParameters.WorkArea
                : ScreenToDip(System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea, this);
            MaxWidth = wa.Width;
            MaxHeight = wa.Height;
            Width = Math.Min(1040, wa.Width * 0.9);
            Height = Math.Min(760, wa.Height * 0.9);
            if (Owner != null && Owner.IsVisible)
            {
                Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
                Top = Owner.Top + (Owner.ActualHeight - Height) / 2;
            }
            else
            {
                Left = wa.Left + (wa.Width - Width) / 2;
                Top = wa.Top + (wa.Height - Height) / 2;
            }
            Left = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
            Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        }
        catch
        {
            Width = 1040; Height = 760;
            MaxWidth = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private static Rect ScreenToDip(System.Drawing.Rectangle px, Visual v)
    {
        var dpi = VisualTreeHelper.GetDpi(v);
        return new Rect(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY,
            px.Width / dpi.DpiScaleX, px.Height / dpi.DpiScaleY);
    }

    // ── Показ текущего тайла ────────────────────────────────────────────────

    private void ShowCurrent()
    {
        if (_tiles.Count == 0) { Close(); return; }
        var tile = _tiles[_index];
        FileNameText.Text = tile.FileName;
        FileNameText.ToolTip = tile.FileName;
        CounterText.Text = _tiles.Count > 1
            ? string.Format(App.TT("L_ImgOfFmt", "{0} of {1}"), _index + 1, _tiles.Count)
            : "";
        // Сразу показываем то, что есть (превью ≤640), оригинал догружаем фоном.
        Picture.Source = tile.Thumb;
        Picture.Visibility = tile.Thumb != null ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = tile.Thumb == null ? Visibility.Visible : Visibility.Collapsed;
        Fit();
        _ = LoadOriginalAsync(tile, ++_loadSeq);
    }

    /// <summary>Оригинал по требованию; файл отсутствует → превью + плашка «недоступен».</summary>
    private async Task LoadOriginalAsync(ChatImageTileVm tile, int seq)
    {
        try
        {
            var bytes = _flow != null ? await _flow.LoadOriginalBytesAsync(tile.ImageId) : null;
            if (seq != _loadSeq) return; // пользователь уже перелистнул
            if (bytes is { Length: > 0 })
            {
                var decoded = ChatImageCodec.TryDecode(bytes, tile.FileName);
                if (decoded != null)
                {
                    Picture.Source = decoded.Bitmap;
                    Picture.Visibility = Visibility.Visible;
                    UnavailablePanel.Visibility = Visibility.Collapsed;
                    Fit();
                    return;
                }
            }
            if (Picture.Source == null)
            {
                Picture.Visibility = Visibility.Collapsed;
                UnavailablePanel.Visibility = Visibility.Visible;
            }
        }
        catch { /* превью уже на экране — тихая деградация */ }
    }

    // ── Зум / пан ───────────────────────────────────────────────────────────

    private double CurrentScale => ScaleT.ScaleX;

    /// <summary>Range 10%–400%; Fit может быть <10% для длинных (ТЗ §4).</summary>
    private void SetZoom(double scale, System.Windows.Point? atViewportPoint = null)
    {
        _fitMode = false; // ручной зум выходит из режима «вписать»
        scale = Math.Clamp(scale, 0.10, 4.0);
        if (Picture.Source == null) return;
        var p = atViewportPoint
            ?? new System.Windows.Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);
        // Зум к точке: img = (p - t)/s, t' = p - img*s'.
        var imgX = (p.X - TransT.X) / CurrentScale;
        var imgY = (p.Y - TransT.Y) / CurrentScale;
        ScaleT.ScaleX = ScaleT.ScaleY = scale;
        TransT.X = p.X - imgX * scale;
        TransT.Y = p.Y - imgY * scale;
        UpdateZoomText();
    }

    /// <summary>Вписать в вьюпорт (учитывает Stretch=Uniform: масштаб относительно
    /// отображаемого размера — задаём через DesiredSize-картинку в DIP).</summary>
    private void Fit()
    {
        _fitMode = true;
        if (Picture.Source is not BitmapSource src || Viewport.ActualWidth <= 0) return;
        var iw = src.PixelWidth / src.DpiX * 96.0;
        var ih = src.PixelHeight / src.DpiY * 96.0;
        if (iw <= 0 || ih <= 0) return;
        // Fit может опуститься ниже 10% (длинные скриншоты) — без нижнего зажима.
        var scale = Math.Min(Viewport.ActualWidth / iw, Viewport.ActualHeight / ih);
        ScaleT.ScaleX = ScaleT.ScaleY = Math.Min(scale, 4.0);
        TransT.X = (Viewport.ActualWidth - iw * ScaleT.ScaleX) / 2;
        TransT.Y = (Viewport.ActualHeight - ih * ScaleT.ScaleY) / 2;
        UpdateZoomText();
    }

    /// <summary>100% = пиксель-в-пиксель с учётом DpiScale текущего монитора.</summary>
    private void OneToOne()
    {
        if (Picture.Source is not BitmapSource src) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Исходный bitmap отображается 1:1 с физическими пикселями экрана:
        // масштаб = DpiX исходника / 96 / DpiScale монитора… но проще:
        // Image в DIP уже даёт src.DpiX→96 пересчёт; 1:1 экран = 1/dpiScale DIP-пикселя.
        var scale = 1.0 / dpi.DpiScaleX;
        SetZoom(scale);
    }

    private void UpdateZoomText()
    {
        ZoomText.Text = $"{CurrentScale * 100:0}%";
        ZoomOutBtn.IsEnabled = CurrentScale > 0.099;
        ZoomInBtn.IsEnabled = CurrentScale < 4.0;
    }

    private void OnViewportMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (Picture.Source == null) return;
        var factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        SetZoom(CurrentScale * factor, e.GetPosition(Viewport));
    }

    private void OnViewportMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Fit(); return; }
        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _panOrigin = new System.Windows.Point(TransT.X, TransT.Y);
        Viewport.CaptureMouse();
    }

    private void OnViewportMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(Viewport);
        TransT.X = _panOrigin.X + (p.X - _panStart.X);
        TransT.Y = _panOrigin.Y + (p.Y - _panStart.Y);
    }

    private void OnViewportMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _panning = false;
        Viewport.ReleaseMouseCapture();
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Fit();
    private void OnOneToOneClick(object sender, RoutedEventArgs e) => OneToOne();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(CurrentScale / 1.2);
    private void OnZoomInClick(object sender, RoutedEventArgs e) => SetZoom(CurrentScale * 1.2);
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Перетаскивание окна за верхнюю панель.</summary>
    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            try { DragMove(); } catch { /* окно в процессе закрытия и т.п. */ }
    }

    /// <summary>В Fit-режиме ресайз вьюпорта пересчитывает вписывание.</summary>
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode && Picture.Source != null) Fit();
    }

    /// <summary>Стрелки — только внутри набора одного сообщения, на краях стоп (ТЗ §4).</summary>
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.Left && _index > 0) { _index--; ShowCurrent(); e.Handled = true; }
        else if (e.Key == Key.Right && _index < _tiles.Count - 1) { _index++; ShowCurrent(); e.Handled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Фокус — туда, откуда открыли (поле ввода оверлея и т.п.).
        if (_returnFocus != null) Keyboard.Focus(_returnFocus);
    }
}
