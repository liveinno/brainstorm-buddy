using System.Globalization;
using System.Text;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Единая нормализация извлечённого текста для всех экстракторов:
/// неразрывные и экзотические пробелы/табуляция → обычный пробел; переносы строк → '\n';
/// прочие управляющие символы выбрасываются; повторные пробелы схлопываются;
/// строки подрезаются по краям; не более двух пустых строк подряд.
/// Нужна, чтобы контекст LLM не тащил типографский мусор и чтобы текст
/// из разных форматов выглядел одинаково.
/// </summary>
public static class TextSanitizer
{
    /// <summary>Нормализует текст целиком; пустой/whitespace-вход → "".</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        var spacePending = false;  // пробел откладывается до следующего значимого символа —
                                   // бесплатно даёт trim конца строки и схлопывание пробелов
        var lineHasContent = false;
        var emittedLines = 0;
        var blankRun = 0;          // подряд выведенных пустых строк
        var skipLf = false;        // '\n' после '\r' проглатываем

        void EmitNewline()
        {
            spacePending = false;
            if (lineHasContent)
            {
                sb.Append('\n');
                lineHasContent = false;
                emittedLines++;
                blankRun = 0;
            }
            else if (emittedLines > 0 && blankRun < 2)
            {
                // пустые строки внутри текста сохраняем, но не больше двух подряд;
                // пустые строки в самом начале текста отбрасываем
                sb.Append('\n');
                blankRun++;
            }
        }

        foreach (var raw in text)
        {
            if (raw == '\r')
            {
                skipLf = true;
                EmitNewline();
                continue;
            }
            if (raw == '\n')
            {
                if (skipLf) { skipLf = false; continue; }
                EmitNewline();
                continue;
            }
            skipLf = false;

            var cat = char.GetUnicodeCategory(raw);
            char ch;
            // табы и все невидимые пробельные/форматные знаки (NBSP, ZWSP, BOM и т.п.) → пробел
            if (raw is '\t' or '\v' or '\f'
                || (raw != ' ' && cat is UnicodeCategory.SpaceSeparator or UnicodeCategory.Format))
            {
                ch = ' ';
            }
            else if (char.IsControl(raw))
            {
                continue;   // прочий контрольный мусор (NUL, ESC и т.п.) выбрасываем
            }
            else
            {
                ch = raw;
            }

            if (ch == ' ')
            {
                if (lineHasContent && !spacePending)
                    spacePending = true;
                continue;
            }

            if (spacePending) { sb.Append(' '); spacePending = false; }
            sb.Append(ch);
            lineHasContent = true;
        }

        return sb.ToString().TrimEnd('\n', ' ');
    }
}
