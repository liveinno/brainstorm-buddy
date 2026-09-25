using BrainstormBuddy.Ai;
using Xunit;

namespace BrainstormBuddy.Tests;

public class LlmAnswerSanitizerTests
{
    [Fact]
    public void Sanitize_DsmlOnlyReply_ReturnsNull()
    {
        var text = "<|DSML|> <invoke name=\"respond\"><parameter name=\"text\">hi</parameter></invoke> </|DSML|>";
        Assert.Null(LlmAnswerSanitizer.Sanitize(text));
    }

    [Fact]
    public void Sanitize_TruncatesFromFirstDsmlMarker()
    {
        var text = "Результат: 42.\n<|DSML|> <invoke name=\"x\"/> мусор до конца";
        Assert.Equal("Результат: 42.", LlmAnswerSanitizer.Sanitize(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_NullOrEmpty_PassesThrough(string? input) =>
        Assert.Equal(input, LlmAnswerSanitizer.Sanitize(input));

    [Fact]
    public void Sanitize_PlainTextWithAngleBrackets_NotTouched()
    {
        var text = "Обычный ответ с <скобками> и 2 < 3.";
        Assert.Equal(text, LlmAnswerSanitizer.Sanitize(text));
    }

    [Fact]
    public void Sanitize_MarkerInsideLine_TagRemovedLineKept()
    {
        var text = "Ответ с <|invoke|>-тегом в строке — режем тег, строку не трогаем.";
        Assert.Equal("Ответ с -тегом в строке — режем тег, строку не трогаем.",
            LlmAnswerSanitizer.Sanitize(text));
    }

    [Fact]
    public void Sanitize_FullwidthPipeMarker_AlsoCaught()
    {
        var text = "Ответ. <｜DSML｜>junk";
        Assert.Equal("Ответ.", LlmAnswerSanitizer.Sanitize(text));
    }
}
