using BrainstormBuddy.Audio;
using Xunit;

namespace BrainstormBuddy.Tests;

public class ResamplerTests
{
    [Fact]
    public void ResampleLinear_ResamplesCorrectly()
    {
        var input = new float[48000];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
        var output = Resampler.ResampleLinear(input, 48000, 1, 16000);

        // 48000 -> 16000, должен получиться массив длиной 16000
        Assert.Equal(16000, output.Length);
    }

    [Fact]
    public void DownmixToMono_AveragesChannels()
    {
        var input = new float[] { 1f, 3f, 5f, 7f }; // 2 канала
        var output = Resampler.DownmixToMono(input, 2);
        Assert.Equal(2, output.Length);
        Assert.Equal(2f, output[0]);
        Assert.Equal(6f, output[1]);
    }

    [Fact]
    public void CalculateRms_OfSineWave()
    {
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * i / 100);
        var rms = Resampler.CalculateRms(samples);
        // RMS чистой синусоиды амплитуды 1 = 1/sqrt(2) ≈ 0.7071
        Assert.InRange(rms, 0.65f, 0.75f);
    }

    [Fact]
    public void Mix_ClipsAtOne()
    {
        var a = new float[] { 1f, 1f };
        var b = new float[] { 1f, 1f };
        var mixed = Resampler.Mix(a, b, 1.0f, 1.0f);
        // 1.0 * 1.0 + 1.0 * 1.0 = 2.0 -> клиппинг до 1.0
        Assert.Equal(1f, mixed[0]);
        Assert.Equal(1f, mixed[1]);
    }
}
