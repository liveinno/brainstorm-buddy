namespace BrainstormBuddy.Audio;

public class Resampler
{
    public static float[] ResampleLinear(float[] input, int inputRate, int inputChannels, int outputRate)
    {
        if (inputRate == outputRate && inputChannels == 1)
            return input;

        var mono = DownmixToMono(input, inputChannels);
        if (inputRate == outputRate)
            return mono;

        if (outputRate < inputRate)
            mono = LowPass17Tap(mono);

        double ratio = (double)outputRate / inputRate;
        int outputLength = (int)(mono.Length * ratio);
        var output = new float[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double sourceIndex = i / ratio;
            int i0 = (int)sourceIndex;
            int i1 = Math.Min(i0 + 1, mono.Length - 1);
            double t = sourceIndex - i0;
            output[i] = (float)(mono[i0] * (1 - t) + mono[i1] * t);
        }

        return output;
    }

    // 17-tap Hamming-windowed sinc lowpass.
    // Cutoff = 7200 Hz (@ 48kHz input): passes speech (0-4kHz) cleanly,
    // attenuates >8kHz by 50dB+ to prevent aliasing when decimating to 16kHz.
    private static readonly float[] _antiAliasKernel = BuildKernel();

    private static float[] BuildKernel()
    {
        const double cutoff = 0.15;  // 7200/48000
        const int taps = 17;
        const int half = taps / 2;
        var k = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            double n = i - half;
            double sinc;
            if (Math.Abs(n) < 1e-9)
                sinc = 2.0 * cutoff;
            else
                sinc = Math.Sin(2.0 * Math.PI * cutoff * n) / (Math.PI * n);
            double hamming = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (taps - 1));
            k[i] = (float)(sinc * hamming);
        }
        return k;
    }

    private static float[] LowPass17Tap(float[] input)
    {
        var output = new float[input.Length];
        int half = _antiAliasKernel.Length / 2;
        for (int i = 0; i < input.Length; i++)
        {
            float sum = 0f;
            for (int k = -half; k <= half; k++)
            {
                int idx = Math.Clamp(i + k, 0, input.Length - 1);
                sum += input[idx] * _antiAliasKernel[k + half];
            }
            output[i] = sum;
        }
        return output;
    }

    public static float[] DownmixToMono(float[] input, int channels)
    {
        if (channels <= 1) return input;
        var mono = new float[input.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
                sum += input[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    public static float[] Mix(float[] a, float[] b, float gainA = 0.7f, float gainB = 0.7f)
    {
        var len = Math.Min(a.Length, b.Length);
        var result = new float[len];
        for (int i = 0; i < len; i++)
        {
            var mixed = a[i] * gainA + b[i] * gainB;
            if (mixed > 1.0f) mixed = 1.0f;
            else if (mixed < -1.0f) mixed = -1.0f;
            result[i] = mixed;
        }
        return result;
    }

    public static float CalculateRms(float[] samples)
    {
        if (samples.Length == 0) return 0f;
        double sumSquares = 0;
        for (int i = 0; i < samples.Length; i++)
            sumSquares += samples[i] * samples[i];
        return (float)Math.Sqrt(sumSquares / samples.Length);
    }
}
