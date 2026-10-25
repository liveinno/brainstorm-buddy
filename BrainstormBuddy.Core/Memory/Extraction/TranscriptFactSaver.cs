namespace BrainstormBuddy.Memory.Extraction;

/// <summary>Итог сохранения фактов транскрипта: сколько добавлено и сколько пропущено как дубли.</summary>
public sealed record TranscriptSaveResult(int Added, int SkippedDuplicates, IReadOnlyList<string> Failed);

/// <summary>
/// Идемпотентное сохранение директив транскрипта в memory-стор:
/// - применяются только MEMORY_ADD (текст встречи не может удалять/менять чужую память);
/// - дубли определяются по нормализованному тексту среди уже активных фактов
///   (повторное сохранение тех же кандидатов не плодит записи);
/// - записи привязываются к встрече-источнику транскрипта, а не к случайно активной.
/// </summary>
public static class TranscriptFactSaver
{
    public static async Task<TranscriptSaveResult> SaveAsync(
        IMemoryStore store, IReadOnlyList<MemoryDirective> directives,
        long? meetingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(directives);

        var writer = new MemoryWriter(store);
        var existing = ExistingFactTexts(store);
        var added = 0;
        var dup = 0;
        var failed = new List<string>();

        foreach (var d in directives)
        {
            ct.ThrowIfCancellationRequested();
            if (d.Kind != MemoryDirectiveKind.Add) { dup++; continue; }
            var norm = TranscriptFactExtractor.NormalizeFact(d.Old);
            if (norm.Length == 0) { dup++; continue; }
            if (!existing.Add(norm)) { dup++; continue; }

            var applied = await writer.ApplyAsync(new[] { d }, meetingId, ct).ConfigureAwait(false);
            if (applied.Count > 0) added++;
            else { dup++; existing.Remove(norm); }   // writer отклонил (напр. обрезка до пустоты)
        }

        return new TranscriptSaveResult(added, dup, failed);
    }

    // Тексты уже живых фактов (Active + Pending) для дедупа. Падающий стор не должен
    // блокировать сохранение - тогда просто без пред-дедупа (writer всё равно запишет).
    private static HashSet<string> ExistingFactTexts(IMemoryStore store)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var it in store.ListItems(status: MemoryItemStatus.Active, limit: 5000))
                AddNormalized(set, it.Content);
            foreach (var it in store.ListItems(status: MemoryItemStatus.Pending, limit: 5000))
                AddNormalized(set, it.Content);
        }
        catch { /* стор без поддержки выборки - сохраняем без пред-фильтра */ }
        return set;
    }

    private static void AddNormalized(HashSet<string> set, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        set.Add(TranscriptFactExtractor.NormalizeFact(content));
    }
}
