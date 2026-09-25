using System.Text;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Ai;

/// <summary>
/// Чистит ответы моделей от протечки разметки функ-вызовов. Некоторые open-source модели
/// (DeepSeek-R1 и клоны) иногда сливают служебную DSML-разметку (&lt;|DSML|&gt;…&lt;/|DSML|&gt;,
/// &lt;|invoke|&gt;, &lt;|parameter|&gt;) прямо в текст ответа — юзер видит мусор.
/// Порядок: 1) всё от первого "&lt;|…DSML" до конца отрезаем; 2) построчно вырезаем
/// остаточные строки-маячки с этими тегами (строка без маркера не трогается — обычные
/// скобки/угловые в тексте живут); 3) пустой результат → null (вызывающий честно покажет
/// «модель вернула пустой ответ» вместо обрезанного мусора).
/// </summary>
public static class LlmAnswerSanitizer
{
    // Маркер начала DSML-блока: <|DSML, <||DSML, <｜DSML (｜ — U+FF5C FULLWIDTH VERTICAL LINE).
    private static readonly Regex s_dsmlStart = new(
        @"<[|｜]{1,3}\s*DSML", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Строка-маячок: тег вида <|DSML|>, <|invoke|>, </|parameter|>, <|string|…> —
    // с опциональным слешем закрытия (</|name|> или <|/name|>).
    private static readonly Regex s_markerTag = new(
        @"</?[|｜]+\s*/?\s*(?:DSML|invoke|parameter|string)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var start = s_dsmlStart.Match(text);
        if (start.Success) text = text[..start.Index];

        if (s_markerTag.IsMatch(text))
        {
            // Построчно: выкидываем только строки, которые ЦЕЛИКОМ состояли из маркеров
            // (после вырезания тегов ничего не осталось). Строки с текстом — чистим на месте,
            // обычные пустые строки сохраняем (абзацы ответа не склеиваем).
            var sb = new StringBuilder(text.Length);
            foreach (var raw in text.Split('\n'))
            {
                var line = s_markerTag.Replace(raw, "");
                if (line.Trim().Length == 0 && s_markerTag.IsMatch(raw)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line.TrimEnd('\r'));
            }
            text = sb.ToString();
        }

        text = text.Trim();
        return text.Length == 0 ? null : text;
    }
}
