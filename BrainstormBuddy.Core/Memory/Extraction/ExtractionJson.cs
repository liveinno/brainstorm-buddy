using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Memory.Extraction;

/// <summary>Один извлечённый memory-кандидат из JSON модели (DTO до маппинга в MemoryItem).</summary>
public sealed record ParsedMemory(
    string? Type,
    string? Content,
    double? Importance,
    double? Confidence,
    string? SpeakerId,
    long? SourceStartMs,
    long? SourceEndMs);

/// <summary>Результат разбора ответа memory-модели: summary + candidates или ошибка.</summary>
public sealed record ExtractionParseResult(
    string Summary,
    List<ParsedMemory> Memories,
    string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Строгий парсер ответа memory-модели: сначала чистый JSON по схеме
/// { "summary": "...", "memories": [...] }, при неудаче — один локальный repair-проход
/// (снять ```-обёртку, вырезать первый JSON-объект, убрать висячие запятые/комментарии).
/// Если и это не помогло — <see cref="ExtractionParseResult.Error"/>: тогда extractor
/// делает retry с <see cref="MemoryPrompts.Repair"/>.
/// </summary>
public static class ExtractionJson
{
    private static readonly JsonDocumentOptions StrictOpts = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    private static readonly JsonDocumentOptions LenientOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    // Висячая запятая перед } или ] — частая поломка LLM-JSON.
    private static readonly Regex TrailingComma = new(@",\s*([}\]])", RegexOptions.Compiled);

    /// <summary>Parse ответ модели. Никогда не бросает: ошибка → Error в результате.</summary>
    public static ExtractionParseResult Parse(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
            return new ExtractionParseResult("", new List<ParsedMemory>(), "пустой ответ memory-модели");

        var (summary, memories, error) = TryRead(modelText.Trim(), StrictOpts);
        if (error != null)
        {
            // Локальный repair: снять обёртку и распарсить мягче.
            var repaired = RepairText(modelText);
            if (!string.Equals(repaired, modelText.Trim(), StringComparison.Ordinal))
                (summary, memories, error) = TryRead(repaired, LenientOpts);
            if (error != null)
                (summary, memories, error) = TryRead(modelText.Trim(), LenientOpts);
        }
        if (error != null)
            return new ExtractionParseResult("", new List<ParsedMemory>(), error);

        return new ExtractionParseResult(summary ?? "", memories ?? new List<ParsedMemory>(), null);
    }

    /// <summary>Читает JSON по схеме; при любой поломке возвращает error (без исключений наружу).</summary>
    private static (string? summary, List<ParsedMemory>? memories, string? error)
        TryRead(string json, JsonDocumentOptions opts)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, opts);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null, "корень JSON не объект");

            var root = doc.RootElement;
            var summary = GetString(root, "summary") ?? "";

            var memories = new List<ParsedMemory>();
            var arr = GetProp(root, "memories");
            if (arr is { ValueKind: JsonValueKind.Array })
            {
                foreach (var el in arr.Value.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    memories.Add(new ParsedMemory(
                        Type: GetString(el, "type"),
                        Content: GetString(el, "content"),
                        Importance: GetDouble(el, "importance"),
                        Confidence: GetDouble(el, "confidence"),
                        SpeakerId: GetString(el, "speakerId"),
                        SourceStartMs: GetLong(el, "sourceStartMs"),
                        SourceEndMs: GetLong(el, "sourceEndMs")));
                }
            }
            return (summary, memories, null);
        }
        catch (JsonException ex)
        {
            return (null, null, $"невалидный JSON: {ex.Message}");
        }
    }

    /// <summary>Снимает ```-обёртку и вырезает первый {...}-объект из мусора вокруг.</summary>
    internal static string RepairText(string raw)
    {
        var s = raw.Trim();

        // ```json ... ``` или ``` ... ```
        var fence = Regex.Match(s, @"```(?:json|JSON)?\s*(?<b>[\s\S]*?)```");
        if (fence.Success)
            s = fence.Groups["b"].Value.Trim();

        // Вырезать сбалансированный первый объект { … } (учитываем строки/экранирование).
        int start = s.IndexOf('{');
        if (start < 0) return s;
        int depth = 0; bool inStr = false; bool esc = false;
        for (int i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (esc) { esc = false; continue; }
            if (inStr)
            {
                if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return TrailingComma.Replace(s[start..(i + 1)], "$1");
            }
        }
        // Объект не закрыт (ответ обрезали?) — берём хвост как есть, чистим запятые.
        return TrailingComma.Replace(s[start..], "$1");
    }

    private static JsonElement? GetProp(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        return null;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        var p = GetProp(obj, name);
        return p is { ValueKind: JsonValueKind.String } ? p.Value.GetString() : null;
    }

    private static double? GetDouble(JsonElement obj, string name)
    {
        var p = GetProp(obj, name);
        if (p is { ValueKind: JsonValueKind.Number } && p.Value.TryGetDouble(out var d)) return d;
        if (p is { ValueKind: JsonValueKind.String } &&
            double.TryParse(p.Value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
        return null;
    }

    private static long? GetLong(JsonElement obj, string name)
    {
        var p = GetProp(obj, name);
        if (p is { ValueKind: JsonValueKind.Number } && p.Value.TryGetInt64(out var l)) return l;
        if (p is { ValueKind: JsonValueKind.String } &&
            long.TryParse(p.Value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var ls)) return ls;
        return null;
    }
}
