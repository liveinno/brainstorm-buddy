// WIC-обёртка «изображения в чате» (ТЗ .work/feature-chat-images/IMAGE-CHAT-SPEC.md §§3,6).
// Декод/перекодирование через System.Windows.Media.Imaging (PresentationCore) — без
// внешних зависимостей. Методы безопасны с любого потока (результаты freeze'ятся),
// кроме буфера обмена — Clipboard* только из STA/UI-потока.

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrainstormBuddy;

/// <summary>Кодек изображений чата: декод, EXIF-ориентация, даунскейл, клипборд.</summary>
public static class ChatImageCodec
{
    /// <summary>Результат декода: исходные байты + bitmap с УЖЕ применённой
    /// EXIF-ориентацией (W/H — после поворота). Потокобезопасно (Freeze).</summary>
    public sealed record Decoded(byte[] Bytes, BitmapSource Bitmap, int W, int H, string Mime, string Ext);

    /// <summary>Копия для отправки: байты, MIME и размеры после даунскейла.</summary>
    public sealed record SendCopy(byte[] Bytes, string Mime, int W, int H);

    // Лимиты копии для отправки (ТЗ §6): ≤4096px длинная сторона, ≤12 Мп, ≤8 МиБ.
    private const int SendMaxSide = 4096;
    private const long SendMaxPixels = 12_000_000;
    private const long SendMaxBytes = 8L * 1024 * 1024;
    private const int PreviewMaxSide = 640;

    /// <summary>Декод PNG/JPEG/BMP/GIF(кадр 0)/ICO/WebP с применением EXIF-ориентации.
    /// Формат определяется содержимым (магические байты + фактическим декодером),
    /// расширение имени файла — только фолбэк. null — не картинка/битый файл.</summary>
    public static Decoded? TryDecode(byte[] bytes, string? fileName = null)
    {
        if (bytes is not { Length: > 24 }) return null;
        // Дёшевый предфильтр по магическим байтам: отсекает мусор до создания декодера.
        var sniffed = SniffMime(bytes);
        var fallback = MimeFromExtension(fileName);
        if (sniffed == null && fallback == null) return null;
        try
        {
            var bmp = DecodeFrame0(bytes);
            if (bmp == null || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) return null;
            var oriented = ApplyExifOrientation(bmp);
            var mime = sniffed ?? fallback!;
            return new Decoded(bytes, oriented, oriented.PixelWidth, oriented.PixelHeight,
                mime, ExtFor(mime));
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException
            or InvalidDataException or ArgumentException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>BitmapSource → PNG-байты (без потерь, альфа сохраняется).</summary>
    public static byte[] ToPngBytes(BitmapSource src) => EncodeTo(src, new PngBitmapEncoder());

    /// <summary>PNG-байты → замороженный BitmapSource (кросс-поточный).</summary>
    public static BitmapSource ToBitmap(byte[] png)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad; // байты в память, стрим закрываем
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>
    /// Копия для отправки: EXIF-ориентация применена, даунскейл до ≤4096px/≤12Мп.
    /// Если оригинал уже в лимитах — возвращается как есть (BMP всегда → PNG).
    /// Иначе PNG-перекодирование; всё ещё >8МиБ → JPEG q92 (альфа на белом фоне).
    /// </summary>
    public static SendCopy MakeSendCopy(byte[] original, string mime, int w, int h)
    {
        var isBmp = mime is "image/bmp" or "image/x-bmp" or "image/x-ms-bmp";
        var decoded = TryDecode(original); // включает EXIF-ориентацию
        // Размеры для лимитов берём по фактическому bitmap (после EXIF-поворота);
        // недекодируемый файл — отдаём как есть, провайдер решит.
        int dw = decoded?.Bitmap.PixelWidth ?? w;
        int dh = decoded?.Bitmap.PixelHeight ?? h;
        var withinPixels = Math.Max(dw, dh) <= SendMaxSide && (long)dw * dh <= SendMaxPixels;
        var withinBytes = original.LongLength <= SendMaxBytes;

        if (withinPixels && withinBytes && !isBmp)
            return new SendCopy(original, mime, w, h);
        if (decoded == null)
            return new SendCopy(original, mime, w, h);

        BitmapSource bmp = decoded.Bitmap;
        if (!withinPixels)
            bmp = Scale(bmp, ScaleToLimits(bmp.PixelWidth, bmp.PixelHeight, SendMaxSide, SendMaxPixels));
        if (withinBytes && withinPixels) // сюда доходим только при isBmp
            return new SendCopy(ToPngBytes(bmp), "image/png", bmp.PixelWidth, bmp.PixelHeight);

        var pngBytes = ToPngBytes(bmp);
        if (pngBytes.LongLength <= SendMaxBytes)
            return new SendCopy(pngBytes, "image/png", bmp.PixelWidth, bmp.PixelHeight);

        // PNG не влез — JPEG q92 (альфа-канал запечём на белый фон).
        var flat = bmp.Format == PixelFormats.Pbgra32 || bmp.Format == PixelFormats.Bgra32
            ? FlattenOnWhite(bmp)
            : bmp;
        var jpgBytes = EncodeTo(flat, new JpegBitmapEncoder { QualityLevel = 92 });
        if (jpgBytes.LongLength > SendMaxBytes)
        {
            flat = Scale(flat, 0.5);
            jpgBytes = EncodeTo(flat, new JpegBitmapEncoder { QualityLevel = 92 });
        }
        return new SendCopy(jpgBytes, "image/jpeg", flat.PixelWidth, flat.PixelHeight);
    }

    /// <summary>Превью ≤640px по длинной стороне (только уменьшение) → PNG.</summary>
    public static byte[] MakePreviewPng(byte[] original, int w, int h)
    {
        var decoded = TryDecode(original);
        BitmapSource bmp = decoded?.Bitmap ?? DecodeFrame0(original)!;
        var longSide = Math.Max(bmp.PixelWidth, bmp.PixelHeight); // после EXIF-поворота
        if (longSide > PreviewMaxSide)
            bmp = Scale(bmp, (double)PreviewMaxSide / longSide);
        return ToPngBytes(bmp);
    }

    /// <summary>BitmapSource из буфера → PNG-байты. Пустой массив — картинки нет.</summary>
    public static byte[] ClipboardBitmapToPng()
    {
        var bmp = ClipboardRetry(() => System.Windows.Clipboard.GetImage());
        return bmp == null ? Array.Empty<byte>() : ToPngBytes(bmp);
    }

    /// <summary>
    /// Изображения из буфера: FileDropList картинок (png/jpg/jpeg/bmp/gif/webp,
    /// существующие файлы — байты оригинала побайтово), затем PNG-формат буфера,
    /// затем bitmap-представление. Дубли между источниками не собираем: файлы
    /// приоритетнее растровых представлений одной и той же картинки.
    /// </summary>
    public static List<(byte[] bytes, string fileName)> GetClipboardImages()
    {
        var items = new List<(byte[] bytes, string fileName)>();
        try
        {
            var files = ClipboardRetry(() => System.Windows.Clipboard.GetFileDropList());
            if (files != null)
            {
                foreach (var f in files.Cast<string>())
                {
                    if (f == null || !File.Exists(f)) continue;
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")) continue;
                    try { items.Add((File.ReadAllBytes(f), f)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch { /* буфер занят — дальше пробуем bitmap */ }
        if (items.Count > 0) return items;

        // PNG-представление буфера (Office, браузеры «копировать картинку»).
        try
        {
            if (System.Windows.Clipboard.ContainsData("PNG"))
            {
                var data = ClipboardRetry(() => System.Windows.Clipboard.GetData("PNG"));
                byte[]? png = data switch
                {
                    byte[] b => b,
                    MemoryStream ms => ms.ToArray(),
                    Stream s => ReadAll(s),
                    _ => null,
                };
                if (png is { Length: > 0 })
                {
                    items.Add((png, "clipboard.png"));
                    return items;
                }
            }
        }
        catch { /* экзотический формат — пробуем bitmap */ }

        try
        {
            var bytes = ClipboardBitmapToPng();
            if (bytes.Length > 0) items.Add((bytes, "clipboard.png"));
        }
        catch { /* буфер заблокирован — возвращаем что есть */ }
        return items;
    }

    /// <summary>Публикация изображения в буфер: PNG (прозрачность) + BitmapSource.
    /// SetDataObject(..., true) — данные переживают закрытие приложения (ТЗ §5).</summary>
    public static void PutImageToClipboard(byte[] png, BitmapSource bmp)
    {
        var obj = new System.Windows.DataObject();
        obj.SetData("PNG", new MemoryStream(png));       // копипастеры Office/мессенджеров
        obj.SetData(DataFormats.Bitmap, bmp);            // DIB/DIBV5 для классики
        ClipboardRetry(() => System.Windows.Clipboard.SetDataObject(obj, true));
    }

    // ── Внутренности ────────────────────────────────────────────────────────

    /// <summary>Декод первого кадра с учётом цветового профиля; результат заморожен.</summary>
    private static BitmapSource? DecodeFrame0(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames[0];
        if (frame.CanFreeze) frame.Freeze();
        return frame;
    }

    /// <summary>EXIF Orientation 1..8 → поворот/зеркалирование через TransformedBitmap.</summary>
    private static BitmapSource ApplyExifOrientation(BitmapSource src)
    {
        try
        {
            if (src is not BitmapFrame frame || frame.Metadata is not BitmapMetadata meta) return src;
            if (!meta.ContainsQuery("/app1/ifd/{ushort=274}")) return src;
            if (meta.GetQuery("/app1/ifd/{ushort=274}") is not ushort o) return src;
            TransformGroup tg = o switch
            {
                2 => new TransformGroup { Children = { new ScaleTransform(-1, 1, 0, 0) } },
                3 => new TransformGroup { Children = { new RotateTransform(180) } },
                4 => new TransformGroup { Children = { new RotateTransform(180), new ScaleTransform(-1, 1, 0, 0) } },
                5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1, 0, 0) } },
                6 => new TransformGroup { Children = { new RotateTransform(90) } },
                7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1, 0, 0) } },
                8 => new TransformGroup { Children = { new RotateTransform(270) } },
                _ => new TransformGroup(),
            };
            if (tg.Children.Count == 0) return src;
            var tb = new TransformedBitmap(src, tg);
            tb.Freeze();
            return tb;
        }
        catch { return src; } // битые метаданные — берём как есть
    }

    private static double ScaleToLimits(int w, int h, int maxSide, long maxPixels)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
        var px = (double)w * h;
        if (px > maxPixels) scale = Math.Min(scale, Math.Sqrt(maxPixels / px));
        return scale;
    }

    private static BitmapSource Scale(BitmapSource src, double scale)
    {
        if (scale >= 0.999) return src;
        var tb = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        tb.Freeze();
        return tb;
    }

    /// <summary>Альфа → белый фон: DrawingVisual с белым Rect поверх (для JPEG).</summary>
    private static BitmapSource FlattenOnWhite(BitmapSource src)
    {
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, src.PixelWidth, src.PixelHeight));
            ctx.DrawImage(src, new System.Windows.Rect(0, 0, src.PixelWidth, src.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static byte[] EncodeTo(BitmapSource src, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Магические байты → MIME (содержимое важнее расширения, ТЗ §3).</summary>
    private static string? SniffMime(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return "image/x-icon";
        return null;
    }

    private static string? MimeFromExtension(string? fileName) => Path.GetExtension(fileName ?? "")
        .ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        _ => null,
    };

    private static string ExtFor(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/bmp" => ".bmp",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/x-icon" => ".ico",
        _ => ".png",
    };

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>До 3 коротких попыток буфера обмена (ТЗ §3, суммарно <1с).</summary>
    private static T? ClipboardRetry<T>(Func<T?> op) where T : class
    {
        for (var i = 0; i < 3; i++)
        {
            try { return op(); }
            catch (System.Runtime.InteropServices.COMException) when (i < 2) { Thread.Sleep(150); }
            catch (System.Runtime.InteropServices.ExternalException) when (i < 2) { Thread.Sleep(150); }
        }
        return null;
    }

    private static void ClipboardRetry(Action op)
    {
        for (var i = 0; i < 3; i++)
        {
            try { op(); return; }
            catch (System.Runtime.InteropServices.COMException) when (i < 2) { Thread.Sleep(150); }
            catch (System.Runtime.InteropServices.ExternalException) when (i < 2) { Thread.Sleep(150); }
        }
    }
}
