namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Отпечаток условий, при которых сохранённые сегменты документа считаются
/// актуальными (documents.extract_key). Повторный импорт того же содержимого
/// (дедуп по sha256, в т.ч. после рестарта процесса) переиспользует сегменты,
/// только если отпечаток сохранённого прогона совпал: для PDF — режим чтения,
/// политика OCR (потолок страниц, доступность движка), для остальных форматов —
/// только вид документа (извлечение из тех же байтов детерминировано; явно
/// выбранная кодировка TXT/MD даёт тот же корректный текст).
/// <see cref="Version"/> повышается при изменении правил нарезки/OCR — тогда
/// старые документы один раз перечитываются и получают новый отпечаток.
/// </summary>
public static class ExtractionFingerprint
{
    /// <summary>Версия конвейера извлечения, участвующая в отпечатке.</summary>
    // 2: Fast с доступным OCR автоматически продолжается в Full, поэтому
    // доступность OCR и потолок страниц влияют и на быстрый прогон.
    // 3 (19.10): пустые страницы сохраняются заглушкой «Стр. N» (раньше
    // выпадали из сегментов и считались необработанными), поэтому старые
    // документы один раз перечитываются.
    public const int Version = 3;

    /// <summary>
    /// Отпечаток для запрошенных опций. <paramref name="pdfOcr"/> — OCR,
    /// которым будет читаться PDF (null — недоступен). В быстром режиме
    /// доступность OCR тоже входит в отпечаток: при доступном OCR такие
    /// страницы автоматически дочитываются в Full, при недоступном — нет.
    /// </summary>
    public static string For(DocumentKind kind, PdfReadOptions? pdfOptions, IPdfPageOcr? pdfOcr)
    {
        if (kind != DocumentKind.Pdf)
            return $"v{Version}|{kind}";

        var opts = pdfOptions ?? PdfReadOptions.Default;
        var ocrAvailable = pdfOcr is { IsAvailable: true };
        return $"v{Version}|pdf|{opts.Mode}|ocr={opts.EffectiveOcrMaxPages}|avail={(ocrAvailable ? 1 : 0)}";
    }

    /// <summary>
    /// Сохранённый прогон PDF прошёл в Full (текст + OCR): его сегменты —
    /// надмножество любого будущего Fast-прогона, поэтому Fast-импорт может
    /// переиспользовать их, не запуская OCR повторно (аудит 18.10: каждый
    /// повторный Fast-импорт большого PDF заново ждал OCR пустых страниц).
    /// </summary>
    public static bool IsFullPdfRun(string? savedFingerprint) =>
        savedFingerprint != null
        && savedFingerprint.Contains("|pdf|Full|", StringComparison.Ordinal);

    /// <summary>
    /// Full-прогон покрывает запрос Fast, только если он читал не меньше
    /// OCR-страниц: иначе повторный импорт с увеличенным лимитом молча
    /// унаследовал бы неполный текст (аудит 18.10: 12 страниц оставались
    /// нераспознанными после снятия потолка OCR). Отпечаток другой версии
    /// конвейера не переиспользуем — правила нарезки могли измениться.
    /// </summary>
    public static bool CoversFastRequest(string? savedFingerprint, PdfReadOptions requested)
    {
        var marker = $"v{Version}|pdf|Full|ocr=";
        if (savedFingerprint == null
            || !savedFingerprint.StartsWith(marker, StringComparison.Ordinal))
            return false;
        var end = savedFingerprint.IndexOf('|', marker.Length);
        if (end < 0) return false;
        return int.TryParse(savedFingerprint.AsSpan(marker.Length, end - marker.Length),
                   out var savedCap)
               && savedCap >= requested.EffectiveOcrMaxPages;
    }
}
