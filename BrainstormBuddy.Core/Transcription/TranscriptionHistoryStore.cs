using System.Text.Json;

namespace BrainstormBuddy.Transcription;

/// <summary>Одна запись истории транскрибаций (файл + текст + саммари).</summary>
public sealed class TranscriptionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public double DurationSeconds { get; set; }
    public string ExtractMethod { get; set; } = "";
    public string TimestampedText { get; set; } = "";
    public string PlainText { get; set; } = "";
    public string Summary { get; set; } = "";

    // ListBox без DisplayMemberPath берёт ToString.
    public override string ToString() => FileName;
}

/// <summary>
/// Хранилище истории транскрибаций: по одному .json-файлу на запись в
/// %APPDATA%\BrainstormBuddy\transcriptions. Простая файловая база — как чат-история.
/// </summary>
public sealed class TranscriptionHistoryStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _dir;

    public TranscriptionHistoryStore(string appDataDir)
    {
        _dir = Path.Combine(appDataDir, "transcriptions");
        System.IO.Directory.CreateDirectory(_dir);
    }

    public string Directory => _dir;

    /// <summary>Все записи, свежие сверху.</summary>
    public List<TranscriptionRecord> LoadAll()
    {
        var list = new List<TranscriptionRecord>();
        foreach (var f in System.IO.Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<TranscriptionRecord>(File.ReadAllText(f));
                if (r != null) list.Add(r);
            }
            catch { /* повреждённый файл истории — пропускаем */ }
        }
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public void Save(TranscriptionRecord r)
    {
        try { File.WriteAllText(Path.Combine(_dir, r.Id + ".json"), JsonSerializer.Serialize(r, Opts)); }
        catch { /* best-effort */ }
    }

    public void Delete(string id)
    {
        try { File.Delete(Path.Combine(_dir, id + ".json")); }
        catch { /* best-effort */ }
    }
}
