using System.Runtime.InteropServices;

namespace BrainstormBuddy.Core.Ocr;

/// <summary>Монитор в физических пикселях виртуального десктопа.</summary>
public sealed record MonitorBounds(int Index, PixelRect Bounds, bool IsPrimary, string DeviceName);

/// <summary>
/// Перечисление мониторов через Win32.
/// </summary>
/// <remarks>
/// <para>
/// Граница между Win32 и доменом: здесь живут <c>HMONITOR</c> и <c>MONITORINFOEXW</c>,
/// наружу отдаются готовые числа в физических пикселях.
/// </para>
/// <para>
/// [WARN] Значения корректны только потому, что приложение объявлено <b>Per-Monitor DPI v2</b>
/// в манифесте. У DPI-unaware процесса Windows виртуализирует координаты, и <c>rcMonitor</c>
/// приходит уменьшенным на масштаб — тогда кадр снимается не с той области.
/// </para>
/// <para>
/// [WARN] Отрицательные координаты штатны: монитор слева от главного даёт <c>X &lt; 0</c>.
/// </para>
/// </remarks>
public static class MonitorEnumerator
{
    private const int MonitorInfoPrimary = 0x1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>
    /// Список мониторов. Перечисление не удалось (сеанс без экрана, отказ API) —
    /// отдаётся один монитор по метрикам виртуального экрана, чтобы вызывающий получил
    /// рабочую геометрию, а не исключение.
    /// </summary>
    public static IReadOnlyList<MonitorBounds> Enumerate()
    {
        var monitors = TryEnumerate();
        if (monitors.Count > 0)
        {
            return monitors;
        }

        return new[] { new MonitorBounds(0, VirtualScreenBounds(), IsPrimary: true, string.Empty) };
    }

    /// <summary>Границы виртуального десктопа по системным метрикам — без перечисления.</summary>
    public static PixelRect VirtualScreenBounds()
    {
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);

        if (width <= 0 || height <= 0)
        {
            // Экрана нет вовсе (служба, сеанс 0) — отдаём заведомо валидный минимум 1×1,
            // иначе PixelBuffer откажется выделяться и падение уедет далеко от причины.
            return new PixelRect(0, 0, 1, 1);
        }

        return new PixelRect(GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen), width, height);
    }

    /// <summary>
    /// Область захвата для индекса монитора из конфига.
    /// <c>-1</c> — основной монитор (или первый, если основной не определился);
    /// индекс вне списка обрезается до первого.
    /// </summary>
    public static PixelRect CaptureBoundsFor(int monitorIndex)
    {
        var monitors = Enumerate();

        if (monitorIndex < 0)
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            return primary.Bounds;
        }

        return monitors[monitorIndex < monitors.Count ? monitorIndex : 0].Bounds;
    }

    private static List<MonitorBounds> TryEnumerate()
    {
        var result = new List<MonitorBounds>();
        var index = 0;

        // Делегат держим в локальной переменной: маршалер не продлевает ему жизнь,
        // и сборщик мусора вправе собрать его прямо посреди перечисления.
        // Типы параметров указаны явно: у лямбды с ref-параметром неявная типизация запрещена.
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            if (TryDescribe(hMonitor, index, out var info))
            {
                result.Add(info);
                index++;
            }

            return true;
        };

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return result;
        }
        catch (EntryPointNotFoundException)
        {
            return result;
        }
        finally
        {
            GC.KeepAlive(callback);
        }

        return result;
    }

    private static bool TryDescribe(IntPtr hMonitor, int index, out MonitorBounds info)
    {
        var native = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };

        if (!GetMonitorInfoW(hMonitor, ref native))
        {
            info = default!;
            return false;
        }

        var bounds = ToPixelRect(native.rcMonitor);
        if (bounds.IsEmpty)
        {
            info = default!;
            return false;
        }

        info = new MonitorBounds(index, bounds, (native.dwFlags & MonitorInfoPrimary) != 0, native.szDevice ?? string.Empty);
        return true;
    }

    private static PixelRect ToPixelRect(Rect r) => PixelRect.FromBounds(r.Left, r.Top, r.Right, r.Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect lprcMonitor, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
