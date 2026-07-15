using BrainstormBuddy.Audio;
using BrainstormBuddy.Services;
using NAudio.Wave;

namespace BrainstormBuddy.Headless;

public sealed class LoopbackAudioSource : IDisposable
{
    private readonly AudioBuffer _buffer;
    private readonly int _targetSampleRate;
    private readonly LoggingService _logger;
    private WasapiLoopbackCapture? _capture;
    private bool _disposed;
    private long _callbacks;
    private DateTime _lastStatsLog = DateTime.MinValue;
    private double _lastRms;
    private int _lastSamples;

    public event EventHandler<float>? RmsUpdated;

    public LoopbackAudioSource(AudioBuffer buffer, int targetSampleRate, LoggingService logger)
    {
        _buffer = buffer;
        _targetSampleRate = targetSampleRate;
        _logger = logger;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LoopbackAudioSource));
        _capture = new WasapiLoopbackCapture();
        var fmt = _capture.WaveFormat;
        _logger.Info($"Loopback format: {fmt.SampleRate}Hz, {fmt.BitsPerSample}bit, {fmt.Channels}ch, encoding={fmt.Encoding}", "Audio");
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (s, e) => _logger.Error($"Loopback recording stopped: {e.Exception?.Message}", e.Exception, "Audio");
        _capture.StartRecording();
        _logger.Info("Loopback capture started", "Audio");
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_disposed || _capture == null) return;
        _callbacks++;
        try
        {
            var fmt = _capture.WaveFormat;
            var mono = ConvertToMono(e.Buffer, e.BytesRecorded, fmt);
            if (mono.Length == 0)
            {
                _logger.Warn($"Loopback: zero samples after conversion (bytesRecorded={e.BytesRecorded}, fmt={fmt.BitsPerSample}bit/{fmt.Encoding})", "Audio");
                return;
            }

            var resampled = Resampler.ResampleLinear(mono, fmt.SampleRate, 1, _targetSampleRate);
            _buffer.AddSamples(resampled);

            var rms = Resampler.CalculateRms(resampled);
            _lastRms = rms;
            _lastSamples = resampled.Length;
            RmsUpdated?.Invoke(this, rms);

            if ((DateTime.Now - _lastStatsLog).TotalSeconds >= 2)
            {
                _lastStatsLog = DateTime.Now;
                _logger.Info($"Loopback: callbacks={_callbacks}, last chunk samples={_lastSamples}, rms={_lastRms:F4}, buf={_buffer.CurrentSampleCount}", "Audio");
                _callbacks = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Loopback OnData failed", ex, "Audio");
        }
    }

    private static float[] ConvertToMono(byte[] buffer, int bytesRecorded, WaveFormat fmt)
    {
        var channels = Math.Max(1, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            var sampleCount = bytesRecorded / 4;
            var frames = sampleCount / channels;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    var idx = (i * channels + c) * 4;
                    if (idx + 3 >= bytesRecorded) break;
                    sum += BitConverter.ToSingle(buffer, idx);
                }
                mono[i] = sum / channels;
            }
            return mono;
        }

        if (bytesPerSample == 2)
        {
            var sampleCount = bytesRecorded / 2;
            var frames = sampleCount / channels;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    var idx = (i * channels + c) * 2;
                    if (idx + 1 >= bytesRecorded) break;
                    sum += BitConverter.ToInt16(buffer, idx) / 32768f;
                }
                mono[i] = sum / channels;
            }
            return mono;
        }

        System.Diagnostics.Debug.WriteLine($"Unsupported loopback format: {fmt.BitsPerSample}bit {fmt.Encoding}");
        return Array.Empty<float>();
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch (Exception ex) { _logger.Warn($"Stop: {ex.Message}", "Audio"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
    }
}
