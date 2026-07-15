using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Лог-мел фронтенд, ТОЧНО повторяющий torchaudio MelSpectrogram из GigaAM
/// (см. gigaam/preprocess.py): sr=16000, n_fft=400, win=400, hop=160, n_mels=64,
/// window=hann(periodic), power=2, center=True/reflect, mel_scale=htk, norm=None,
/// затем ln(clamp(x, 1e-9, 1e9)). Выход: [n_mels=64][frames].
/// Валидируется против ref_features.npy (см. tools/gigaam_export).
/// </summary>
public sealed class MelFrontend
{
    public const int SampleRate = 16000;
    public const int NFft = 400;
    public const int WinLength = 400;
    public const int HopLength = 160;
    public const int NMels = 64;
    private const int NFreqs = NFft / 2 + 1; // 201

    private readonly double[] _window;        // hann periodic, длина WinLength
    private readonly float[,] _melFb;         // [NFreqs, NMels], htk, norm=None

    public MelFrontend()
    {
        _window = HannPeriodic(WinLength);
        _melFb = BuildMelFilterbank();
    }

    /// <summary>float PCM [-1..1] @16кГц → лог-мел [64][frames].</summary>
    public float[][] Compute(float[] samples)
    {
        // center=True → reflect-pad по n_fft/2 с каждой стороны
        int pad = NFft / 2;
        float[] x = ReflectPad(samples, pad);
        int frames = 1 + (x.Length - NFft) / HopLength;
        if (frames < 1) frames = 1;

        var outp = new float[NMels][];
        for (int m = 0; m < NMels; m++) outp[m] = new float[frames];

        var buf = new Complex[NFft];
        var power = new double[NFreqs];

        for (int t = 0; t < frames; t++)
        {
            int start = t * HopLength;
            // окно
            for (int i = 0; i < NFft; i++)
            {
                double v = (start + i) < x.Length ? x[start + i] : 0.0;
                buf[i] = new Complex(v * _window[i], 0.0);
            }
            // FFT без нормировки (как torch.stft)
            Fourier.Forward(buf, FourierOptions.NoScaling);
            // |FFT|^2 для первых NFreqs бинов (power=2)
            for (int f = 0; f < NFreqs; f++)
            {
                double re = buf[f].Real, im = buf[f].Imaginary;
                power[f] = re * re + im * im;
            }
            // мел + log
            for (int m = 0; m < NMels; m++)
            {
                double acc = 0.0;
                for (int f = 0; f < NFreqs; f++)
                {
                    float w = _melFb[f, m];
                    if (w != 0f) acc += w * power[f];
                }
                double clamped = acc < 1e-9 ? 1e-9 : (acc > 1e9 ? 1e9 : acc);
                outp[m][t] = (float)Math.Log(clamped);
            }
        }
        return outp;
    }

    // hann periodic: w[n] = 0.5 - 0.5*cos(2*pi*n/N)
    private static double[] HannPeriodic(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++)
            w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / n);
        return w;
    }

    // np.pad(..., mode="reflect"): без повтора крайнего сэмпла
    private static float[] ReflectPad(float[] s, int pad)
    {
        int n = s.Length;
        if (n == 1) { var one = new float[n + 2 * pad]; Array.Fill(one, s[0]); return one; }
        var r = new float[n + 2 * pad];
        for (int i = 0; i < n; i++) r[pad + i] = s[i];
        for (int j = 0; j < pad; j++)
        {
            r[pad - 1 - j] = s[Math.Min(j + 1, n - 1)];       // левое зеркало
            r[pad + n + j] = s[Math.Max(n - 2 - j, 0)];       // правое зеркало
        }
        return r;
    }

    // torchaudio melscale_fbanks: htk, norm=None, треугольные фильтры
    private static float[,] BuildMelFilterbank()
    {
        double fMin = 0.0, fMax = SampleRate / 2.0;
        var allFreqs = new double[NFreqs];
        for (int i = 0; i < NFreqs; i++) allFreqs[i] = fMax * i / (NFreqs - 1);

        double mMin = HzToMel(fMin), mMax = HzToMel(fMax);
        var fPts = new double[NMels + 2];
        for (int i = 0; i < NMels + 2; i++)
            fPts[i] = MelToHz(mMin + (mMax - mMin) * i / (NMels + 1));

        var fb = new float[NFreqs, NMels];
        for (int m = 0; m < NMels; m++)
        {
            double lower = fPts[m], center = fPts[m + 1], upper = fPts[m + 2];
            for (int f = 0; f < NFreqs; f++)
            {
                double freq = allFreqs[f];
                double up = (freq - lower) / (center - lower);
                double down = (upper - freq) / (upper - center);
                double val = Math.Max(0.0, Math.Min(up, down));
                fb[f, m] = (float)val;
            }
        }
        return fb;
    }

    private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
}
