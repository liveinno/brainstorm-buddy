using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Native;

public static class WindowHelper
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x8000000;

    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;

    public static IntPtr GetHandle(Window window)
    {
        var helper = new WindowInteropHelper(window);
        return helper.Handle;
    }

    public static void ApplyClickThrough(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    public static void RemoveClickThrough(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);
    }

    public static void ApplyExcludeFromCapture(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;

        // WDA_EXCLUDEFROMCAPTURE supported since Windows 10 2004 (build 19041)
        if (Environment.OSVersion.Version.Build >= 19041)
        {
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        }
        else
        {
            SetWindowDisplayAffinity(hwnd, WDA_NONE);
        }
    }

    public static void RemoveExcludeFromCapture(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;
        SetWindowDisplayAffinity(hwnd, WDA_NONE);
    }

    public static void SetTopmost(Window window, bool topmost = true)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;
        var insertAfter = topmost ? (IntPtr)(-1) : (IntPtr)(-2);
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;
        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static IntPtr InstallHwndSourceHook(Window window, HwndSourceHook hook)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(hook);
        return hwnd;
    }
}

/// <summary>
/// Валидация и восстановление сохранённой геометрии окон (DIP).
/// Сохраняем/применяем только конечные значения в нормальном состоянии;
/// NaN/Infinity, свёрнутое окно и полностью уехавшая за экран геометрия
/// отбрасываются. Частично видимое окно подтягивается в границы виртуального
/// рабочего стола, чтобы его нельзя было «потерять» после смены мониторов.
/// </summary>
public static class WindowGeometryHelper
{
    /// <summary>Минимальная видимая часть окна, чтобы считать позицию не потерянной.</summary>
    private const double MinVisibleWidth = 96;
    private const double MinVisibleHeight = 40;

    private static Rect VirtualScreen => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// Проверить сохранённую геометрию и вернуть прямоугольник для восстановления.
    /// false — данных нет/они невалидны: окно открывается по прежним правилам.
    /// </summary>
    public static bool TryGetRestoreRect(
        WindowGeometryConfig? saved, double minWidth, double minHeight, out Rect rect)
        => TryGetRestoreRect(saved, minWidth, minHeight,
            double.PositiveInfinity, double.PositiveInfinity, out rect);

    /// <summary>Вариант с верхними границами окна (Window.MaxWidth/MaxHeight).</summary>
    public static bool TryGetRestoreRect(
        WindowGeometryConfig? saved, double minWidth, double minHeight,
        double maxWidth, double maxHeight, out Rect rect)
    {
        rect = Rect.Empty;
        if (saved is null || !saved.IsComplete) return false;
        double left = saved.Left!.Value, top = saved.Top!.Value;
        double width = saved.Width!.Value, height = saved.Height!.Value;
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height)) return false;
        if (width < minWidth || height < minHeight) return false;

        var screen = VirtualScreen;
        if (screen.IsEmpty || screen.Width <= 0 || screen.Height <= 0) return false;

        // Окно больше виртуального экрана/MaxWidth — ужимаем (минимумы XAML всё равно сильнее).
        double limitWidth = double.IsFinite(maxWidth) && maxWidth > 0
            ? Math.Min(screen.Width, maxWidth) : screen.Width;
        double limitHeight = double.IsFinite(maxHeight) && maxHeight > 0
            ? Math.Min(screen.Height, maxHeight) : screen.Height;
        width = Math.Min(width, Math.Max(minWidth, limitWidth));
        height = Math.Min(height, Math.Max(minHeight, limitHeight));

        // Полностью/почти полностью за экраном — восстановление невозможно.
        var visible = Rect.Intersect(new Rect(left, top, width, height), screen);
        if (visible.IsEmpty || visible.Width < MinVisibleWidth || visible.Height < MinVisibleHeight)
            return false;

        // Частично видимое окно подтягиваем целиком в границы виртуального экрана.
        left = Math.Clamp(left, screen.Left, Math.Max(screen.Left, screen.Right - width));
        top = Math.Clamp(top, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        rect = new Rect(left, top, width, height);
        return true;
    }

    /// <summary>
    /// Снять текущую геометрию в конфиг. false — окно свёрнуто/не загружено/
    /// значения невалидны; в этом случае конфиг не трогаем.
    /// </summary>
    public static bool TryCapture(Window window, WindowGeometryConfig target, double minWidth, double minHeight)
    {
        if (window.WindowState != WindowState.Normal) return false;
        if (!window.IsLoaded) return false;
        double left = window.Left, top = window.Top;
        double width = window.Width, height = window.Height;
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height)) return false;
        if (width < minWidth || height < minHeight) return false;

        var screen = VirtualScreen;
        if (screen.IsEmpty || screen.Width <= 0 || screen.Height <= 0) return false;
        var visible = Rect.Intersect(new Rect(left, top, width, height), screen);
        if (visible.IsEmpty || visible.Width < MinVisibleWidth || visible.Height < MinVisibleHeight)
            return false;

        target.Set(left, top, width, height);
        return true;
    }
}
