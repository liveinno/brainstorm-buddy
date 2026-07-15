using System.IO;
using System.Text;

namespace BrainstormBuddy.Services;

public class QaHistoryLogger
{
    private readonly object _lock = new();
    private readonly string _filePath;

    public QaHistoryLogger(string appDataDir)
    {
        _filePath = Path.Combine(appDataDir, "qa_history.txt");
        try
        {
            using var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var w = new StreamWriter(fs, new UTF8Encoding(true));
            w.WriteLine($"=== Q/A history session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            w.Flush();
        }
        catch
        {
        }
    }

    public string FilePath => _filePath;

    public void Log(string question, string answer, string status,
        double sttMs, double llmMs, double audioSec, string model = "")
    {
        try
        {
            lock (_lock)
            {
                using var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var w = new StreamWriter(fs, new UTF8Encoding(true));
                w.WriteLine();
                w.WriteLine($"--- [{DateTime.Now:HH:mm:ss.fff}] status={status} audio={audioSec:F1}s stt={sttMs/1000:F1}s llm={llmMs/1000:F1}s model={model} ---");
                w.WriteLine($"Q: {question}");
                w.WriteLine($"A: {answer}");
                w.Flush();
            }
        }
        catch
        {
        }
    }
}
