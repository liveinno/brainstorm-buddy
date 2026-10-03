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

    // Спека 2.7.2: лимит токенов ответа зажат в 50..4096 — UI не должен уметь
    // сохранить значение ниже 50 (clamp в сеттере, поле MaxTokensBox коммитит сюда).
    [Theory]
    [InlineData(30, 50)]
    [InlineData(0, 50)]
    [InlineData(-5, 50)]
    [InlineData(50, 50)]
    [InlineData(800, 800)]
    [InlineData(99999, 4096)]
    public void MaxResponseTokens_ClampedTo50_4096(int input, int expected)
    {
        var vm = new SettingsViewModel(new AppConfig());
        vm.MaxResponseTokens = input;
        Assert.Equal(expected, vm.MaxResponseTokens);
        Assert.Equal(expected, vm.Config.Advanced.MaxResponseTokens);
    }
}
