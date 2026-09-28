using System.IO;
using System.IO.Compression;
using System.Text;
using BrainstormBuddy.Services;
using Xunit;

namespace BrainstormBuddy.Tests;

public class DiagnosticsServiceTests
{
    [Fact]
    public void RedactLogText_RemovesTranscriptAndAnswerContent()
    {
        var input = string.Join("\n", new[]
        {
            "[10:00:00.000] [Info ] [Worker    ] Ask done in 1200ms: 'Расскажите про ваш опыт работы с Kafka'",
            "[10:00:01.000] [Debug] [Audio     ] STT response received: 'Здравствуйте меня зовут Иван расскажите о себе'",
            "[10:00:02.000] [Info ] [UI        ] Overlay: expand answer for 'Я строил пайплайн на 50 тысяч событий…'",
        });

        var red = DiagnosticsService.RedactLogText(input);

        Assert.Contains("<redacted>", red);
        Assert.DoesNotContain("Kafka", red);
        Assert.DoesNotContain("Иван", red);
        Assert.DoesNotContain("пайплайн", red);
        // Метаданные (тайминги, категории) должны сохраниться для диагностики.
        Assert.Contains("Ask done in 1200ms", red);
        Assert.Contains("[Worker", red);
    }

    [Fact]
    public void RedactLogText_RemovesUsernameEmailAndTokens()
    {
        var input =
            @"[10:00:00.000] [Info ] [App       ] Path: C:\Users\Ivan\AppData\Roaming\BrainstormBuddy" + "\n" +
            "[10:00:00.001] [Info ] [Net       ] Auth: PRIVATE-TOKEN=glpat-abc123SECRETxyz user ivan.petrov@example.com";

        var red = DiagnosticsService.RedactLogText(input);

        Assert.Contains(@"C:\Users\<user>", red);
        Assert.DoesNotContain("Ivan", red);
        // Токен вырезан (тут даже сильнее — PRIVATE-TOKEN=… схлопывается в ***).
        Assert.DoesNotContain("glpat-abc123SECRETxyz", red);
        Assert.DoesNotContain("SECRET", red);
        Assert.Contains("***", red);
        Assert.DoesNotContain("ivan.petrov@example.com", red);
        Assert.Contains("<email>", red);
    }

    [Fact]
    public void RedactConfigJson_BlanksKeysButKeepsSettings()
    {
        var json = @"{
            ""Api"": { ""BaseUrl"": ""http://127.0.0.1:11434/v1"", ""ApiKey"": ""sk-supersecret12345"", ""ChatModel"": ""qwen2.5vl:7b"" },
            ""Audio"": { ""SttModelAuthValue"": ""glpat-tokenvalue999"", ""SampleRate"": 16000 }
        }";

        var red = DiagnosticsService.RedactConfigJson(json);

        Assert.DoesNotContain("sk-supersecret12345", red);
        Assert.DoesNotContain("glpat-tokenvalue999", red);
        Assert.Contains("***", red);
        // Небезопасные значения ушли, полезные настройки остались.
        Assert.Contains("qwen2.5vl:7b", red);
        Assert.Contains("16000", red);
        Assert.Contains("127.0.0.1", red);
    }

    [Fact]
    public void CreateSupportBundle_ExcludesQaHistory_AndRedactsLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "bb_diag_test_" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "appdata");
        var dest = Path.Combine(root, "docs");
        Directory.CreateDirectory(Path.Combine(appData, "logs"));
        Directory.CreateDirectory(dest);

        try
        {
            // Лог с PII
            File.WriteAllText(Path.Combine(appData, "logs", "app.log"),
                "[10:00:00.000] [Info ] [Worker    ] Ask done in 900ms: 'Секретный вопрос про архитектуру'\n", Encoding.UTF8);
            // qa_history.txt — ПЕРСОНАЛЬНЫЕ данные, НЕ должны попасть в архив
            File.WriteAllText(Path.Combine(appData, "qa_history.txt"), "Q: тайный вопрос\nA: тайный ответ\n", Encoding.UTF8);
            // config
            var configPath = Path.Combine(appData, "config.json");
            File.WriteAllText(configPath, @"{ ""Api"": { ""ApiKey"": ""sk-zzz9999secret"" } }", Encoding.UTF8);

            var zipPath = DiagnosticsService.CreateSupportBundle(dest, appData, configPath, "Активный STT-движок: native");

            Assert.True(File.Exists(zipPath));
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.Name).ToList();

            Assert.Contains("README.txt", names);
            Assert.Contains("config.redacted.json", names);
            Assert.Contains("system_info.txt", names);
            Assert.Contains("app.log", names);
            // Критично: истории Q/A в архиве нет.
            Assert.DoesNotContain("qa_history.txt", names);

            var logEntry = zip.GetEntry("app.log")!;
            using var reader = new StreamReader(logEntry.Open());
            var logText = reader.ReadToEnd();
            Assert.DoesNotContain("Секретный вопрос", logText);
            Assert.Contains("<redacted>", logText);

            var cfgEntry = zip.GetEntry("config.redacted.json")!;
            using var cfgReader = new StreamReader(cfgEntry.Open());
            Assert.DoesNotContain("sk-zzz9999secret", cfgReader.ReadToEnd());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
