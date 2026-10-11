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
    {
        var scopeLabel = (scopeName ?? DefaultScopeName)(hit.Item.ScopeType, hit.Item.ScopeId);
        return $"• [{hit.Item.Type}] {hit.Item.Content} (scope: {scopeLabel})";
    }

    public static string Build(List<MemoryRecallHit> hits, int maxTokens,
        ScopeNameResolver? scopeName = null)
    {
        if (hits == null || hits.Count == 0 || maxTokens <= 0) return string.Empty;

        var budgetChars = maxTokens * CharsPerToken;
        const string open = "<memory_context>\n";
        const string close = "\n</memory_context>";
        if (open.Length + close.Length > budgetChars) return string.Empty;

        var sb = new StringBuilder(open);
        int used = open.Length + close.Length;
        foreach (var hit in hits.OrderByDescending(h => h.Score))
        {
            var line = FormatLine(hit, scopeName);
            int cost = line.Length + 1;
            if (used + cost > budgetChars) break;
            sb.Append(line).Append('\n');
            used += cost;
        }
        if (sb.Length == open.Length) return string.Empty;
        sb.Length--;                               // последний '\n' станет частью close
        sb.Append(close);
        return sb.ToString();
    }
}
