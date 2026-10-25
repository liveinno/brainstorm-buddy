namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Точка входа извлечения: диспетчеризует по <see cref="DocumentKind"/> на
/// <see cref="TxtExtractor"/>, <see cref="OpenXmlExtractor"/> и
/// <see cref="PdfExtractor"/>. Раннеру достаточно одного экземпляра.
/// <paramref name="encodingWebName"/> — только для TXT/MD: явная кодировка при
/// повторе после <see cref="EncodingAmbiguousException"/>.
/// <paramref name="pdfOptions"/> — режим чтения PDF (умолчание
/// <see cref="PdfReadOptions.Default"/> — быстрый, только текстовый слой);
/// для не-PDF игнорируется. <paramref name="progress"/> — прогресс
/// извлечения (страницы для PDF, файл целиком для остальных форматов).
/// </summary>
public sealed class DocumentExtractor : IDocumentExtractor
{
    /// <summary>Контрактная точка входа без явной кодировки/опций (быстрый режим PDF).</summary>
    Task<ExtractedDocument> IDocumentExtractor.ExtractAsync(
        string storedPath, DocumentKind kind, string displayName,
        IPdfPageOcr? pdfOcr, CancellationToken ct) =>
        ExtractAsync(storedPath, kind, displayName, pdfOcr, ct);

    /// <summary>
    /// Извлекает текст. <paramref name="encodingWebName"/> (WebName, напр.
    /// «windows-1251») используется только для TXT/MD и только когда вызывающий
    /// явно выбрал кодировку после <see cref="EncodingAmbiguousException"/>.
    /// </summary>
    public async Task<ExtractedDocument> ExtractAsync(
        string storedPath, DocumentKind kind, string displayName,
        IPdfPageOcr? pdfOcr, CancellationToken ct, string? encodingWebName = null,
        PdfReadOptions? pdfOptions = null, IProgress<ExtractionProgress>? progress = null)
    {
        if (kind == DocumentKind.Pdf)
        {
            // Постраничный прогресс ведёт сам PdfExtractor (Total = числу страниц).
            return await PdfExtractor
                .ExtractAsync(storedPath, displayName, pdfOcr, ct, pdfOptions, progress)
                .ConfigureAwait(false);
        }

        // Остальные форматы — однофайловая работа: 0/1 перед стартом, 1/1 по завершении.
        progress?.Report(new ExtractionProgress(0, 1));
        var doc = await (kind switch
        {
            DocumentKind.Txt or DocumentKind.Markdown =>
                TxtExtractor.ExtractAsync(storedPath, displayName, encodingWebName, ct),
            DocumentKind.Docx or DocumentKind.Xlsx or DocumentKind.Pptx =>
                OpenXmlExtractor.ExtractAsync(storedPath, kind, displayName, ct),
            _ => throw new AttachmentRejectedException("Формат файла не поддерживается")
        }).ConfigureAwait(false);
        progress?.Report(new ExtractionProgress(1, 1));
        return doc;
    }

    /// <summary>
    /// Перечитка уже импортированного документа: уже сохранённые сегменты
    /// переиспользуются через <see cref="PdfReadOptions.ReuseSegments"/> —
    /// дочитываются только недостающие страницы (остановка/смена режима,
    /// §5.1–5.2). Не-PDF форматы переизвлекаются целиком (reuse не применяется).
    /// </summary>
    /// <param name="priorSegments">Сегменты, сохранённые у документа ранее.</param>
    /// <param name="encodingWebName">Явная кодировка TXT/MD после подтверждения пользователя.</param>
    public async Task<ExtractedDocument> ExtractReuseAsync(
        string storedPath, DocumentKind kind, string displayName,
        IPdfPageOcr? pdfOcr, IReadOnlyList<DocSegment> priorSegments,
        PdfReadOptions pdfOptions, IProgress<ExtractionProgress>? progress,
        CancellationToken ct, string? encodingWebName = null)
    {
        var opts = pdfOptions with { ReuseSegments = priorSegments };
        return await ExtractAsync(storedPath, kind, displayName, pdfOcr, ct,
            encodingWebName: encodingWebName, pdfOptions: opts, progress: progress)
            .ConfigureAwait(false);
    }
}
