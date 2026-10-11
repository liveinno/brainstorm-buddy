using System.Text;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Ai;

/// <summary>
/// Чистит ответы моделей от протечки разметки функ-вызовов. Некоторые open-source модели
/// (DeepSeek-R1 и клоны) иногда сливают служебную DSML-разметку (&lt;|DSML|&gt;…&lt;/|DSML|&gt;,
/// &lt;|invoke|&gt;, &lt;|parameter|&gt;) прямо в текст ответа — юзер видит мусор.
/// Порядок: 1) всё от первого "&lt;|…DSML" до конца отрезаем; 2) построчно вырезаем
/// остаточные строки-маячки с этими тегами (строка без маркера не трогается — обычные
/// скобки/угловые в тексте живут); 3) страховка от утечки «мыслей» модели (баг 2.8.x
/// п.2): типовые CoT-преамбулы («Okay, the user wants…», «Let me think…», «Мне нужно…»)
/// режем до первого реального абзаца — ТОЛЬКО если после среза остаётся осмысленный
/// ответ; ответ-целиком-рассуждение не «чиним» и возвращаем как есть (решение о показе
/// остаётся у вызывающего); 4) пустой результат → null (вызывающий честно покажет
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

    // Голова абзаца, похожая на размышление модели вслух («Okay, so…», «The user wants…»,
    // «I need to…», «Мне нужно…», «Сначала подумаем…»). Список — как у эвристики
    // LooksLikeFinalAnswer в OpenAiClient; держим оба в одном месте синхронно.
    private static readonly Regex s_cotPreambleStart = new(
        @"^\s*(?:" +
        @"okay\b|ok\b|so[,\s]|let me|let's\b|the user|the (?:question|request|task|prompt|input|message|phrase|text)|" +
        @"i (?:need|have|must|should|will|am|will try|want)|first[,\s]|hmm+\b|wait\b|alright\b|well[,\s]|" +
        @"итак\b|хм+\b|думаю\b|мне нужно|сначала|давайте?|ну[,\s])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Абзацный разрыв: ≥2 перевода строки (учитываем CRLF и пробелы в пустой строке).
    private static readonly Regex s_paragraphBreak = new(
        @"\r?\n[ \t]*\r?\n", RegexOptions.Compiled);

    // Отрезанный кусок короче — не считаем его «достаточным» финальным ответом и
    // страхуемся от ложного среза.
    private const int MinTailLength = 10;

    /// <summary>debug — опциональный лог при срабатывании CoT-среза (диагностика).</summary>
    public static string? Sanitize(string? text, Action<string>? debug = null)
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
        if (text.Length == 0) return null;

        // Страховка от CoT-преамбулы (баг 2.8.x п.2): если ответ открывается
        // размышлением вслух («Okay, the user wants…», «Мне нужно…») и дальше есть
        // настоящий ответ — отрезаем ведущие абзацы-размышления. Если ВЕСЬ ответ —
        // сплошное рассуждение без финальной части, НЕ «чиним» и возвращаем как есть:
        // решение о показе принимает вызывающий (не утопить валидный ответ).
        var stripped = StripCotPreamble(text);
        if (stripped != text)
        {
            debug?.Invoke($"LlmAnswerSanitizer: срезана CoT-преамбула ({text.Length}→{stripped.Length} chars)");
            text = stripped;
        }
        return text;
    }

    // Срезает ВЕДУЩИЕ абзацы, открывающиеся CoT-маркером — итеративно (могут идти
    // подряд несколько «думающих» абзацев). Срез применяем только если хвост после
    // среза непуст, достаточной длины и сам не начинается с маркера. Иначе вернём
    // исходник: лучше не тронуть, чем съесть валидный ответ.
    private static string StripCotPreamble(string text)
    {
        var cur = text;
        for (var i = 0; i < 3; i++)
        {
            if (!s_cotPreambleStart.IsMatch(cur)) break;
            var br = s_paragraphBreak.Match(cur);
            if (!br.Success) return text;      // абзац один — весь ответ = рассуждение
            var rest = cur[(br.Index + br.Length)..].TrimStart();
            if (rest.Length < MinTailLength) return text; // срез съест весь ответ
            cur = rest;
        }
        // Страховка: срезанный остаток сам не должен выглядеть продолжением размышления.
        return cur != text && !s_cotPreambleStart.IsMatch(cur) ? cur : text;
    }

    // ── Литеральные «маркеры молчания» ──
    // Промпт суфлёра требует «молчи — пустой ответ», но модели вместо пустого
    // content шлют литералы: «(empty)», «пустой ответ», «[silent]», «(空)» и т.п.
    // Такие строки — НЕ ответ: вызывающий должен трактовать их как молчание
    // (IsSilent), иначе мусор попадает в пузырь и караоке.

    // Длина-кандидат: реальный ответ длиннее; CJK-маркер «(空)» — 3 символа,
    // настоящий CJK-ответ — обычно десятки. Длинные строки не глушим.
    private const int SilentMarkerMaxLen = 24;

    // Явные литералы (после trim+lower): фиксация живых наблюдений — deepseek
    // присылает «(空)», open-source клоны — «(empty)»/«(Пустой ответ.)».
    private static readonly HashSet<string> s_silentMarkers = new(StringComparer.Ordinal)
    {
        "(пустой ответ.)", "(пустой ответ)", "пустой ответ",
        "(empty)", "empty",
        "[silent]", "silent", "(silent)",
        "(空)", "空", "（空）", "(无)", "无", "（无）",
        "нет ответа", "(нет ответа)", "no answer", "(no answer)",
        "n/a", "(n/a)",
        "...", "…"
    };

    // Страховка поверх allowlist: та же фраза с произвольными скобками/точкой
    // («empty.», «[пустой ответ]», «( silent )»).
    private static readonly Regex s_silentMarkerRe = new(
        @"^[\(\[]?\s*(пустой\s*ответ|empty|silent|no\s*answer)\s*[\)\]]?\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// true, если строка — литеральный «маркер молчания» (модель записала словами
    /// то, что должна была вернуть пустым content). Проверка после trim+lower;
    /// кандидатом считаем только короткие строки (≤24 символов), чтобы не глушить
    /// реальные CJK-/длинные ответы.
    /// </summary>
    public static bool IsSilentMarkerAnswer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.Length > SilentMarkerMaxLen) return false;
        var lower = s.ToLowerInvariant();
        return s_silentMarkers.Contains(lower) || s_silentMarkerRe.IsMatch(lower);
    }
}
