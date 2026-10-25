using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Core.Ocr;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Pdf;

/// <summary>
/// Постраничный OCR PDF для вложений чата: WinRT <see cref="PdfDocument"/> рендерит
/// страницу (scale 2.0 ≈ 192 dpi) в Bgra32 <see cref="PixelBuffer"/>, дальше штатный
/// Tesseract (<see cref="RecognizerFactory.Create()"/> — препроцессинг навешивается
/// всегда, tessdata рядом с exe). Живёт в WPF-проекте: Core (net8.0) WinRT не видит.
///
/// Параллелизм (аудит 2026-10-15 §5.3): <see cref="RecognizePageAsync"/> вызывается
/// конкурентно из <c>PdfExtractor</c> (планировщик с потолком
/// <c>PdfReadOptions.EffectiveOcrParallelism</c> ≤ <see cref="MaxOcrWorkers"/>).
/// <see cref="TesseractRecognizer"/> потокобезопасен, но весь движок сидит на одном
/// внутреннем локе — один экземпляр распараллелить нельзя, поэтому здесь пул
/// <see cref="OcrWorker"/>: у каждого свой <see cref="ITextRecognizer"/> и свой
/// WinRT-<see cref="PdfDocument"/> (рендер чужого документа между потоками не делим).
/// Глобальный потолок воркеров — <see cref="MaxOcrWorkers"/>: фактический предел
/// задаёт вызывающий семафором (юзерская «1» остаётся последовательной), пул лишь
/// не даёт зайти больше воркеров, чем их есть.
///
/// Отдельный <see cref="PdfDocument"/> на воркер = не больше <see cref="MaxOcrWorkers"/>
/// открытых документов (старый кэш LRU держал столько же). Повторные страницы одного
/// скана не переоткрывают файл (двойное открытие с PdfPig в Core допустимо — читатели).
/// Ошибки WinRT (кроме ошибок конкретного файла) переводят сервис в
/// <see cref="IsAvailable"/>=false — повторные попытки не мусорят в лог.
/// </summary>
public sealed class PdfOcrService : IPdfPageOcr, IDisposable
{
    private const string LogCategory = "Attach";
    /// <summary>Потолок воркеров пула: совпадает с верхней границей
    /// EffectiveOcrParallelism (clamp(cores,1,4)) — больше воркеров планировщик
    /// страниц всё равно не запросит. Каждый воркер = Tesseract rus+eng + открытый
    /// PdfDocument: десятки МБ нативной памяти на штуку, потолок удерживает бюджет.</summary>
    private const int MaxOcrWorkers = 4;
    private static readonly TimeSpan OcrInitTimeout = TimeSpan.FromSeconds(30);

    private readonly LoggingService? _log;
    /// <summary>Глобальный ограничитель параллельных проходов (не больше воркеров).</summary>
    private readonly SemaphoreSlim _concurrency = new(MaxOcrWorkers, MaxOcrWorkers);
    /// <summary>Сериализация создания движков: параллельный init rus+eng дважды грузит
    /// модели в память разом — инициализация дешевле по очереди, распознавание параллелится.</summary>
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly List<OcrWorker> _workers = new();
    private readonly object _workersLock = new();

    private bool _unavailable;
    private string? _unavailableReason;
    private bool _disposed;

    /// <summary>Воркер пула: свой экземпляр Tesseract (движок внутренне сериализует
    /// вызовы — общий экземпляр убил бы параллелизм) и свой открытый PdfDocument
    /// (WinRT-документ между конкурентными рендерами не делим).</summary>
    private sealed class OcrWorker
    {
        public bool Busy;
        public ITextRecognizer? Recognizer;
        public string? DocPath;
        public PdfDocument? Doc;

        /// <summary>PdfDocument по пути из кэша воркера (промах → LoadFromFileAsync;
        /// старый документ отпускается — WinRT PdfDocument не IDisposable).</summary>
        public async Task<PdfDocument> GetDocumentAsync(string pdfPath, CancellationToken ct)
        {
            var full = Path.GetFullPath(pdfPath);
            if (Doc != null && string.Equals(DocPath, full, StringComparison.OrdinalIgnoreCase))
                return Doc;
            var file = await StorageFile.GetFileFromPathAsync(full).AsTask(ct).ConfigureAwait(false);
            var doc = await PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
            if (doc == null)
                throw new IOException($"PDF не открылся: {Path.GetFileName(pdfPath)}");
            Doc = doc;
            DocPath = full;
            return doc;
        }
    }

    public PdfOcrService(LoggingService? log = null) => _log = log;

    /// <inheritdoc/>
    public bool IsAvailable => !_unavailable;

    /// <inheritdoc/>
    public string? UnavailableReason => _unavailableReason;

    /// <inheritdoc/>
    /// Отказ по файлу (битый PDF, пароль) — исключение наверх, сервис остаётся
    /// доступным. Отказ среды (нет tessdata, WinRT не взлетел) — IsAvailable=false
    /// и дальше RecognizePageAsync падает быстро с той же причиной.
    public async Task<string> RecognizePageAsync(string pdfPath, int pageIndex, CancellationToken ct)
    {
        ThrowIfUnavailable();
        // Межфайловая параллельность тоже проходит этот семафор — суммарное число
        // тяжёлых OCR-проходов никогда не превысит MaxOcrWorkers.
        await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        OcrWorker? worker = null;
        try
        {
            ThrowIfUnavailable();
            worker = await RentWorkerAsync(ct).ConfigureAwait(false);
            var doc = await worker.GetDocumentAsync(pdfPath, ct).ConfigureAwait(false);
            if (pageIndex < 0 || pageIndex >= doc.PageCount)
                throw new AttachmentRejectedException($"страница {pageIndex + 1} вне диапазона (всего {doc.PageCount})");

            using var page = doc.GetPage((uint)pageIndex);
            var pixels = await RenderPageAsync(page, ct).ConfigureAwait(false);
            var result = await worker.Recognizer!.RecognizeAsync(pixels, RecognitionOptions.Default, ct)
                .ConfigureAwait(false);
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        catch (Exception ex) when (IsEnvironmentFailure(ex))
        {
            MarkUnavailable(ex.Message);
            throw;
        }
        finally
        {
            if (worker != null) ReturnWorker(worker);
            _concurrency.Release();
        }
    }

    /// <summary>Среда сломана (WinRT/DLL/tessdata), а не файл — сервис глушим навсегда.</summary>
    private static bool IsEnvironmentFailure(Exception ex) =>
        ex is TypeInitializationException or DllNotFoundException
            or BadImageFormatException or PlatformNotSupportedException
            or EntryPointNotFoundException;

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PdfOcrService));
        if (_unavailable)
            throw new InvalidOperationException($"PDF OCR недоступен: {_unavailableReason}");
    }

    private void MarkUnavailable(string reason)
    {
        _unavailableReason = reason;
        _unavailable = true;
        _log?.Warn($"PdfOcrService недоступен: {reason}", LogCategory);
    }

    /// <summary>Захват свободного воркера; свободных нет и пул не заполнен — создаём
    /// новый (лениво, чтобы файл с парой страниц не платил за весь пул). Первый
    /// захват воркера прогревает его Tesseract под _initGate; провал init — воркер
    /// из пула удаляется, слот не остаётся мёртвым.</summary>
    private async Task<OcrWorker> RentWorkerAsync(CancellationToken ct)
    {
        OcrWorker worker;
        lock (_workersLock)
        {
            worker = _workers.FirstOrDefault(x => !x.Busy)
                ?? new OcrWorker();
            worker.Busy = true;
            if (!_workers.Contains(worker)) _workers.Add(worker);
        }
        if (worker.Recognizer != null) return worker;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Гонка двух прогревов: второй увидит недоступность и сдаст слот.
            ThrowIfUnavailable();
            worker.Recognizer = await CreateRecognizerAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_workersLock) _workers.Remove(worker);
            throw;
        }
        finally { _initGate.Release(); }
        return worker;
    }

    private void ReturnWorker(OcrWorker worker)
    {
        lock (_workersLock) worker.Busy = false;
    }

    /// <summary>Свой Tesseract на воркер: та же конфигурация, что была у одиночного
    /// движка — rus+eng одним проходом, препроцессинг через <see cref="RecognizerFactory"/>.</summary>
    private async Task<ITextRecognizer> CreateRecognizerAsync(CancellationToken ct)
    {
        var tessdata = TesseractRecognizer.DefaultTessdataPath;
        if (!Directory.Exists(tessdata))
        {
            MarkUnavailable($"tessdata не найден: {tessdata}");
            throw new InvalidOperationException($"PDF OCR недоступен: {_unavailableReason}");
        }
        var rec = RecognizerFactory.Create(tessdata,
            trace: m => _log?.Debug(m, LogCategory));
        try
        {
            // Русский+английский — как у экранного OCR; InitializeAsync не бросает,
            // недоступность приходит через Availability.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(OcrInitTimeout);
            await rec.InitializeAsync(RecognitionLanguages.RussianAndEnglish, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rec.Dispose();
            MarkUnavailable($"инициализация Tesseract: {ex.Message}");
            throw new InvalidOperationException($"PDF OCR недоступен: {_unavailableReason}");
        }
        if (rec.Availability != RecognizerAvailability.Available)
        {
            var reason = rec.UnavailabilityReason ?? "Tesseract не готов";
            rec.Dispose();
            MarkUnavailable(reason);
            throw new InvalidOperationException($"PDF OCR недоступен: {reason}");
        }
        return rec;
    }

    /// <summary>Рендер страницы в Bgra32 PixelBuffer (scale 2.0 — 192 dpi для Tesseract).</summary>
    private static async Task<PixelBuffer> RenderPageAsync(PdfPage page, CancellationToken ct)
    {
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, (int)Math.Round(page.Size.Width * 2.0)),
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options).AsTask(ct).ConfigureAwait(false);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        using var bmp = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct).ConfigureAwait(false);
        var w = (int)bmp.PixelWidth;
        var h = (int)bmp.PixelHeight;
        var bytes = new byte[w * h * 4];
        bmp.CopyToBuffer(bytes.AsBuffer());
        return new PixelBuffer(w, h, PixelFormatKind.Bgra32, bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _concurrency.Dispose();
        _initGate.Dispose();
        lock (_workersLock)
        {
            foreach (var w in _workers)
                if (w.Recognizer is IDisposable d) d.Dispose();
            _workers.Clear();
        }
    }
}
