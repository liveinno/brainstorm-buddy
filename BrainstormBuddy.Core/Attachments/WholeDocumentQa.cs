using System.Text;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>Прогресс map-reduce по документу: <paramref name="Stage"/> —
/// "map"/"reduce", <paramref name="Done"/> из <paramref name="Total"/> шагов.</summary>
public readonly record struct WholeDocProgress(string Stage, int Done, int Total, string RangeLabel);

/// <summary>
/// Результат полного прохода по документу. <paramref name="Digest"/> — сжатый
/// конспект всех страниц. <paramref name="UncoveredRanges"/> — диапазоны,
/// до которых LLM не доехал (ошибка/отмена): перечислены честно, не замалчиваются.
/// <paramref name="Reduced"/> — финальная сводка сделана; false → Digest содержит
/// склейку частичных конспектов (canceled/ошибка reduce).
/// </summary>
public sealed record WholeDocResult(
    string Digest,
    int MappedParts,
    int TotalParts,
    IReadOnlyList<PageInterval> UncoveredRanges,
    bool Reduced,
    bool Canceled);

/// <summary>
/// Реальный поэтапный map-reduce по всем сегментам документа для вопросов
/// «по документу целиком»: ни одна страница не подменяется выборкой — каждый
/// блок уходит в map-вызов, частичные конспекты сводятся иерархически.
/// Вызовы LLM приходят через делегат <paramref name="callAsync"/> (App подставляет
/// ApiClient), поэтому движок остаётся в Core и тестируется без сети.
/// Бюджеты — в СИМВОЛАХ (токены → символы решает вызывающий по
/// <c>ContextBudgetResolver.EstimateTokens</c>).
/// </summary>
public static class WholeDocumentQa
{
    /// <summary>Минимальный полезный объём map-части (меньше — прогон бессмысленно дробный).</summary>
    public const int MinPartChars = 4000;

    /// <summary>
    /// <paramref name="callAsync"/>(userText, maxTokens, ct) — один обычный
    /// текстовый запрос к LLM; вернуть null при отказе → блок уйдёт в uncovered.
    /// </paramref>
    /// <paramref name="maxInputChars"/> — максимум символов в одном map/reduce
    /// вызове (input-бюджет минус служебные части, в символах).
    /// <paramref name="mapMaxTokens"/> — потолок ответа для map/reduce вызовов.
    /// </summary>
    public static async Task<WholeDocResult> RunAsync(
        string question,
        string displayName,
        IReadOnlyList<DocSegment> segments,
        int maxInputChars,
        int mapMaxTokens,
        Func<string, int, CancellationToken, Task<string?>> callAsync,
        IProgress<WholeDocProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callAsync);
        var parts = BuildParts(segments, maxInputChars);
        if (parts.Count == 0)
            return new WholeDocResult("", 0, 0, [], Reduced: true, Canceled: false);

        // Документ помещается в один вызов — отдаём исходный текст без
        // map/reduce: лишние LLM-вызовы небольшим файлам не нужны, ответ
        // формирует обычный чат (там же кнопка «Остановить»).
        if (parts.Count == 1)
            return new WholeDocResult(parts[0].Text.Trim(), 0, 1, [], Reduced: true, Canceled: false);

        // Оценка total для прогресса: map-частей + примерное число reduce-вызовов.
        var estReduceCalls = EstimateReduceCalls(parts.Count);
        var total = parts.Count + estReduceCalls;
        var done = 0;

        var partials = new List<string>(parts.Count);
        var uncovered = new List<PageInterval>();
        var canceled = false;

        foreach (var part in parts)
        {
            if (ct.IsCancellationRequested) { canceled = true; break; }
            var rangeLabel = part.RangeLabel;
            // Прогресс ДО вызова: кнопка «Остановить» должна появиться ещё во
            // время первого (потенциально долгого) map-запроса.
            progress?.Report(new WholeDocProgress("map", done, total, rangeLabel));
            try
            {
                var note = await callAsync(MapPrompt(question, part.Text, rangeLabel, displayName),
                    mapMaxTokens, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(note))
                {
                    // Пустой ответ — одна повторная попытка, потом честный пробел.
                    note = await callAsync(MapPrompt(question, part.Text, rangeLabel, displayName),
                        mapMaxTokens, ct).ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(note))
                    uncovered.AddRange(part.Pages);
                else
                    partials.Add($"[стр. {rangeLabel}]\n{note.Trim()}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                canceled = true;
                uncovered.AddRange(part.Pages);
                break;
            }
            catch (Exception)
            {
                // Сетевые повторы принадлежат API-клиенту. Если делегат всё же
                // завершился ошибкой, этот диапазон остаётся непокрытым, а
                // остальные части продолжают обработку.
                uncovered.AddRange(part.Pages);
            }
            done++;
            progress?.Report(new WholeDocProgress("map", done, total, rangeLabel));
        }

        var joined = JoinPartials(partials);
        var digest = joined;
        var reduced = false;

        if (canceled)
        {
            // Отмена: непройденные части — честный пробел покрытия; финальный
            // ответ App сложит из склейки частичных конспектов + списка диапазонов.
            uncovered.AddRange(parts.Skip(done).SelectMany(pr => pr.Pages));
            return new WholeDocResult(digest, partials.Count, parts.Count,
                AttachmentQueryRouter.Merge(uncovered), false, true);
        }

        // Все map-вызовы упали/вернули пусто: сводить нечего, но покрытие
        // честно перечислено в UncoveredRanges.
        if (partials.Count == 0)
            return new WholeDocResult("", 0, parts.Count,
                AttachmentQueryRouter.Merge(uncovered), false, false);

        // Иерархический reduce: пока конспект не влезает в один вызов —
        // сводим группами; последняя сводка отвечает на вопрос целиком.
        var current = partials;
        var depth = 0;
        while (!ct.IsCancellationRequested)
        {
            var groups = GroupByChars(current, maxInputChars);
            if (groups.Count == 1)
            {
                // Reduce анонсируем до вызова, иначе на маленьком документе
                // UI не успевает показать этап «свожу».
                progress?.Report(new WholeDocProgress("reduce", done, total, ""));
                try
                {
                    var final = await callAsync(
                        ReducePrompt(question, groups[0], displayName, final: true),
                        mapMaxTokens, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(final))
                    {
                        digest = final.Trim();
                        reduced = true;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    canceled = true;
                }
                catch (Exception) { /* сводка упала — отдаём склейку частичных, Reduced=false */ }
                done++;
                progress?.Report(new WholeDocProgress("reduce", done, total, ""));
                break;
            }

            var next = new List<string>(groups.Count);
            foreach (var group in groups)
            {
                if (ct.IsCancellationRequested) { canceled = true; break; }
                progress?.Report(new WholeDocProgress("reduce", done, total, $"уровень {depth + 1}"));
                try
                {
                    var merged = await callAsync(
                        ReducePrompt(question, group, displayName, final: false),
                        mapMaxTokens, ct).ConfigureAwait(false);
                    next.Add(string.IsNullOrWhiteSpace(merged) ? group : merged.Trim());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }
                catch (Exception)
                {
                    next.Add(group); // группа не свернулась — остаётся как есть
                }
                done++;
                progress?.Report(new WholeDocProgress("reduce", done, total, $"уровень {depth + 1}"));
            }
            if (canceled) break;
            current = next;
            depth++;
            if (depth >= 8) break; // защита от зацикливания: фан-ин ≥6 гарантирует сходимость
        }

        digest = reduced ? digest : JoinPartials(current);
        return new WholeDocResult(digest, partials.Count, parts.Count,
            AttachmentQueryRouter.Merge(uncovered), reduced, canceled);
    }

    // ---------- разбиение на части ----------

    private sealed record Part(string Text, List<PageInterval> Pages, string RangeLabel);

    private static List<Part> BuildParts(IReadOnlyList<DocSegment> segments, int maxChars)
    {
        var parts = new List<Part>();
        var sb = new StringBuilder(Math.Min(maxChars, 1 << 20));
        var pages = new List<PageInterval>();

        void Flush()
        {
            var text = sb.ToString().Trim();
            if (text.Length == 0) { sb.Clear(); pages.Clear(); return; }
            parts.Add(new Part(text, AttachmentQueryRouter.Merge(pages),
                LabelOf(pages)));
            sb.Clear();
            pages.Clear();
        }

        foreach (var seg in segments)
        {
            // Заглушка пустой страницы — служебная отметка, а не содержание
            // документа: в map-reduce её не отправляем.
            if (seg.Anchor.Kind == AttachmentPageMarkers.EmptyKind) continue;
            var text = seg.Text.TrimEnd('\n');
            if (text.Length == 0) continue;
            // Один сегмент больше бюджета — отдельная часть целиком
            // (LLM решит обрезку лучше, чем разрыв страницы посередине).
            if (sb.Length > 0 && sb.Length + text.Length + 1 > maxChars)
                Flush();
            if (text.Length > maxChars)
            {
                Flush();
                var singlePages = PagesOf(seg);
                parts.Add(new Part(text, singlePages, LabelOf(singlePages)));
                continue;
            }
            sb.Append(text).Append('\n');
            pages.AddRange(PagesOf(seg));
        }
        Flush();
        return parts;
    }

    private static List<PageInterval> PagesOf(DocSegment seg)
    {
        var m = Regex.Match(seg.Anchor.Label, @"(?<!\d)(\d{1,6})(?!\d)");
        return int.TryParse(m.Value, out var p) && p > 0
            ? [new PageInterval(p, p)] : [];
    }

    private static string LabelOf(List<PageInterval> pages) =>
        pages.Count == 0 ? "фрагмент" : string.Join(", ", pages.Select(p => p.ToString()));

    private static string JoinPartials(List<string> partials) =>
        string.Join("\n\n", partials);

    private static List<string> GroupByChars(List<string> items, int maxChars)
    {
        var groups = new List<string>();
        var sb = new StringBuilder(Math.Min(maxChars, 1 << 20));
        foreach (var item in items)
        {
            if (sb.Length > 0 && sb.Length + item.Length + 2 > maxChars)
            {
                groups.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(item);
        }
        if (sb.Length > 0) groups.Add(sb.ToString());
        return groups;
    }

    /// <summary>Грубая оценка числа reduce-вызовов для total прогресса
    /// (конспекты короче исходников — оценка сверху).</summary>
    private static int EstimateReduceCalls(int partCount)
    {
        var calls = 0;
        var n = partCount;
        while (n > 1) { n = (n + 5) / 6; calls++; }
        return calls;
    }

    // ---------- промпты ----------

    private static string MapPrompt(string question, string text, string rangeLabel, string docName) =>
        "Ниже — фрагмент документа «" + docName + "» (страницы: " + rangeLabel + ").\n"
        + "Вопрос пользователя: " + question + "\n"
        + "Выдели из фрагмента факты, нужные для ответа на вопрос. Если вопрос общий —\n"
        + "плотный конспект содержания. Каждый пункт отмечай номером страницы в скобках.\n"
        + "Не выдумывай — только то, что есть во фрагменте. Без вступлений и пояснений.\n\n"
        + "=== ФРАГМЕНТ (стр. " + rangeLabel + ") ===\n" + text;

    private static string ReducePrompt(string question, string notes, string docName, bool final) =>
        final
            ? "Ниже — конспекты всех фрагментов документа «" + docName + "» (помечены страницами).\n"
              + "Вопрос пользователя: " + question + "\n"
              + "Собери из конспектов связный черновик ответа: факты с номерами страниц,\n"
              + "без повторов. Пробелы в данных не скрывай. Без вступлений.\n\n=== КОНСПЕКТЫ ===\n" + notes
            : "Ниже — частичные конспекты документа «" + docName + "».\n"
              + "Вопрос пользователя: " + question + "\n"
              + "Объедини конспекты в один плотный, сохрани факты и номера страниц,\n"
              + "убери дубли. Не отвечай на вопрос — только своди заметки.\n\n=== КОНСПЕКТЫ ===\n" + notes;
}
