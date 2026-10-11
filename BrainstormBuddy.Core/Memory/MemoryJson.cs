using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Единые JsonSerializerOptions для payload'ов memory-подсистемы (jobs, items):
/// camelCase, enum — строками (camelCase). PayloadJson живёт в SQLite-колонках —
/// пишется и читается только через эти опции.
/// </summary>
public static class MemoryJson
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Толерантное чтение payload'а: битый/пустой JSON (обрезан при записи, ручная
    /// правка БД) не должен валить job-пайплайн — возвращаем default.
    /// </summary>
    public static T? DeserializeTolerant<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Default); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return default; }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Default);
}
