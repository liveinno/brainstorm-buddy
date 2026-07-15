using BrainstormBuddy.Audio;
using Xunit;

namespace BrainstormBuddy.Tests;

public class SentenceCompletenessTests
{
    [Theory]
    [InlineData("расскажите про ваш прошлый проект", false)] // законченная
    [InlineData("я работал в компании и", true)]             // висит союз «и»
    [InlineData("мы использовали этот инструмент для", true)] // висит предлог «для»
    [InlineData("это была сложная задача но", true)]          // висит «но»
    [InlineData("потому что", true)]                          // висит «что»
    [InlineData("мой опыт пять лет в разработке", false)]     // законченная
    [InlineData("я думаю что", true)]                         // висит «что»
    public void LooksIncomplete_DetectsDanglingTail(string text, bool expected)
        => Assert.Equal(expected, SentenceCompleteness.LooksIncomplete(text));

    [Theory]
    [InlineData("как вы тестируете код", true)]
    [InlineData("сколько у вас опыта", true)]
    [InlineData("расскажите о себе", true)]
    [InlineData("почему вы ушли", true)]
    [InlineData("я работал пять лет", false)]
    [InlineData("меня зовут иван", false)]
    public void LooksLikeQuestion_ByLeadingWord(string text, bool expected)
        => Assert.Equal(expected, SentenceCompleteness.LooksLikeQuestion(text));
}

public class TurnAggregatorTests
{
    [Fact]
    public void CompleteThought_PassesThroughImmediately()
    {
        var agg = new TurnAggregator();
        var outText = agg.Push("расскажите про ваш проект");
        Assert.Equal("расскажите про ваш проект", outText);
        Assert.False(agg.HasPending);
    }

    [Fact]
    public void IncompleteThought_IsHeldThenMerged()
    {
        var agg = new TurnAggregator();
        Assert.Null(agg.Push("я работал в компании и"));   // висит «и» → удержано
        Assert.True(agg.HasPending);
        var outText = agg.Push("занимался бэкендом");        // продолжение → отдаём склейку
        Assert.Equal("я работал в компании и занимался бэкендом", outText);
        Assert.False(agg.HasPending);
    }

    [Fact]
    public void MaxHold_ForcesFlush_NoInfiniteHold()
    {
        var agg = new TurnAggregator(maxHold: 2);
        Assert.Null(agg.Push("значит так во-первых и"));     // удержано (1)
        // второй незаконченный фрагмент достигает maxHold=2 → форс-выдача, не залипаем
        var outText = agg.Push("во-вторых а");
        Assert.NotNull(outText);
        Assert.Contains("во-первых", outText);
        Assert.Contains("во-вторых", outText);
        Assert.False(agg.HasPending);
    }

    [Fact]
    public void Flush_ReturnsPending()
    {
        var agg = new TurnAggregator();
        agg.Push("мы обсудили это и");     // удержано
        var outText = agg.Flush();
        Assert.Equal("мы обсудили это и", outText);
        Assert.Null(agg.Flush());          // пусто после сброса
    }

    [Fact]
    public void MaxHoldChars_ForcesFlush()
    {
        var agg = new TurnAggregator(maxHold: 100, maxHoldChars: 40);
        var outText = agg.Push("это очень длинный незаконченный фрагмент речи который висит на и");
        Assert.NotNull(outText); // длина > 40 → форс, несмотря на висящий союз
    }
}
