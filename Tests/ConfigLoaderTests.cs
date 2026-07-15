using System.IO;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;
using Xunit;

namespace BrainstormBuddy.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void LoadValidConfig_Success()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsb_test_{Guid.NewGuid()}.json");
        var json = """
        {
          "Api": {
            "BaseUrl": "https://api.example.com/v1",
            "ApiKey": "test-key",
            "ChatModel": "gpt-4o-mini",
            "SttModel": "whisper-1"
          },
          "Audio": {
            "SampleRate": 16000,
            "RmsThreshold": 0.025
          }
        }
        """;
        File.WriteAllText(path, json);

        try
        {
            var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
            var loader = new ConfigLoader(path, logger);
            var config = loader.Load();

            Assert.Equal("https://api.example.com/v1", config.Api.BaseUrl);
            Assert.Equal("test-key", config.Api.ApiKey);
            Assert.Equal("gpt-4o-mini", config.Api.ChatModel);
            Assert.Equal(16000, config.Audio.SampleRate);
            Assert.Equal(0.025, config.Audio.RmsThreshold);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadInvalidConfig_UsesDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsb_test_{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{ not valid json :::");

        try
        {
            var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
            var loader = new ConfigLoader(path, logger);
            var config = loader.Load();

            Assert.Equal("http://127.0.0.1:11434/v1", config.Api.BaseUrl);
            Assert.Equal(16000, config.Audio.SampleRate);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadMissingConfig_CreatesDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsb_test_{Guid.NewGuid()}.json");
        try
        {
            var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
            var loader = new ConfigLoader(path, logger);
            var config = loader.Load();

            Assert.NotNull(config);
            Assert.Equal(0.90, config.Ui.WindowOpacity);
            Assert.Equal(ConfigLoader.CurrentSchemaVersion, config.SchemaVersion);
            Assert.True(File.Exists(path), "Default config file should be created");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MigratesOldConfig_CleansDevSttAndDedups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsb_test_{Guid.NewGuid()}.json");
        var json = """
        {
          "SchemaVersion": 0,
          "Api": { "SttBaseUrl": "http://192.168.0.10:2701/v1", "SttModel": "t-one" },
          "Advanced": {
            "ActiveSystemPromptName": "Несуществующий",
            "SystemPromptPresets": [
              { "Name": "Собеседование", "Content": "a" },
              { "Name": "Собеседование", "Content": "b" },
              { "Name": "Продажи", "Content": "c" }
            ]
          },
          "MultiAgent": { "UserProfile": { "Summary": "тест" } }
        }
        """;
        try
        {
            File.WriteAllText(path, json);
            var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
            var config = new ConfigLoader(path, logger).Load();

            // dev-остатки STT вычищены
            Assert.Equal(string.Empty, config.Api.SttBaseUrl);
            Assert.NotEqual("t-one", config.Api.SttModel);
            // точные дубли пресетов схлопнуты (2× «Собеседование» → 1)
            Assert.Equal(2, config.Advanced.SystemPromptPresets.Count);
            // висячий активный пресет сброшен
            Assert.Equal(string.Empty, config.Advanced.ActiveSystemPromptName);
            // мульти-агент выключен по умолчанию у всех (требование владельца продукта)
            Assert.False(config.MultiAgent.Enabled);
            // версия схемы поднята
            Assert.Equal(ConfigLoader.CurrentSchemaVersion, config.SchemaVersion);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsb_test_{Guid.NewGuid()}.json");
        try
        {
            var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
            var loader = new ConfigLoader(path, logger);
            var config = new AppConfig();
            config.Api.ApiKey = "round-trip-key";
            loader.Save(config);

            var loaded = loader.Load();
            Assert.Equal("round-trip-key", loaded.Api.ApiKey);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var bak = path + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
    }
}
