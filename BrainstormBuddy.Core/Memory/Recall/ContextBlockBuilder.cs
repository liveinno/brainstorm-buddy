using System.Text;

namespace BrainstormBuddy.Memory.Recall;

/// <summary>
/// Собирает справочный блок &lt;memory_context&gt; для промпта ассистента.
/// Порядок — по Score desc; бюджет — maxTokens (грубая оценка ~len/4, т.е.
/// 4 символа на токен), рамка-обёртка тоже считается. Пустой hits → "".
/// </summary>
public static class ContextBlockBuilder
{
    internal const int CharsPerToken = 4;

    /// <summary>Псевдоним scope'а для подписи «(scope: name)». null → англ. имя типа (+#id).</summary>
    public delegate string? ScopeNameResolver(MemoryScopeType scope, long? scopeId);

    internal static string DefaultScopeName(MemoryScopeType scope, long? scopeId)
    {
        var name = scope switch
        {
            MemoryScopeType.Global => "global",
            MemoryScopeType.Project => "project",
            MemoryScopeType.Person => "person",
            MemoryScopeType.Meeting => "meeting",
            _ => scope.ToString().ToLowerInvariant(),
        };
        return scopeId is { } id && scope != MemoryScopeType.Global ? $"{name} #{id}" : name;
    }

    /// <summary>Строка bullet без учёта бюджета — переиспользуется UI/тестами.</summary>
    public static string FormatLine(MemoryRecallHit hit, ScopeNameResolver? scopeName = null)
        => $"• [{hit.Item.Type}] {hit.Item.Content}{FormatSuffix(hit, scopeName)}";

    /// <summary>Хвост строки: scope + происхождение. Происхождение обязательно —
    /// иначе модель цитирует факт из ответа ИИ как слова участника.</summary>
    private static string FormatSuffix(MemoryRecallHit hit, ScopeNameResolver? scopeName)
    {
        var scopeLabel = (scopeName ?? DefaultScopeName)(hit.Item.ScopeType, hit.Item.ScopeId);
        var it = hit.Item;
        var extra = new StringBuilder();
        extra.Append("source: ").Append(SourceLabel(it.Source));
        if (it.SourceMeetingId is { } mid) extra.Append("; meeting #").Append(mid);
        if (!string.IsNullOrWhiteSpace(it.Speaker)) extra.Append("; speaker ").Append(it.Speaker);
        if (!string.IsNullOrWhiteSpace(it.SourceDocName))
            extra.Append("; document \"").Append(it.SourceDocName).Append('"');
        return $" (scope: {scopeLabel}) ({extra})";
    }

    /// <summary>Короткая метка происхождения факта для промпта/поиска.</summary>
    public static string SourceLabel(string? source) => source switch
    {
        "user" => "user",
        "user-edited" => "user-edited",
        "extraction" => "extraction",
        // Эти факты извлечены из ответа ИИ/документа, а не сказаны участником.
        // Явная метка не даёт модели выдать их за цитату.
        "llm-directive" => "extraction (llm)",
        "attachment-directive" => "extraction (attachment)",
        "imported" => "imported",
        _ => string.IsNullOrWhiteSpace(source) ? "unknown" : source
    };

    /// <summary>Результат сборки блока: сам текст + реальная статистика объёма.
    /// Truncated=true → внутрь блока уже вписана строка-маркер «показано N из M»
    /// (§6.4 аудита: частичный контекст не должен выглядеть как «полный»).</summary>
    public sealed record ContextBlock(string Text, int Shown, int Total, bool Truncated,
        int Collapsed = 0, IReadOnlyList<long>? SelectedIds = null);

    public static string Build(List<MemoryRecallHit> hits, int maxTokens,
        ScopeNameResolver? scopeName = null)
        => BuildDetailed(hits, maxTokens, scopeName).Text;

    /// <summary>
    /// Собрать блок и вернуть статистику: сколько фактов реально вошло
    /// (Shown) из предложенных (Total). При усечении в конец блока
    /// добавляется маркер — модель и UI видят факт усечения и причину.
    /// </summary>
    public static ContextBlock BuildDetailed(List<MemoryRecallHit> hits, int maxTokens,
        ScopeNameResolver? scopeName = null)
    {
        var total = hits?.Count ?? 0;
        if (total == 0 || maxTokens <= 0)
            return new ContextBlock(string.Empty, 0, total, false);

        var budgetChars = maxTokens * CharsPerToken;
        const string open = "<memory_context>\n";
        const string close = "\n</memory_context>";
        if (open.Length + close.Length > budgetChars)
            return new ContextBlock(string.Empty, 0, total, total > 0);

        // Дубликаты похожих фактов не занимают бюджет: в памяти живут
        // парафразы одного факта (импорт, повторные извлечения), и модель
        // получала 8 почти одинаковых строк вместо покрытия темы.
        var ordered = hits!.OrderByDescending(h => h.Score).ToList();
        var kept = new List<MemoryRecallHit>(ordered.Count);
        var collapsed = 0;
        foreach (var hit in ordered)
        {
            if (kept.Any(k => IsNearDuplicate(k.Item.Content, hit.Item.Content)))
            {
                collapsed++;
                continue;
            }
            kept.Add(hit);
        }

        var sb = new StringBuilder(open);
        int used = open.Length + close.Length;
        int shown = 0;
        foreach (var hit in kept)
        {
            var line = FormatLine(hit, scopeName);
            int cost = line.Length + 1;
            if (used + cost > budgetChars)
            {
                // Крошечный бюджет: лучше один факт с обрезанным содержимым и
                // сохранённым происхождением, чем пустой блок памяти.
                if (shown > 0) break;
                var suffix = FormatSuffix(hit, scopeName);
                var prefix = $"• [{hit.Item.Type}] ";
                var room = budgetChars - used - 1 - prefix.Length - suffix.Length - 1;
                var content = hit.Item.Content;
                if (room < 8 || content.Length <= room) break;
                line = prefix + content[..room].TrimEnd() + "…" + suffix;
                cost = line.Length + 1;
            }
            sb.Append(line).Append('\n');
            used += cost;
            shown++;
        }
        // Усечение по бюджету — только невлезшие УНИКАЛЬНЫЕ факты; свёрнутые
        // дубли не признак нехватки места.
        var truncated = shown < kept.Count;
        if (truncated)
        {
            // Маркер усечения — последней строкой внутри блока; бюджет на него
            // резервируем задним числом (если не влез — жертвуем последним фактом).
            var marker = $"• [контекст усечён: показано {shown} из {total} фактов — " +
                         "не влезло в лимит токенов памяти; не выдавай усечённый список за полный]";
            while (shown > 0 && used + marker.Length + 1 > budgetChars)
            {
                // net8: у StringBuilder нет LastIndexOf — ищем '\n' вручную с конца.
                var lastNl = -1;
                for (var i = sb.Length - 2; i >= open.Length; i--)
                    if (sb[i] == '\n') { lastNl = i; break; }
                if (lastNl < 0) break;
                used -= sb.Length - lastNl;
                sb.Length = lastNl;
                shown--;
            }
            if (used + marker.Length + 1 <= budgetChars)
            {
                sb.Append(marker).Append('\n');
                used += marker.Length + 1;
            }
        }
        if (collapsed > 0)
        {
            var note = $"• [свёрнуто {collapsed} похожих фактов, чтобы не дублировать контекст]";
            if (used + note.Length + 1 <= budgetChars)
            {
                sb.Append(note).Append('\n');
                used += note.Length + 1;
            }
        }
        if (sb.Length == open.Length) return new ContextBlock(string.Empty, 0, total, truncated, collapsed);
        // shown — уникальные строки; collapsed отмечен отдельной строкой и НЕ
        // означает усечение по бюджету (иначе статус врал бы о нехватке места).
        sb.Length--;                               // последний '\n' станет частью close
        sb.Append(close);
        return new ContextBlock(sb.ToString(), shown, total, truncated, collapsed,
            kept.Take(shown).Select(h => h.Item.Id).ToList());
    }

    /// <summary>Почти одинаковый текст: та же нормализованная строка или
    /// Jaccard по словам ≥ 0.85 (минимум 4 слова, чтобы короткие факты
    /// «Иван отвечает за бэкенд» не схлопывались по двум общим словам).</summary>
    internal static bool IsNearDuplicate(string a, string b)
    {
        static string Norm(string s) => s.ToLowerInvariant();
        static string[] Toks(string s) => s.Split(
            new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')',
                    '«', '»', '"', '\'', '—', '–', '-', '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries);
        if (Norm(a) == Norm(b)) return true;
        var ta = Toks(a);
        var tb = Toks(b);
        if (ta.Length < 4 || tb.Length < 4) return false;
        var setB = new HashSet<string>(tb);
        int inter = ta.Distinct().Count(setB.Contains);
        int union = ta.Distinct().Count() + setB.Count - inter;
        return union > 0 && (double)inter / union >= 0.85;
    }
}
