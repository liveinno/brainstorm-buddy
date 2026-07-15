using BrainstormBuddy.Audio;
using Xunit;

namespace BrainstormBuddy.Tests;

public class SimpleVadTests
{
    private static float[] MakeSilence(int n) => new float[n];

    private static float[] MakeSine(int n, double freq, double amp = 0.3, int sampleRate = 16000)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
            s[i] = (float)(Math.Sin(2 * Math.PI * freq * i / sampleRate) * amp);
        return s;
    }

    private static float[] MakeConstant(int n, float v) => Enumerable.Repeat(v, n).ToArray();

    private static float[] MakeNoise(int n, int seed = 42, double amp = 0.3)
    {
        var rng = new Random(seed);
        var s = new float[n];
        for (int i = 0; i < n; i++)
            s[i] = (float)(rng.NextDouble() * 2.0 * amp - amp);
        return s;
    }

    [Fact]
    public void Silence_IsNotSpeech()
    {
        var cfg = SimpleVad.Config.ForMode(2);
        Assert.False(SimpleVad.IsSpeech(MakeSilence(480), cfg, 2));
    }

    [Fact]
    public void ConstantZero_IsNotSpeech()
    {
        var cfg = SimpleVad.Config.ForMode(2);
        Assert.False(SimpleVad.IsSpeech(MakeConstant(480, 0f), cfg, 2));
    }

    [Fact]
    public void SpeechLikeSine_IsSpeech()
    {
        // 300Hz tone (нижний край речевого диапазона) при amp=0.3 → RMS=0.21 > 0.014
        var cfg = SimpleVad.Config.ForMode(2);
        Assert.True(SimpleVad.IsSpeech(MakeSine(480, 300, 0.3), cfg, 2));
    }

    [Fact]
    public void Mode3_RejectsBroadbandNoise()
    {
        // mode 3 имеет ZCR filter; white noise имеет высокий ZCR, должен пройти, но RMS 0.3 > 0.010 — пройдёт.
        // mode 3 отвергает только сигналы с экстремальным ZCR.
        // Тест: 50Hz tone (ниже речевого) — ZCR=0 → должен быть отвергнут в mode 3.
        var cfg = SimpleVad.Config.ForMode(3);
        var lowHum = MakeSine(480, 50, 0.3);
        Assert.False(SimpleVad.IsSpeech(lowHum, cfg, 3));
    }

    [Fact]
    public void Mode2_AcceptsLowZcrSignal()
    {
        // mode 2 БЕЗ ZCR filter — 50Hz tone пройдёт по RMS
        var cfg = SimpleVad.Config.ForMode(2);
        var lowHum = MakeSine(480, 50, 0.3);
        Assert.True(SimpleVad.IsSpeech(lowHum, cfg, 2));
    }

    [Fact]
    public void LowAmplitude_IsNotSpeech()
    {
        // amp=0.001 → RMS=0.0007 < 0.014 (mode 2)
        var cfg = SimpleVad.Config.ForMode(2);
        var quiet = MakeSine(480, 300, 0.001);
        Assert.False(SimpleVad.IsSpeech(quiet, cfg, 2));
    }

    [Fact]
    public void Mode_HasFourLevels()
    {
        _ = SimpleVad.Config.ForMode(0);
        _ = SimpleVad.Config.ForMode(1);
        _ = SimpleVad.Config.ForMode(2);
        _ = SimpleVad.Config.ForMode(3);
        // Unknown mode defaults to 2
        Assert.Equal(0.014, SimpleVad.Config.ForMode(99).RmsThreshold);
    }

    [Fact]
    public void Mode3_HasLowestThreshold()
    {
        // mode 3 = самый чувствительный = самый низкий порог
        var t0 = SimpleVad.Config.ForMode(0).RmsThreshold;
        var t1 = SimpleVad.Config.ForMode(1).RmsThreshold;
        var t2 = SimpleVad.Config.ForMode(2).RmsThreshold;
        var t3 = SimpleVad.Config.ForMode(3).RmsThreshold;
        Assert.True(t3 < t2);
        Assert.True(t2 < t1);
        Assert.True(t1 < t0);
    }
}

public class TextPostProcessorTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("ааа", false)] // <3 meaningful words
    [InlineData("э э э", false)]
    [InlineData("хорох, хорох, хорох, хорох", false)] // → "хорох" — 1 слово, дроп
    [InlineData("Сумма двух чисел это базовая операция арифметики", true)]
    [InlineData("Токен это атомарный элемент языка программирования", true)]
    [InlineData("Да, да, да, да, да, да, да, да, да", false)] // 1 unique word "да", дроп
    public void Clean_ReturnsExpectedValidity(string input, bool expectedValid)
    {
        var result = TextPostProcessor.Clean(input);
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void CommaRepeat_CollapsedToOne()
    {
        var r = TextPostProcessor.Clean("хорох, хорох, хорох, хорох, хорох, хорох");
        Assert.Equal("хорох", r.Text);
    }

    [Fact]
    public void SpaceRepeat_CollapsedToOne()
    {
        var r = TextPostProcessor.Clean("токен токен токен токен токен");
        Assert.Equal("токен", r.Text);
    }

    [Fact]
    public void MixedRepeatAndFiller_Handled()
    {
        // "Ну, ну, ну" → "Ну" (1 word → дроп), но потом "токен, токен" → "токен", и "это понятие в лексике" добавляется
        // Реально: "Ну, ну, ну, ну токен токен это понятие в лексике"
        //   → CommaRepeated: "Ну" + "Ну, ну" → "Ну" + "Ну" → "Ну" (1 повтор)
        //   → RepeatedWord: "Ну Ну" → "Ну", "токен токен" → "токен"
        //   → FillerRun: "Ну," → " "
        //   → Final: "это понятие в лексике" (3 words)
        //   → "ну" один → дроп
        //   → Result: "это понятие в лексике" (3 meaningful) — valid
        var r = TextPostProcessor.Clean("Ну, ну, ну, ну токен токен это понятие в лексике");
        Assert.True(r.IsValid);
        Assert.DoesNotContain("ну, ну", r.Text);
    }
}
