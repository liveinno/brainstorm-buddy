using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Хранилище полных результатов инструментов вне контекста LLM (спека §6.3):
/// тяжёлый tools/call-ответ складывается целиком на диск (%AppData%\BrainstormBuddy\mcp\tool-results\),
/// в контекст уходит фрагмент + указатель {toolResultId, totalChars, hasMore}.
/// Чтение — страницами по offset/limitBytes с UTF-8 безопасным срезом.
/// Файлы: {id}.json (метаданные) + {id}.txt (полный текст). TTL — фоновая подчистка.
/// </summary>
public sealed class McpToolResultStore
{
    /// <summary>Размер страницы чтения по умолчанию (байты UTF-8).</summary>
    public const int DefaultPageBytes = 128 * 1024;
    /// <summary>Верхняя граница страницы.</summary>
    public const int MaxPageBytes = 2 * 1024 * 1024;
    /// <summary>Сколько держим результат на диске.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static long _sweep;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;

    public McpToolResultStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
        SweepExpired();
    }

    public string Dir => _dir;

    /// <summary>Каталог по умолчанию: {appDataDir}\mcp\tool-results.</summary>
    public static string DefaultDir(string appDataDir) =>
        Path.Combine(appDataDir, "mcp", "tool-results");

    /// <summary>Сохранить полный результат. serverTruncated — сам сервер урезал
    /// ответ до нас (loop это видит в метаданных страниц и называет причину).</summary>
    public McpToolResultStoreEntry Save(string text, string? toolName = null,
        string? jobId = null, bool serverTruncated = false)
    {
        var id = NewId();
        var entry = new McpToolResultStoreEntry(
            id, text.Length, toolName, jobId, DateTime.UtcNow, serverTruncated);
        var tmp = Path.Combine(_dir, id + ".tmp");
        File.WriteAllText(tmp, text); // UTF-8 без BOM — единый формат страниц
        File.Move(tmp, TextPath(id), overwrite: true);
        File.WriteAllText(MetaPath(id), JsonSerializer.Serialize(entry, JsonOpts));
        return entry;
    }

    /// <summary>Есть ли сохранённый результат с таким id.</summary>
    public bool Exists(string id) => ValidId(id) && File.Exists(TextPath(id));

    /// <summary>Метаданные результата или null.</summary>
    public McpToolResultStoreEntry? GetEntry(string id)
    {
        if (!ValidId(id)) return null;
        try
        {
            var meta = MetaPath(id);
            if (!File.Exists(meta)) return null;
            return JsonSerializer.Deserialize<McpToolResultStoreEntry>(File.ReadAllText(meta), JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Страница текста: offset — символы от начала, limitBytes — бюджет
    /// UTF-8 выдачи (срез не ломает многобайтовые символы). null — нет такого id.</summary>
    public McpToolResultPage? ReadPage(string id, long offset, int? limitBytes)
    {
        var entry = GetEntry(id);
        if (entry == null) return null;
        string text;
        try { text = File.ReadAllText(TextPath(id)); }
        catch { return null; }
        var budget = Math.Clamp(limitBytes ?? DefaultPageBytes, 1, MaxPageBytes);
        if (offset < 0) offset = 0;
        if (offset >= text.Length)
        {
            return new McpToolResultPage(id, "", offset, text.Length,
                HasMore: false, NextCursor: null, entry.ToolName, entry.ServerTruncated);
        }
        var (chunk, next) = SliceUtf8(text, (int)offset, budget);
        var hasMore = next < text.Length;
        return new McpToolResultPage(id, chunk, offset, text.Length, hasMore,
            hasMore ? McpSearchCursor.EncodeSource(next) : null,
            entry.ToolName, entry.ServerTruncated);
    }

    /// <summary>Срез [start, start+budgetBytes) по UTF-8 без разрыва кодпоинтов.
    /// Возвращает кусок и символьную позицию следующей страницы.</summary>
    internal static (string Chunk, int Next) SliceUtf8(string text, int start, int budgetBytes)
    {
        // Не начинаем посередине суррогатной пары.
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]))
            start++;
        // Быстрый путь: почти все тексты ASCII/кириллица — идём от границы байт назад.
        var end = Math.Min(text.Length, start + budgetBytes); // грубая верхняя граница в chars
        while (end > start && Encoding.UTF8.GetByteCount(text.AsSpan(start, end - start)) > budgetBytes)
            end--;
        if (end == start && text.Length > start)
        {
            // Один символ крупнее бюджета — отдаём его целиком (не разрываем).
            end = start + (char.IsHighSurrogate(text[start]) ? 2 : 1);
        }
        // Не режем между суррогатной парой.
        if (end < text.Length && end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;
        return (text[start..end], end);
    }

    private string TextPath(string id) => Path.Combine(_dir, id + ".txt");
    private string MetaPath(string id) => Path.Combine(_dir, id + ".json");

    private static bool ValidId(string id) =>
        !string.IsNullOrEmpty(id) && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && id is not "." and not "..";

    private static string NewId() =>
        "tr-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Чистка протухших файлов — не чаще раза в час на процесс.</summary>
    private void SweepExpired()
    {
        var now = DateTime.UtcNow.Ticks;
        var prev = Interlocked.Exchange(ref _sweep, now);
        if (now - prev < TimeSpan.TicksPerHour) return;
        try
        {
            var cutoff = DateTime.UtcNow - Ttl;
            foreach (var f in Directory.EnumerateFiles(_dir))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                catch { }
            }
        }
        catch { }
    }
}

/// <summary>Метаданные сохранённого результата (рядом с .txt лежит .json).</summary>
public sealed record McpToolResultStoreEntry(
    string Id,
    int TotalChars,
    string? ToolName,
    string? JobId,
    DateTime CreatedUtc,
    bool ServerTruncated);


/// <summary>
/// Непрозрачные курсоры чтения источника: "s{charOffset}" — позиция в символах.
/// Намеренно простой формат: отладка видна, валидация тривиальна, секретов нет.
/// </summary>
internal static class McpSearchCursor
{
    public static string EncodeSource(int charOffset) => "s" + charOffset;
    public static string EncodeSource(long charOffset) => "s" + charOffset;

    /// <summary>null — курсор не наш; плохой формат трактуем как «с начала».</summary>
    public static long? DecodeSource(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        var s = cursor.StartsWith('s') ? cursor[1..] : cursor;
        return long.TryParse(s, out var n) && n >= 0 ? n : 0;
    }
}