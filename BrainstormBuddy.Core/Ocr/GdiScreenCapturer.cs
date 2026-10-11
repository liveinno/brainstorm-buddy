using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Снимок экрана через GDI <c>BitBlt</c>.
/// </summary>
/// <remarks>
/// <para>
/// DC всего экрана (<c>GetDC(NULL)</c>) покрывает ВЕСЬ виртуальный десктоп, а его система
/// координат совпадает с координатами мониторов. Поэтому исходная точка <c>BitBlt</c>
/// берётся прямо из <see cref="PixelRect.Left"/>/<see cref="PixelRect.Top"/> и может быть
/// отрицательной — монитор слева от главного это штатно.
/// </para>
/// <para>
/// <b>Флаг <c>CAPTUREBLT</c> обязателен:</b> без него в кадр не попадают слоистые окна
/// (подсказки, тосты, часть современных оверлеев) — на скриншоте их просто нет, и это
/// выглядит как «пропал текст», а не как флаг захвата.
/// </para>
/// <para>
/// ⚠️ <b>Хендлы освобождаются детерминированно.</b> Приложение живёт в трее сутками и снимает
/// экран каждые пару секунд: утечка одного DC или битмапа на снимок за день выливается
/// в тысячи хендлов и валит систему. Все освобождения — в <c>finally</c>, финализаторов нет.
/// </para>
/// <para>
/// ⛔ Кадр никуда не пишется: ни временного файла, ни PNG «для отладки». Только массив
/// в памяти, который получает вызывающий.
/// </para>
/// <para>
/// Известное ограничение: защищённое DRM-видео в захват не попадает — на его
/// месте будет чёрный прямоугольник. Это поведение Windows, а не дефект.
/// </para>
/// </remarks>
public sealed class GdiScreenCapturer : IScreenCapturer
{
    /// <summary>Имя реализации в журнале.</summary>
    public const string ImplementationName = "GDI BitBlt";

    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const int BiRgb = 0;
    private const int DibRgbColors = 0;

    /// <inheritdoc />
    public string Name => ImplementationName;

    /// <inheritdoc />
    public ScreenFrame CaptureRegion(PixelRect region)
    {
        var clamped = region.Intersect(MonitorEnumerator.VirtualScreenBounds());

        if (clamped.IsEmpty)
        {
            throw new ArgumentException(
                $"Область {region} не пересекается с виртуальным десктопом — снимать нечего.",
                nameof(region));
        }

        return Capture(clamped);
    }

    /// <summary>Снять указанный прямоугольник. Все хендлы освобождаются в <c>finally</c>.</summary>
    private static ScreenFrame Capture(PixelRect area)
    {
        if (area.IsEmpty)
        {
            throw new ArgumentException($"Пустая область {area}: снимок нулевого размера невозможен.", nameof(area));
        }

        var started = Stopwatch.GetTimestamp();

        var screenDc = IntPtr.Zero;
        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("GetDC вернул нулевой контекст экрана: снимок недоступен.");
            }

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC вернул нулевой контекст: снимок недоступен.");
            }

            // Битмап создаётся совместимым с ЭКРАННЫМ DC, а не с memoryDc: у последнего
            // по умолчанию монохромный битмап 1×1, и совместимый с ним кадр вышел бы чёрно-белым.
            bitmap = CreateCompatibleBitmap(screenDc, area.Width, area.Height);
            if (bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateCompatibleBitmap отказал на размере {area.Width}×{area.Height}.");
            }

            previousBitmap = SelectObject(memoryDc, bitmap);

            if (!BitBlt(memoryDc, 0, 0, area.Width, area.Height, screenDc, area.Left, area.Top, SrcCopy | CaptureBlt))
            {
                throw new InvalidOperationException($"BitBlt отказал на области {area}.");
            }

            // GetDIBits требует, чтобы битмап НЕ был выбран в DC, — возвращаем прежний заранее.
            if (previousBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousBitmap);
                previousBitmap = IntPtr.Zero;
            }

            var pixels = ReadPixels(screenDc, bitmap, area.Width, area.Height);
            var elapsed = Stopwatch.GetElapsedTime(started);

            return new ScreenFrame(
                new PixelBuffer(area.Width, area.Height, PixelFormatKind.Bgra32, pixels),
                area,
                ImplementationName,
                elapsed);
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero) SelectObject(memoryDc, previousBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Байты кадра в Bgra32, строки сверху вниз.
    /// </summary>
    /// <remarks>
    /// Отрицательная <c>biHeight</c> — не хитрость, а единственный способ получить top-down DIB:
    /// по умолчанию GDI отдаёт строки СНИЗУ ВВЕРХ, и без этого кадр приходит перевёрнутым,
    /// а OCR получает вверх ногами то, что выглядит корректно по размеру.
    /// </remarks>
    private static byte[] ReadPixels(IntPtr screenDc, IntPtr bitmap, int width, int height)
    {
        var info = new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            },
        };

        var buffer = new byte[(long)width * height * 4];

        var copied = GetDIBits(screenDc, bitmap, 0, (uint)height, buffer, ref info, DibRgbColors);
        if (copied == 0)
        {
            throw new InvalidOperationException($"GetDIBits не отдал ни одной строки для кадра {width}×{height}.");
        }

        return buffer;
    }

    /// <summary>Собственных неуправляемых ресурсов реализация не держит — всё освобождается на месте.</summary>
    public void Dispose()
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;

        // Для 32-битного BI_RGB палитра не используется, но структура обязана быть цельной.
        public int bmiColorsPlaceholder;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        [Out] byte[] lpvBits, ref BitmapInfo lpbmi, uint usage);
}
