using System.IO;
using System.Text.Json;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;
using Xunit;

namespace BrainstormBuddy.Tests;

public class KaraokeConfigTests
{
    [Fact]
    public void KaraokeConfig_Defaults()
    {
        var config = new KaraokeConfig();

        Assert.False(config.Enabled);
        Assert.Equal("Bottom", config.Position);
        Assert.Equal(60, config.WidthPct);
        Assert.Equal(0, config.HeightPct);
        Assert.Equal(26, config.FontSize);
        Assert.Equal("#FFFFFFFF", config.TextColor);
        Assert.Equal("#FF4ADE80", config.AccentColor);
        Assert.Equal("#FFFFE9A8", config.FreshColor);
        Assert.Equal("#FFFFFFFF", config.OldLinesColor);
        Assert.Equal("#FF0A1622", config.BackgroundColor);
        Assert.Equal(0.78, config.BackgroundOpacity);
        Assert.True(config.ShowBackground);
        Assert.True(config.TextShadow);
        Assert.Equal(0.9, config.Opacity);
        Assert.Equal(4, config.MaxLines);
        Assert.Equal(700, config.SlideDurationMs);
        Assert.Equal(600, config.FadeDurationMs);
        Assert.Equal(24, config.InnerPadding);
        Assert.Equal(30, config.MarginPx);
        Assert.Equal("Left", config.TextAlignment);
        Assert.True(config.ShowAnswers);
        Assert.False(config.ShowQuestions);
        Assert.False(config.ShowUserLines);
        Assert.True(config.DimByAge);
        Assert.True(config.AgeColorFade);
        Assert.True(config.ShowMarker);
        Assert.True(config.HiddenFromCapture);
        Assert.True(config.ClickThrough);
        Assert.True(config.WordWrap);
    }

    [Fact]
    public void HotkeysConfig_ToggleKaraoke_Default()
    {
        // Хоткей караоке живёт в общей секции Hotkeys — дефолт Ctrl+Shift+K
        // и сериализуется вместе с остальными.
        var config = new AppConfig();
        Assert.Equal("Ctrl+Shift+K", config.Hotkeys.ToggleKaraoke);

        var json = JsonSerializer.Serialize(config);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal("Ctrl+Shift+K", back.Hotkeys.ToggleKaraoke);
    }

    [Fact]
    public void HotkeysConfig_ShowTrayIcon_Default()
    {
        // Хоткей возврата иконки трея — дефолт Ctrl+Shift+T, не коллидирует
        // с H/O/S/P/K и зашитыми L/Y/C.
        var config = new AppConfig();
        Assert.Equal("Ctrl+Shift+T", config.Hotkeys.ShowTrayIcon);
        Assert.False(config.Ui.HideTrayIcon);

        var json = JsonSerializer.Serialize(config);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal("Ctrl+Shift+T", back.Hotkeys.ShowTrayIcon);
        Assert.False(back.Ui.HideTrayIcon);
    }


    [Fact]
    public void KaraokeConfig_SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsb_test_{Guid.NewGuid()}.json");
        try
        {
            var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
            var loader = new ConfigLoader(path, logger);
            var config = new AppConfig();
            config.Karaoke.Enabled = true;
            config.Karaoke.Position = "Top";
            config.Karaoke.FontSize = 32;
            config.Karaoke.AccentColor = "#FF00FF00";
            config.Karaoke.MaxLines = 6;
            config.Karaoke.ShowUserLines = true;
            config.Karaoke.HeightPct = 40;
            config.Karaoke.FreshColor = "#FF112233";
            config.Karaoke.AgeColorFade = false;
            config.Karaoke.TextShadow = false;
            loader.Save(config);

            var loaded = loader.Load();
            Assert.True(loaded.Karaoke.Enabled);
            Assert.Equal("Top", loaded.Karaoke.Position);
            Assert.Equal(32, loaded.Karaoke.FontSize);
            Assert.Equal("#FF00FF00", loaded.Karaoke.AccentColor);
            Assert.Equal(6, loaded.Karaoke.MaxLines);
            Assert.True(loaded.Karaoke.ShowUserLines);
            Assert.Equal(40, loaded.Karaoke.HeightPct);
            Assert.Equal("#FF112233", loaded.Karaoke.FreshColor);
            Assert.False(loaded.Karaoke.AgeColorFade);
            Assert.False(loaded.Karaoke.TextShadow);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var bak = path + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
    }

    [Fact]
    public void KaraokeConfig_UnknownFields_DoNotBreak()
    {
        var json = """
        {
          "karaoke": {
            "Enabled": true,
            "Position": "Left",
            "UnknownNewField": 12345,
            "AnotherUnknown": { "nested": true }
          },
          "UnknownTopLevel": "value"
        }
        """;

        // ConfigLoader читает с PropertyNameCaseInsensitive — старые конфиги с
        // lowercase "karaoke" (до 2.6.0-финал) обязаны загружаться.
        var config = JsonSerializer.Deserialize<AppConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        Assert.True(config!.Karaoke.Enabled);
        Assert.Equal("Left", config.Karaoke.Position);
    }

    [Fact]
    public void KaraokeConfig_OldConfig_WithoutSection_GetsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bsb_test_{Guid.NewGuid()}.json");
        var json = """
        {
          "Api": {
            "BaseUrl": "https://api.example.com/v1",
            "ApiKey": "old-key"
          },
          "Ui": {
            "WindowOpacity": 0.7
          }
        }
        """;
        File.WriteAllText(path, json);

        try
        {
            var logger = new LoggingService(Path.Combine(Path.GetTempPath(), "bsb_test"));
            var loader = new ConfigLoader(path, logger);
            var config = loader.Load();

            Assert.NotNull(config.Karaoke);
            Assert.False(config.Karaoke.Enabled);
            Assert.Equal("Bottom", config.Karaoke.Position);
            Assert.Equal(60, config.Karaoke.WidthPct);
            Assert.Equal(26, config.Karaoke.FontSize);
            Assert.Equal(4, config.Karaoke.MaxLines);
            Assert.True(config.Karaoke.ClickThrough);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
