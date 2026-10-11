using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using BrainstormBuddy.Core.Ocr;

namespace BrainstormBuddy.Ocr;

/// <summary>
/// Движок «windows»: обёртка над WinRT <see cref="OcrEngine"/> (Windows.Media.Ocr).
/// </summary>
/// <remarks>
/// <para>
/// Живёт в WPF-проекте, а не в BrainstormBuddy.Core: Core собирается под <c>net8.0</c>
/// без WinRT-проекций, а <c>OcrEngine</c> требует TFM с версией Windows
/// (<c>net8.0-windows10.0.19041</c> у BrainstormBuddy.csproj).
/// </para>
/// <para>
/// Языки WinRT привязаны к установленным в системе языковым пакетам OCR. Запрошенные
/// RU/EN выбираются только если система их знает; иначе берётся первый доступный язык
/// (хуже, чем честный отказ — предсказуемый результат на любой машине). Если языков
/// нет вовсе или WinRT недоступен — <see cref="ITextRecognizer.Availability"/> =
/// <see cref="RecognizerAvailability.Unavailable"/>, и App откатывается на Tesseract.
/// </para>
/// <para>
/// Уверенности слов Windows OCR не отдаёт — <see cref="RecognizedWord.UnknownConfidence"/>;
/// лестница повторного прохода <see cref="PreprocessingRecognizer"/> для такого движка
/// опирается на качество текста (<see cref="LineMerger.TextQuality"/>), что у неё
/// предусмотрено.
/// </para>
/// <para>
/// Препроцессинг (грей/апскейл/инверсия) делает та же обёртка
/// <see cref="PreprocessingRecognizer"/>, поэтому сюда, как и в Tesseract, приходит
/// Gray8-растр. Конвертация в <see cref="SoftwareBitmap"/> — одна копия в
/// <see cref="Buffer"/>; это не критично, движок на CPU доминирует по времени.
/// </para>
/// </remarks>
public sealed class WinOcrRecognizer : ITextRecognizer, IDisposable
{
    private readonly object _gate = new();
    private OcrEngine? _engine;
    private Language? _language;
    private bool _disposed;

    public string Name => "Windows OCR";

    public RecognizerAvailability Availability { get; private set; } = RecognizerAvailability.Unknown;

    public string? UnavailabilityReason { get; private set; }

    public Task InitializeAsync(RecognitionLanguages languages, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            try
            {
                var candidates = new List<Language>(2);
                if (languages.HasFlag(RecognitionLanguages.Russian)) candidates.Add(new Language("ru"));
                if (languages.HasFlag(RecognitionLanguages.English)) candidates.Add(new Language("en"));

                _engine = null;
                _language = null;

                foreach (var language in candidates)
                {
                    if (OcrEngine.IsLanguageSupported(language))
                    {
                        _language = language;
                        _engine = OcrEngine.TryCreateFromLanguage(language);
                        if (_engine != null) break;
                    }
                }

                if (_engine == null)
                {
                    // Ни один из запрошенных языков не установлен — берём любой доступный,
                    // чтобы движок оставался рабочим на системе без RU/EN-пакетов.
                    var fallback = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
                    if (fallback != null)
                    {
                        _language = fallback;
                        _engine = OcrEngine.TryCreateFromLanguage(fallback);
                    }
                }

                if (_engine == null)
                {
                    MarkUnavailable(
                        "Windows OCR не создался ни для одного языка. " +
                        "Проверьте «Параметры → Язык» — OCR-пакеты ставятся вместе с языковыми.");
                }
                else
                {
                    Availability = RecognizerAvailability.Available;
                    UnavailabilityReason = null;
                }
            }
            catch (Exception ex)
            {
                // Широкий catch сознательно: на системах без WinRT-проекций падают
                // TypeLoadException/FileNotFoundException ещё до входа в методы OcrEngine,
                // и причина должна дойти до журнала, а не гаснуть в unknown-состоянии.
                _engine = null;
                MarkUnavailable($"Windows OCR недоступен: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Язык, с которым движок реально поднялся (BCP-47, например «ru»). Пусто, если недоступен.</summary>
    public string? ReadyLanguage => _language?.LanguageTag;

    public async Task<RecognitionResult> RecognizeAsync(
        PixelBuffer image,
        RecognitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (_disposed || Availability != RecognizerAvailability.Available || _engine is null)
        {
            return RecognitionResult.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();

        // WinRT жуёт не больше OcrEngine.MaxImageDimension по каждой стороне —
        // кадр крупнее ужимаем бокс-даунскейлом, иначе RecognizeAsync бросит.
        var input = LimitToMaxDimension(image, OcrEngine.MaxImageDimension);
        var bitmap = ToSoftwareBitmap(input);

        var ocrResult = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);

        var lines = new List<RecognizedLine>(ocrResult.Lines.Count);
        foreach (var line in ocrResult.Lines)
        {
            var words = new List<RecognizedWord>(line.Words.Count);
            foreach (var word in line.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                var rect = word.BoundingRect;
                var bounds = new PixelRect(
                    (int)Math.Round(rect.X), (int)Math.Round(rect.Y),
                    (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

                words.Add(new RecognizedWord(
                    word.Text,
                    bounds,
                    RecognizedWord.UnknownConfidence,
                    LanguageDetector.DetectWord(word.Text)));
            }

            if (words.Count > 0) lines.Add(new RecognizedLine(words));
        }

        stopwatch.Stop();
        return new RecognitionResult(lines, Name, stopwatch.Elapsed);
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_gate)
        {
            _engine = null;
            _disposed = true;
            Availability = RecognizerAvailability.Unavailable;
            UnavailabilityReason = "Движок Windows OCR освобождён.";
        }
    }

    private static PixelBuffer LimitToMaxDimension(PixelBuffer image, uint maxDimension)
    {
        var limit = (int)Math.Min(maxDimension, int.MaxValue);
        if (limit <= 0) return image;

        var largest = Math.Max(image.Width, image.Height);
        if (largest <= limit) return image;

        var divisor = (int)Math.Ceiling(largest / (double)limit);
        return ImagePreprocessor.Downscale(image, divisor);
    }

    /// <summary>
    /// Переложить PixelBuffer в WinRT <see cref="SoftwareBitmap"/>. Серый вход идёт
    /// в <see cref="BitmapPixelFormat.Gray8"/> напрямую, цветной — в Bgra8 с альфой
    /// <see cref="BitmapAlphaMode.Ignore"/>: кадры GDI часто несут альфу 0, и Ignore
    /// трактует их как непрозрачные, Premultiplied обнулил бы весь кадр.
    /// </summary>
    private static SoftwareBitmap ToSoftwareBitmap(PixelBuffer image)
    {
        var gray = image.Format == PixelFormatKind.Gray8;
        // Pixels — ReadOnlySpan; AsBuffer оборачивает byte[] в IBuffer без копии
        // (только обёртка над массивом), ToArray остаётся единственной копией.
        var buffer = image.Pixels.ToArray().AsBuffer();
        return SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            gray ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Ignore);
    }

    private void MarkUnavailable(string reason)
    {
        Availability = RecognizerAvailability.Unavailable;
        UnavailabilityReason = reason;
    }
}
