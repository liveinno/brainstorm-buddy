using System.IO;
using BrainstormBuddy.Services;
using Xunit;

namespace BrainstormBuddy.Tests;

public class LoggingServiceTests
{
    [Fact]
    public void Log_RaisesEvent()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bsb_log_test_" + Guid.NewGuid());
        try
        {
            using var logger = new LoggingService(tmp);
            LogEvent? captured = null;
            logger.LogAppended += (s, e) => captured = e;

            logger.Info("hello", "Test");

            Assert.NotNull(captured);
            Assert.Equal("hello", captured!.Message);
            Assert.Equal("Test", captured.Category);
            Assert.Equal(LogLevel.Info, captured.Level);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void GetRecent_ReturnsLatestEvents()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bsb_log_test_" + Guid.NewGuid());
        try
        {
            using var logger = new LoggingService(tmp);
            for (int i = 0; i < 10; i++)
                logger.Info($"msg {i}", "Test");

            var recent = logger.GetRecent(5);
            Assert.Equal(5, recent.Count);
            // последние 5 (хвост): msg 5, msg 6, msg 7, msg 8, msg 9
            Assert.Equal("msg 5", recent[0].Message);
            Assert.Equal("msg 9", recent[4].Message);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void GetRecent_RingBufferWrapsAround()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bsb_log_test_" + Guid.NewGuid());
        try
        {
            using var logger = new LoggingService(tmp);
            // Записать больше, чем ёмкость ring buffer (500)
            for (int i = 0; i < 600; i++)
                logger.Info($"msg {i}", "Test");

            var recent = logger.GetRecent();
            // ring capacity = 500
            Assert.Equal(500, recent.Count);
            // Самые старые из 500 — это msg 100..599
            Assert.Equal("msg 100", recent[0].Message);
            Assert.Equal("msg 599", recent[499].Message);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Log_WritesToFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bsb_log_test_" + Guid.NewGuid());
        try
        {
            using (var logger = new LoggingService(tmp))
            {
                logger.Info("file-test-msg", "Test");
            }
            var logFile = Path.Combine(tmp, "logs", "app.log");
            Assert.True(File.Exists(logFile));
            var content = File.ReadAllText(logFile);
            Assert.Contains("file-test-msg", content);
            Assert.Contains("Test", content);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Log_FormatsLineCorrectly()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bsb_log_test_" + Guid.NewGuid());
        try
        {
            using var logger = new LoggingService(tmp);
            logger.Warn("warn-msg", "MyCat");

            var recent = logger.GetRecent();
            Assert.Single(recent);
            var line = recent[0].FormattedLine;
            // Формат: [HH:mm:ss.fff] [Level] [Category] Message
            Assert.Contains("Warn", line);
            Assert.Contains("MyCat", line);
            Assert.Contains("warn-msg", line);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }
}
