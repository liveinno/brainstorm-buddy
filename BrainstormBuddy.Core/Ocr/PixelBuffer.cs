namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Растр в памяти — единица работы препроцессинга (ТЗ §2.3).
/// </summary>
/// <remarks>
/// Отдельный тип вместо <c>BitmapSource</c> или <c>System.Drawing.Bitmap</c> нужен затем,
/// чтобы весь препроцессинг остался ЧИСТЫМИ ФУНКЦИЯМИ над массивом байтов: такие функции
/// покрываются юнит-тестами без окна, без STA-потока и без GDI-хендлов (ТЗ §2.3 требует
/// «все шаги — чистые функции, покрыты тестами»). Конвертация в платформенные типы живёт
/// на границе, в BrainstormBuddy.App.
///
/// Буфер плотный: <see cref="Stride"/> всегда равен <c>Width * BytesPerPixel</c>, без
/// выравнивания строк. Это упрощает арифметику и делает сравнение двух буферов побайтовым —
/// на этом держится тест «два прогона препроцессинга дают идентичный выход».
/// </remarks>
public sealed class PixelBuffer
{
    private readonly byte[] _pixels;

    public PixelBuffer(int width, int height, PixelFormatKind format, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);

        var bytesPerPixel = BytesPerPixelOf(format);
        var expected = (long)width * height * bytesPerPixel;

        if (pixels.LongLength != expected)
        {
            throw new ArgumentException(
                $"Размер массива {pixels.LongLength} не совпадает с {width}×{height}×{bytesPerPixel} = {expected}.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Format = format;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormatKind Format { get; }

    public int BytesPerPixel => BytesPerPixelOf(Format);

    /// <summary>Байт на строку. Буфер плотный, выравнивания нет.</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>Сырые байты. Менять их извне нельзя — препроцессинг всегда возвращает новый буфер.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>Создать пустой буфер заданного размера (все байты — 0).</summary>
    public static PixelBuffer Allocate(int width, int height, PixelFormatKind format)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        return new PixelBuffer(width, height, format, new byte[(long)width * height * BytesPerPixelOf(format)]);
    }

    /// <summary>Яркость пикселя 0..255. Для <see cref="PixelFormatKind.Gray8"/> — сам байт.</summary>
    public byte GetLuminance(int x, int y)
    {
        var offset = OffsetOf(x, y);

        if (Format == PixelFormatKind.Gray8) return _pixels[offset];

        // Коэффициенты BT.601: человеческий глаз сильнее чувствителен к зелёному.
        // Единая формула используется всеми шагами препроцессинга, поэтому на границе
        // порога инверсии нет расхождений между операциями.
        var b = _pixels[offset];
        var g = _pixels[offset + 1];
        var r = _pixels[offset + 2];

        return (byte)((r * 299 + g * 587 + b * 114) / 1000);
    }

    /// <summary>
    /// Максимум из каналов R, G, B — 0..255. Для <see cref="PixelFormatKind.Gray8"/> — сам байт.
    /// В отличие от яркости, не «топит» насыщенный цвет: синий 0,0,255 по яркости даёт 29
    /// и сливается с тёмным фоном, а по максимуму каналов — 255.
    /// </summary>
    public byte GetMaxChannel(int x, int y)
    {
        var offset = OffsetOf(x, y);

        if (Format == PixelFormatKind.Gray8) return _pixels[offset];

        var b = _pixels[offset];
        var g = _pixels[offset + 1];
        var r = _pixels[offset + 2];

        return Math.Max(r, Math.Max(g, b));
    }

    /// <summary>
    /// Насыщенность пикселя как размах каналов max-min, 0..255. Ноль — чистый серый.
    /// Высокая — насыщенный цвет (красный, синий). ClearType-бахрома по краям букв
    /// даёт лишь небольшой размах, и по нему её можно отличить от настоящего цветного текста.
    /// </summary>
    public byte GetChannelSpread(int x, int y)
    {
        if (Format == PixelFormatKind.Gray8) return 0;

        var offset = OffsetOf(x, y);
        var b = _pixels[offset];
        var g = _pixels[offset + 1];
        var r = _pixels[offset + 2];

        return (byte)(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)));
    }

    /// <summary>Байт по смещению — для тестов и внутренних преобразований.</summary>
    public byte GetByte(int index) => _pixels[index];

    /// <summary>Копия сырых байтов. Нужна там, где вызывающий обязан владеть массивом.</summary>
    public byte[] ToArray() => (byte[])_pixels.Clone();

    /// <summary>Побайтовое равенство размеров, формата и содержимого.</summary>
    public bool ContentEquals(PixelBuffer other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Width == other.Width
               && Height == other.Height
               && Format == other.Format
               && _pixels.AsSpan().SequenceEqual(other._pixels);
    }

    internal byte[] RawArray => _pixels;

    private int OffsetOf(int x, int y)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x), x, $"Ширина {Width}.");
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y), y, $"Высота {Height}.");

        return y * Stride + x * BytesPerPixel;
    }

    private static int BytesPerPixelOf(PixelFormatKind format) => format switch
    {
        PixelFormatKind.Bgra32 => 4,
        PixelFormatKind.Gray8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Неизвестный формат пикселей."),
    };

    public override string ToString() => $"{Width}×{Height} {Format}";
}
