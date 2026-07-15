using BrainstormBuddy.Config;
using Xunit;

namespace BrainstormBuddy.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var config = new AppConfig
        {
            Api = new ApiConfig { BaseUrl = "https://test.example.com/v1", ApiKey = "k", ChatModel = "m", SttModel = "w" }
        };
        var vm = new SettingsViewModel(config);
        Assert.NotNull(vm);
        Assert.NotEmpty(vm.Presets);
        // На Linux AudioDevices будет пустым — это ожидаемо
        Assert.NotNull(vm.AudioDevices);
    }

    [Fact]
    public void SelectedPreset_AppliesGroqValues()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config);
        vm.SelectedPreset = vm.Presets[0]; // Groq

        Assert.Equal("https://api.groq.com/openai/v1", vm.BaseUrl);
        Assert.Equal("llama3-8b-8192", vm.ChatModel);
        // Было "t-one" — dev-остаток, вычищенный миграцией конфига ещё в 2.5.x;
        // пресеты давно ставят стандартный whisper-1, тест отставал от кода.
        Assert.Equal("whisper-1", vm.SttModel);
    }

    [Fact]
    public void DetectPreset_RecognizesGroqUrl()
    {
        var config = new AppConfig
        {
            Api = new ApiConfig { BaseUrl = "https://api.groq.com/openai/v1" }
        };
        var vm = new SettingsViewModel(config);
        Assert.Equal(vm.Presets[0], vm.SelectedPreset);
    }
}
