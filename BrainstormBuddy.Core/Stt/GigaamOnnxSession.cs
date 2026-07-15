using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BrainstormBuddy.Stt;

/// <summary>Режим вычислений STT.</summary>
public enum SttAccel { Auto, Cpu, DirectML }

/// <summary>
/// Обёртка над ONNX-сессией GigaAM CTC. Вход: features [1,64,T] + feature_lengths [1];
/// выход: log_probs [1, T', vocab=34]. CPU по умолчанию (работает везде), DirectML — опция
/// (любая DX12-видеокарта, вкл. Intel UHD; на слабых iGPU может быть медленнее CPU).
/// </summary>
public sealed class GigaamOnnxSession : IDisposable
{
    private readonly InferenceSession _session;
    public string ActiveProvider { get; }

    public GigaamOnnxSession(string modelPath, SttAccel accel = SttAccel.Cpu, int gpuDevice = 0)
    {
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        ActiveProvider = "CPU";
        if (accel == SttAccel.DirectML)
        {
            try
            {
                // gpuDevice = индекс DXGI-адаптера (0 обычно встройка, 1 — дискретная). DirectML EP
                // несовместим с параллельными оптимизациями памяти — отключаем как в доке ORT.
                opts.EnableMemoryPattern = false;
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opts.AppendExecutionProvider_DML(gpuDevice);
                ActiveProvider = $"DirectML:{gpuDevice}";
            }
            catch
            {
                // нет DX12/драйвера/устройства — тихо остаёмся на CPU
                ActiveProvider = "CPU";
            }
        }
        _session = new InferenceSession(modelPath, opts);
    }

    /// <summary>features [64][T] → log_probs [T'][vocab].</summary>
    public float[][] Run(float[][] features)
    {
        int mels = features.Length;
        int frames = features[0].Length;

        var feat = new DenseTensor<float>(new[] { 1, mels, frames });
        for (int m = 0; m < mels; m++)
            for (int t = 0; t < frames; t++)
                feat[0, m, t] = features[m][t];

        var len = new DenseTensor<long>(new[] { 1 });
        len[0] = frames;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("features", feat),
            NamedOnnxValue.CreateFromTensor("feature_lengths", len),
        };

        using var results = _session.Run(inputs);
        var outT = results.First().AsTensor<float>(); // [1, T', vocab]
        int tOut = outT.Dimensions[1], vocab = outT.Dimensions[2];

        var logp = new float[tOut][];
        for (int t = 0; t < tOut; t++)
        {
            logp[t] = new float[vocab];
            for (int v = 0; v < vocab; v++) logp[t][v] = outT[0, t, v];
        }
        return logp;
    }

    public void Dispose() => _session.Dispose();
}
