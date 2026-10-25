namespace BrainstormBuddy.Core.Attachments;

/// <summary>Вид поддерживаемого документа вложения.</summary>
public enum DocumentKind { Txt, Markdown, Docx, Xlsx, Pptx, Pdf }

/// <summary>
/// Локатор места в документе: «стр. 3», «Бюджет!A4:D9», «Слайд 7», «строки 10–40».
/// <paramref name="Kind"/> — тип локатора (lines/page/section/paragraph/table/sheet/slide),
/// <paramref name="Label"/> — короткая русская подпись для показа пользователю.
/// </summary>
public sealed record TextAnchor(string Kind, string Label);

/// <summary>Атомарный фрагмент извлечённого текста с координатой источника.</summary>
public sealed record DocSegment(string Text, TextAnchor Anchor, int Order);

/// <summary>
/// Результат извлечения текста документа.
/// <paramref name="Warnings"/> — предупреждения об ограничениях («скрытые листы пропущены»,
/// «заметки докладчика не читаются», «OCR: N стр.»); это не ошибки, документ читаем.
/// <paramref name="Interrupted"/> — извлечение остановлено до конца документа
/// (пользовательская остановка с <see cref="PdfReadOptions.KeepPartialOnCancel"/>):
/// сегменты содержат только обработанные части, повтор может продолжить с них.
/// Для PDF последние пять полей содержат явные метрики страниц; для остальных
/// форматов они остаются равными нулю.
/// </summary>
public sealed record ExtractedDocument(
    IReadOnlyList<DocSegment> Segments,
    int CharCount,
    IReadOnlyList<string> Warnings,
    bool Interrupted = false,
    int SkippedNoTextPages = 0,
    int TotalPages = 0,
    int ProcessedPages = 0,
    int TextLayerPages = 0,
    int OcrPages = 0,
    int EmptyPages = 0);

/// <summary>Прогресс извлечения текста: Done единиц из Total
/// (для PDF — страницы, для остальных форматов — файл целиком, Total=1).</summary>
public readonly record struct ExtractionProgress(int Done, int Total);

/// <summary>Режим чтения PDF: только текстовый слой или текстовый слой + OCR пустых страниц.</summary>
public enum PdfReadMode
{
    /// <summary>Быстрый: только встроенный текстовый слой; страницы без него
    /// пропускаются с предупреждением (OCR не вызывается, LLM не нужен).</summary>
    Fast,

    /// <summary>Полный: текстовый слой + постраничный OCR страниц без текста
    /// (лимит OCR — <see cref="PdfReadOptions.OcrMaxPages"/>).</summary>
    Full,
}

/// <summary>
/// Опции чтения PDF на один импорт: выбираются в момент добавления файла
/// (пикер читает Config.Attachments/Audio), в FactJob не персистятся.
/// </summary>
/// <param name="Mode">Режим чтения (по умолчанию <see cref="PdfReadMode.Fast"/>).</param>
/// <param name="OcrMaxPages">Потолок OCR-страниц на документ в режиме Full;
/// 0 и меньше — без потолка (аудит 2026-10-14 §3.9: автоматический обрыв на
/// 50 страницах снят — обрабатываем все страницы принятого документа).</param>
/// <param name="OcrParallelism">Параллельность OCR страниц (Audio.AttachmentsOcrParallelism):
/// 0 — auto (до 4 потоков), 1 — последовательно, 2/4 — явный предел.</param>
/// <param name="KeepPartialOnCancel">При отмене вернуть частичный документ
/// (<see cref="ExtractedDocument.Interrupted"/>) вместо OperationCanceledException —
/// используется остановкой «сохранить обработанное».</param>
/// <param name="ReuseSegments">Ранее извлечённые сегменты этого же документа
/// (остановка/смена режима): страницы с сегментом пропускаются, обработка
/// продолжается с оставшихся.</param>
public sealed record PdfReadOptions(
    PdfReadMode Mode = PdfReadMode.Fast,
    int OcrMaxPages = 0,
    int OcrParallelism = 0,
    bool KeepPartialOnCancel = false,
    IReadOnlyList<DocSegment>? ReuseSegments = null)
{
    /// <summary>Быстрый режим по умолчанию (старые конфиги без поля тоже читаются как fast).</summary>
    public static PdfReadOptions Default { get; } = new();

    /// <summary>Эффективный лимит OCR-страниц (0/минус → без ограничения).</summary>
    public int EffectiveOcrMaxPages =>
        OcrMaxPages > 0 ? OcrMaxPages : int.MaxValue;

    /// <summary>Эффективная параллельность OCR (0/auto → по числу ядер, потолок 4).</summary>
    public int EffectiveOcrParallelism =>
        OcrParallelism > 0 ? OcrParallelism : Math.Clamp(Environment.ProcessorCount, 1, 4);

    /// <summary>
    /// Собирает опции из строк конфига (Attachments.PdfMode/PdfOcrMaxPages):
    /// «full» (без учёта регистра) → Full, всё остальное/пустое → Fast.
    /// </summary>
    public static PdfReadOptions FromConfig(string? mode, int ocrMaxPages, string? ocrParallelism = null) =>
        new(string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)
                ? PdfReadMode.Full : PdfReadMode.Fast,
            ocrMaxPages,
            ParseParallelism(ocrParallelism));

    /// <summary>«auto»/пусто/мусор → 0 (auto); «1»/«2»/«4» — явное значение.</summary>
    public static int ParseParallelism(string? s) =>
        int.TryParse(s, out var n) && n > 0 ? n : 0;
}

/// <summary>
/// Отказ в обработке файла. <see cref="Exception.Message"/> — короткая русская причина,
/// которая показывается пользователю прямо в статусе файла.
/// </summary>
public class AttachmentRejectedException : Exception
{
    /// <summary>Отказ устраним переключением режима PDF на «полный» (OCR):
    /// пикер перечитывает такой пункт автоматически при смене режима.</summary>
    public bool RetryableWithOcr { get; }

    public AttachmentRejectedException(string userReason, bool retryableWithOcr = false)
        : base(userReason) => RetryableWithOcr = retryableWithOcr;
}

/// <summary>
/// Кодировка текстового файла не определяется однозначно. Вызывающий предлагает
/// пользователю явный выбор (диалог «прочитать как Windows-1251?») и повторяет
/// импорт, передав кодировку параметром экстрактору.
/// </summary>
public sealed class EncodingAmbiguousException : AttachmentRejectedException
{
    /// <summary>WebName предлагаемой кодировки (например, «windows-1251»).</summary>
    public string SuggestedEncoding { get; }

    public EncodingAmbiguousException(string userReason, string suggestedEncoding)
        : base(userReason) => SuggestedEncoding = suggestedEncoding;
}

/// <summary>
/// Постраничный OCR для PDF без текстового слоя. Реализация живёт в приложении
/// (Windows.Data.Pdf + Tesseract); Core только потребляет контракт.
/// </summary>
public interface IPdfPageOcr
{
    /// <summary>Готов ли OCR к работе прямо сейчас.</summary>
    bool IsAvailable { get; }

    /// <summary>Причина недоступности (для статуса файла), null когда доступен.</summary>
    string? UnavailableReason { get; }

    /// <summary>Распознаёт одну страницу PDF. <paramref name="pageIndex"/> — 0-based.</summary>
    Task<string> RecognizePageAsync(string pdfPath, int pageIndex, CancellationToken ct);
}

/// <summary>
/// Сервис извлечения текста. Ничего не знает про файловую систему импорта —
/// работает с байтами/путём локальной копии.
/// </summary>
public interface IDocumentExtractor
{
    Task<ExtractedDocument> ExtractAsync(
        string storedPath, DocumentKind kind, string displayName,
        IPdfPageOcr? pdfOcr, CancellationToken ct);
}

/// <summary>
/// Общие лимиты обработки вложений. Превышение — отказ через
/// <see cref="AttachmentRejectedException"/> с короткой русской причиной,
/// а не тихая обрезка результата.
/// </summary>
public static class AttachmentLimits
{
    /// <summary>Максимальный размер одного файла, байт (25 МиБ).</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// <summary>Суммарный размер файлов в одном импорте, байт (100 МиБ).</summary>
    public const long MaxImportBytes = 100L * 1024 * 1024;

    /// <summary>Максимум активных документов на одну встречу.</summary>
    public const int MaxActiveDocumentsPerMeeting = 10;

    /// <summary>
    /// Максимум извлечённых символов текста на документ (на один файл, не на сессию).
    /// 2 000 000: покрывает принятый лимитом размера PDF на ≥350 страниц
    /// (~5 700 симв/стр. — плотная страница A4 целиком); бюджет LLM-запроса
    /// ограничивает не этот предел, а AttachmentContextBuilder/WholeDocumentQa —
    /// полный текст в модель целиком не уходит в любом случае.
    /// </summary>
    public const int MaxExtractedCharsPerDocument = 2_000_000;

    /// <summary>Суммарный распакованный размер Office-контейнера, байт (200 МиБ).</summary>
    public const long MaxOfficeUncompressedBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Текстовый слой страницы PDF короче этого порога считается отсутствующим —
    /// страница уходит в OCR или помечается «без текстового слоя».
    /// </summary>
    internal const int MinPdfTextLayerChars = 10;
}

/// <summary>
/// Накопитель сегментов с общей проверкой лимита символов на документ:
/// превышение <see cref="AttachmentLimits.MaxExtractedCharsPerDocument"/> — отказ,
/// обрезать «первые N» нельзя, чтобы не выдавать часть за полный текст.
/// </summary>
internal static class SegmentAccumulator
{
    public static void Add(List<DocSegment> segments, string text, TextAnchor anchor,
        ref int order, ref int totalChars)
    {
        totalChars += text.Length;
        if (totalChars > AttachmentLimits.MaxExtractedCharsPerDocument)
        {
            throw new AttachmentRejectedException(
                $"Текст документа больше {AttachmentLimits.MaxExtractedCharsPerDocument} символов на один файл — разделите файл");
        }
        segments.Add(new DocSegment(text, anchor, order++));
    }
}

/// <summary>
/// Трассировка пайплайна вложений — единая категория логов «Attach».
/// <see cref="Debug.WriteLine"/> в release-билде не достаёт до app.log, поэтому
/// App один раз на старте ставит <see cref="Sink"/> → LoggingService: построчные
/// ошибки/замеры OCR из PdfExtractor должны быть в журнале диагностики (§4/§14).
/// </summary>
public static class AttachLog
{
    /// <summary>Необязательный приёмник строк («[Attach] …») → журнал приложения.</summary>
    public static Action<string>? Sink;

    public static void Warn(string message) => Write(message);

    public static void Info(string message) => Write(message);

    private static void Write(string message)
    {
        var line = $"[Attach] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        Sink?.Invoke(line);
    }
}
