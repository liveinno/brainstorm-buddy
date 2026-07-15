using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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
