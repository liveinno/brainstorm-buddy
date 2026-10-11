using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Лог-мел фронтенд, повторяющий torchaudio MelSpectrogram из GigaAM.
/// Дефолт — параметры v2 (см. gigaam/preprocess.py и frontend_meta.json): sr=16000,
/// n_fft=400, win=400, hop=160, n_mels=64, window=hann(periodic), power=2,
/// center=True/reflect, mel_scale=htk, norm=None, затем ln(clamp(x, 1e-9, 1e9)).
/// GigaAM v3 отличается: n_fft=win=320, center=false (без паддинга) — параметризовано
/// через конструктор. Выход: [n_mels][frames]. Валидируется против ref_features.npy
/// (см. tools/gigaam_export).
/// </summary>
public sealed class MelFrontend
{
    public const int SampleRate = 16000;
    // Параметры GigaAM v2 (используются как дефолт конструктора и в тестах/валидаторах).
    public const int NFft = 400;
    public const int WinLength = 400;
    public const int HopLength = 160;
    public const int NMels = 64;

    private readonly int _nFft;
    private readonly int _winLength;
    private readonly int _hopLength;
    private readonly int _nMels;
    private readonly bool _center;
    private readonly int _nFreqs;               // nFft/2 + 1

    private readonly double[] _window;          // hann periodic, длина winLength
    private readonly float[,] _melFb;           // [nFreqs, nMels], htk, norm=None

    /// <summary>n_fft экземпляра — минимальная длина сигнала при center=false.</summary>
    public int Nfft => _nFft;

    /// <summary>Фронтенд GigaAM v2: n_fft=win=400, hop=160, n_mels=64, center=true.</summary>
    public MelFrontend() : this(NFft, WinLength, HopLength, NMels, center: true) { }

    /// <summary>Параметризованный фронтенд (напр. v3: n_fft=320, win=320, hop=160, n_mels=64, center=false).</summary>
    public MelFrontend(int nFft, int winLength, int hopLength, int nMels, bool center)
    {
        _nFft = nFft;
        _winLength = winLength;
        _hopLength = hopLength;
        _nMels = nMels;
        _center = center;
        _nFreqs = nFft / 2 + 1;
        _window = HannPeriodic(winLength);
        _melFb = BuildMelFilterbank(_nFreqs, nMels);
    }

    /// <summary>float PCM [-1..1] @16кГц → лог-мел [nMels][frames].</summary>
    public float[][] Compute(float[] samples)
    {
        float[] x;
        if (_center)
        {
            // center=True → reflect-pad по n_fft/2 с каждой стороны
            x = ReflectPad(samples, _nFft / 2);
        }
        else
        {
            // center=false: кадров floor((n - n_fft)/hop)+1; короткий сигнал добиваем нулями до 1 кадра
            x = samples;
            if (x.Length < _nFft)
            {
                var padded = new float[_nFft];
                Array.Copy(samples, padded, x.Length);
                x = padded;
            }
        }
        int frames = 1 + (x.Length - _nFft) / _hopLength;
        if (frames < 1) frames = 1;

        var outp = new float[_nMels][];
        for (int m = 0; m < _nMels; m++) outp[m] = new float[frames];

        var buf = new Complex[_nFft];
        var power = new double[_nFreqs];

        for (int t = 0; t < frames; t++)
        {
            int start = t * _hopLength;
            // окно длины winLength; при n_fft>win хвост буфера — нули (как torch.stft)
            for (int i = 0; i < _nFft; i++)
            {
                double v = (i < _winLength && start + i < x.Length) ? x[start + i] : 0.0;
                double w = i < _window.Length ? _window[i] : 0.0;
                buf[i] = new Complex(v * w, 0.0);
            }
            // FFT без нормировки (как torch.stft)
            Fourier.Forward(buf, FourierOptions.NoScaling);
            // |FFT|^2 для первых nFreqs бинов (power=2)
            for (int f = 0; f < _nFreqs; f++)
            {
                double re = buf[f].Real, im = buf[f].Imaginary;
                power[f] = re * re + im * im;
            }
            // мел + log
            for (int m = 0; m < _nMels; m++)
            {
                double acc = 0.0;
                for (int f = 0; f < _nFreqs; f++)
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
    private static float[,] BuildMelFilterbank(int nFreqs, int nMels)
    {
        double fMin = 0.0, fMax = SampleRate / 2.0;
        var allFreqs = new double[nFreqs];
        for (int i = 0; i < nFreqs; i++) allFreqs[i] = fMax * i / (nFreqs - 1);

        double mMin = HzToMel(fMin), mMax = HzToMel(fMax);
        var fPts = new double[nMels + 2];
        for (int i = 0; i < nMels + 2; i++)
            fPts[i] = MelToHz(mMin + (mMax - mMin) * i / (nMels + 1));

        var fb = new float[nFreqs, nMels];
        for (int m = 0; m < nMels; m++)
        {
            double lower = fPts[m], center = fPts[m + 1], upper = fPts[m + 2];
            for (int f = 0; f < nFreqs; f++)
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
