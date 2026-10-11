using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Memory;

/// <summary>Вид memory-директивы в ответе LLM.</summary>
public enum MemoryDirectiveKind { Add, Update, Forget }

/// <summary>
/// Одна директива памяти из ответа LLM:
/// [[MEMORY_ADD]]текст[[/MEMORY_ADD]],
/// [[MEMORY_UPDATE]]старый текст→новый текст[[/MEMORY_UPDATE]],
/// [[MEMORY_FORGET]]текст[[/MEMORY_FORGET]].
/// </summary>
public sealed record MemoryDirective(MemoryDirectiveKind Kind, string Old, string? New);

/// <summary>
/// Парсер inline memory-директив в ответе LLM. Теги регистронезависимы,
/// пробелы/переводы строк внутри тега допускаются. Стрелка UPDATE — «→» или «-&gt;».
/// Битый/незакрытый тег — обычный текст: парсер не теряет хвост ответа.
/// </summary>
public static class MemoryDirectiveParser
{
    // Открывающий и закрывающий теги: двойные скобки по спецификации, одинарные
    // принимаем тоже — модели регулярно теряют одну пару (бэклог №64). Закрывающий
    // тег может опускать имя: [[/MEMORY]]; если имя есть — должно совпадать
    // с открывающим (backreference \k<kind>).
    private static readonly Regex DirectiveRx = new(
        @"\[\[?\s*MEMORY_(?<kind>ADD|UPDATE|FORGET)\s*\]\]?(?<body>.*?)\[\[?\s*/\s*MEMORY(_\k<kind>)?\s*\]\]?",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Директива, занимающая строку целиком (возможно, с отступами): удаляем строку
    // полностью — иначе на её месте остаётся лишний пустой абзац.
    private static readonly Regex FullLineDirectiveRx = new(
        @"[^\S\n]*\[\[?\s*MEMORY_(?<kind>ADD|UPDATE|FORGET)\s*\]\]?.*?\[\[?\s*/\s*MEMORY(_\k<kind>)?\s*\]\]?[^\S\n]*(?:\n|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BlankLinesRx = new(@"\n{3,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Множественные пробелы внутри строки — след от вырезанной mid-line директивы.
    private static readonly Regex MultiSpaceRx = new(@"[ \t]{2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ```ограждение``` целиком — кандидат для JSON-фолбэка (модель заворачивает
    // машинный ответ в markdown-блок).
    private static readonly Regex FenceRx = new(
        @"```[a-zA-Z0-9]*\s*\n?(?<body>.*?)```",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly char[] JsonOpen = { '{', '[' };
    private static readonly char[] JsonClose = { '}', ']' };

    // Ключи JSON-объектов, строковые значения которых — тексты фактов
    // (частые самоназвания у моделей: fact/facts/memory/item/…).
    private static readonly HashSet<string> FactKeys = new(StringComparer.OrdinalIgnoreCase)
        { "fact", "facts", "text", "content", "memory", "remember", "item", "value" };

    /// <summary>Разобрать все директивы в тексте, в порядке появления.
    /// Директив не нашлось → фолбэк: факты из JSON-ответа модели (№64).</summary>
    public static IReadOnlyList<MemoryDirective> Parse(string text)
    {
        var result = new List<MemoryDirective>();
        if (string.IsNullOrEmpty(text))
            return result;

        foreach (Match m in DirectiveRx.Matches(text))
        {
            var kind = m.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "ADD" => MemoryDirectiveKind.Add,
                "UPDATE" => MemoryDirectiveKind.Update,
                "FORGET" => MemoryDirectiveKind.Forget,
                _ => (MemoryDirectiveKind?)null
            };
            if (kind == null)
                continue;

            var body = m.Groups["body"].Value;
            switch (kind.Value)
            {
                case MemoryDirectiveKind.Add:
                case MemoryDirectiveKind.Forget:
                {
                    var content = body.Trim();
                    if (content.Length > 0)
                        result.Add(new MemoryDirective(kind.Value, content, null));
                    break;
                }
                case MemoryDirectiveKind.Update:
                {
                    var (oldText, newText) = SplitUpdateBody(body);
                    if (oldText.Length > 0)
                        result.Add(new MemoryDirective(MemoryDirectiveKind.Update, oldText,
                            newText.Length > 0 ? newText : null));
                    break;
                }
            }
        }
        if (result.Count == 0)
            CollectJsonFallback(text, result);
        return result;
    }

    /// <summary>
    /// Фолбэк для моделей, которые отвечают JSON вместо директив (бэклог №64):
    /// [{"fact":"…"}] / {"facts":[…]} / ["…","…"] → MEMORY_ADD по одному факту.
    /// Директивы в тексте имеют приоритет — фолбэк срабатывает только при их полном
    /// отсутствии. Битый/не-JSON вход молча игнорируется.
    /// </summary>
    private static void CollectJsonFallback(string text, List<MemoryDirective> result)
    {
        foreach (var candidate in JsonCandidates(text))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(candidate); }
            catch (JsonException) { }
            if (doc == null) continue;
            try { CollectJsonFacts(doc.RootElement, result, factContainer: true); }
            finally { doc.Dispose(); }
            if (result.Count > 0) return;
        }
    }

    // Варианты JSON-пейлоада: весь текст, тела ```блоков```, срез от первого
    // «{»/«[» до последнего «}»/«]» (проза вокруг JSON).
    private static IEnumerable<string> JsonCandidates(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0) yield return trimmed;
        foreach (Match m in FenceRx.Matches(text))
        {
            var body = m.Groups["body"].Value.Trim();
            if (body.Length > 0) yield return body;
        }
        var first = trimmed.IndexOfAny(JsonOpen);
        var last = trimmed.LastIndexOfAny(JsonClose);
        if (first >= 0 && last > first)
            yield return trimmed[first..(last + 1)];
    }

    // Строки — факты только из «фактовых» ключей и элементов массивов: иначе метаданные
    // ({\"model\":…}) превращались бы в факты.
    private static void CollectJsonFacts(JsonElement el, List<MemoryDirective> result, bool factContainer)
    {
        if (result.Count >= 64) return; // страховка от бесконечных списков
        switch (el.ValueKind)
        {
            case JsonValueKind.String when factContainer:
            {
                var s = el.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s))
                    result.Add(new MemoryDirective(MemoryDirectiveKind.Add, s, null));
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    CollectJsonFacts(item, result, factContainer: true);
                break;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    var isFactKey = FactKeys.Contains(prop.Name);
                    switch (prop.Value.ValueKind)
                    {
                        case JsonValueKind.String when isFactKey:
                        case JsonValueKind.Array when isFactKey:
                            CollectJsonFacts(prop.Value, result, factContainer: true);
                            break;
                        case JsonValueKind.Object:
                            CollectJsonFacts(prop.Value, result, factContainer: false);
                            break;
                        case JsonValueKind.Array:
                            CollectJsonFacts(prop.Value, result, factContainer: false);
                            break;
                    }
                }
                break;
        }
    }

    /// <summary>Убрать все директивы из видимого текста и подчистить пробелы.</summary>
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var visible = FullLineDirectiveRx.Replace(text, "");
        visible = DirectiveRx.Replace(visible, " ");
        var sb = new StringBuilder(visible.Length);
        foreach (var rawLine in visible.Split('\n'))
        {
            var line = MultiSpaceRx.Replace(rawLine.Trim(), " ");
            if (line.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            else
            {
                // Пустая строка — разделитель абзацев (здесь остаются и
                // «дырки» от вырезанных директив). Схлопываем ниже.
                sb.Append('\n');
            }
        }
        return BlankLinesRx.Replace(sb.ToString().Trim(), "\n\n");
    }

    /// <summary>UPDATE: тело = «старое→новое» («-&gt;» тоже принимается). Берём самую раннюю стрелку.</summary>
    private static (string OldText, string NewText) SplitUpdateBody(string body)
    {
        var uniIdx = body.IndexOf('→');
        var asciiIdx = body.IndexOf("->", StringComparison.Ordinal);
        int idx;
        int arrowLen;
        if (uniIdx >= 0 && (asciiIdx < 0 || uniIdx < asciiIdx))
        {
            idx = uniIdx; arrowLen = 1;
        }
        else if (asciiIdx >= 0)
        {
            idx = asciiIdx; arrowLen = 2;
        }
        else
        {
            return (body.Trim(), string.Empty); // стрелки нет — весь текст «старый», New=null
        }
        return (body[..idx].Trim(), body[(idx + arrowLen)..].Trim());
    }
}
