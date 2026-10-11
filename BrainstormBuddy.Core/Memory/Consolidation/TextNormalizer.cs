using System.Text;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Нормализация текста memory item для exact-дедупликации: lower + trim +
/// схлопывание пробельных серий + снятие финальной пунктуации.
/// Не используется для поиска — только как ключ сравнения «тот же факт».
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// "  Релиз  Перенесён  на  15 октября!!!  " → "релиз перенесён на 15 октября".
    /// Пустой/whitespace вход → "".
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var s = text.Trim().ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        var ws = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                ws = true;
                continue;
            }
            if (ws && sb.Length > 0) sb.Append(' ');
            sb.Append(c);
            ws = false;
        }

        // Финальная пунктуация не меняет смысла факта: снимаем хвост
        // (., !, ?, …, ;, :, кавычки, скобки и т.п.) до буквы/цифры.
        var end = sb.Length;
        while (end > 0 && char.IsPunctuation(sb[end - 1])) end--;
        if (end < sb.Length) sb.Length = end;
        return sb.ToString().TrimEnd();
    }
}
