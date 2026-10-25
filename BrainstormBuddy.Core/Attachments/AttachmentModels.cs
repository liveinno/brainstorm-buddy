namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Пометрика страниц документа после извлечения (JSON-колонка page_stats в
/// attachments.db). Для PDF: <paramref name="Total"/> — страниц в файле,
/// <paramref name="Processed"/> — доведённых до результата (текст или OCR),
/// <paramref name="TextLayer"/> — прочитанных текстовым слоем,
/// <paramref name="Ocr"/> — распознанных OCR (успех, не ошибка),
/// <paramref name="Empty"/> — пустых/пропущенных. Для нестраничных форматов —
/// нули; признак наличия данных — <paramref name="Total"/> &gt; 0.
/// </summary>
public sealed record DocumentPageStats(int Total, int Processed, int TextLayer, int Ocr, int Empty)
{
    /// <summary>Есть ли метрика (PDF-прогон записал page_stats).</summary>
    public bool HasData => Total > 0;
}

/// <summary>
/// Локальная копия прикреплённого документа (строка в attachments.db/documents +
/// файлы attachments/{DocumentId}/source{Extension} и segments.json).
/// Deleted=true → байты и связи стёрты, осталась только отметка «Файл удалён».
/// ExtractKey — отпечаток условий последнего ДОВЕДЁННОГО до конца извлечения
/// (<see cref="ExtractionFingerprint"/>): по нему повторный импорт того же
/// содержимого решает, переиспользовать ли сегменты. Null — прогон был
/// частичным/старым и перечитывается.
/// </summary>
public sealed record StoredDocument(
    string DocumentId, string DisplayName, string Extension, DocumentKind Kind,
    long SizeBytes, string Sha256, DateTime CreatedUtc,
    int CharCount, int PageCount, IReadOnlyList<string> Warnings,
    bool Deleted,
    DocumentPageStats? PageStats = null,
    string? ExtractKey = null);

/// <summary>Состояние связи встреча↔документ (на будущее: сейчас только Active).</summary>
public enum AttachmentLinkState { Active }

/// <summary>
/// Статус фонового задания извлечения фактов из документа (fact_jobs).
/// PendingReview — «Ожидают проверки: N», NoFacts — «Факты не найдены»,
/// Interrupted — процесс умер в Running/Queued (восстановление при старте).
/// </summary>
public enum FactJobStatus { Queued, Running, PendingReview, Done, NoFacts, Error, Canceled, Interrupted }

/// <summary>Связь документа с встречей журнала (MeetingTranscriptStore.CurrentId — НЕ mem_meetings.id).</summary>
public sealed record MeetingAttachment(StoredDocument Doc, string MeetingId, DateTime AddedUtc);

/// <summary>Служебные списки страниц для строк предупреждений/покрытия.</summary>
public static class PageListFormat
{
    /// <summary>«1-3, 7, 10-12» — компактная запись номеров 1-based, до 6 элементов.</summary>
    public static string CompactPages(IReadOnlyList<int> pages, int maxItems = 6)
    {
        if (pages.Count == 0) return string.Empty;
        var shown = pages.Take(maxItems).ToList();
        var parts = new List<string>();
        for (var i = 0; i < shown.Count;)
        {
            var start = shown[i];
            var end = start;
            while (i + 1 < shown.Count && shown[i + 1] == end + 1)
            {
                i++;
                end = shown[i];
            }
            parts.Add(start == end ? start.ToString() : $"{start}-{end}");
            i++;
        }
        var suffix = pages.Count > shown.Count ? ",…" : string.Empty;
        return string.Join(", ", parts) + suffix;
    }
}

/// <summary>
/// Persisted-задание извлечения фактов (fact_jobs). Record с init-полями —
/// обновление через <c>with</c>, запись в БД — <see cref="AttachmentStore.UpdateFactJob"/>.
/// Scope/VerifyMode/ProviderId/Model — снимок настроек памяти на момент Commit.
/// </summary>
public sealed record FactJob(
    long Id, string DocumentId, string MeetingId,
    FactJobStatus Status, int CharsSent, int RequestsPlanned, int RequestsDone,
    int FactsSaved, int FactsPending, string? Error,
    string ProviderId, string Model, string Scope, string VerifyMode, DateTime CreatedUtc);

/// <summary>Предпросмотр удаления документа: число связанных встреч и фактов, извлечённых ранее.</summary>
public sealed record DeletePreview(int LinkedMeetings, int FactsCount);
