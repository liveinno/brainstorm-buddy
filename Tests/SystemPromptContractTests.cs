using System.Linq;
using BrainstormBuddy.Config;
using Xunit;

namespace BrainstormBuddy.Tests;

// Контракт промптов: ассистент работает на КАНДИДАТА/пользователя и НИКОГДА не выводит
// рассуждения/CoT/мета-комментарии — только готовый текст ответа.
public class SystemPromptContractTests
{
    [Fact]
    public void DefaultSystemPrompt_IsCandidateSide_AndBansReasoning()
    {
        var p = new AdvancedConfig().SystemPrompt;

        // Кандидат, а не интервьюер: ответ — готовая реплика кандидата.
        Assert.Contains("кандидат", p);
        Assert.Contains("произнести", p);
        // Явный запрет CoT/мета.
        Assert.Contains("ЗАПРЕЩЕНО", p);
        Assert.Contains("рассуждать вслух", p);
        // Старый дефолт помогал интервьюеру оценивать кандидата — регрессия-ловушка.
        Assert.DoesNotContain("оценить кандидата", p);
        Assert.DoesNotContain("шпаргалки", p);
    }

    [Fact]
    public void AllPromptPresets_BanReasoning_AndMetaInOutput()
    {
        var config = new AppConfig();
        _ = new SettingsViewModel(config); // заполняет SystemPromptPresets дефолтными

        Assert.NotEmpty(config.Advanced.SystemPromptPresets);
        foreach (var preset in config.Advanced.SystemPromptPresets)
        {
            var ok = preset.Content.Contains("НИКОГДА не показывай", StringComparison.Ordinal) ||
                     preset.Content.Contains("never show reasoning", StringComparison.OrdinalIgnoreCase);
            Assert.True(ok, $"Preset '{preset.Name}' lacks the no-reasoning output rule");
        }
    }

    [Fact]
    public void InterviewPreset_HelpsCandidate_NotInterviewer()
    {
        var config = new AppConfig();
        _ = new SettingsViewModel(config);
        var preset = config.Advanced.SystemPromptPresets
            .First(x => x.Name == "Собеседование: шпаргалка");

        // Пресет — суфлёр КАНДИДАТА: готовый ответ от первого лица.
        Assert.Contains("КАНДИДАТ", preset.Content);
        // Интервьюер описан как собеседник [Динамик], а не как тот, кому мы подсказываем.
        Assert.Contains("[Динамик] — интервьюер", preset.Content);
        Assert.DoesNotContain("оценить кандидата", preset.Content);
        Assert.DoesNotContain("интервьюеру мгновенные шпаргалки", preset.Content);
    }
}
