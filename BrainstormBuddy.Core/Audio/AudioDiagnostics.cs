using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Audio;

public class AudioDiagnostics
{
    private readonly LoggingService _logger;
    private bool _enabled;
    private DateTime _lastLogTime = DateTime.MinValue;
    private const int LogIntervalMs = 200;

    public AudioDiagnostics(LoggingService logger, bool enabled = false)
    {
        _logger = logger;
        _enabled = enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    public void LogLevels(float micRms, float loopbackRms, float mixedRms)
    {
        if (!_enabled) return;
        var now = DateTime.Now;
        if ((now - _lastLogTime).TotalMilliseconds < LogIntervalMs) return;
        _lastLogTime = now;

        _logger.Debug($"RMS mic={micRms:F4} loop={loopbackRms:F4} mix={mixedRms:F4}");
    }

    public void VadTriggered()
    {
        if (!_enabled) return;
        _logger.Debug("VAD triggered");
    }

    public void ChunkSent(int bytes)
    {
        if (!_enabled) return;
        _logger.Debug($"Chunk sent: {bytes} bytes");
    }

    public void SttResponseReceived(string? text)
    {
        if (!_enabled) return;
        _logger.Debug($"STT response received: '{text}'");
    }

    public void DeviceInitError(string deviceName, Exception ex)
    {
        _logger.Error($"Audio device init error ({deviceName})", ex);
    }

    public void ApplyConfig(AudioConfig config)
    {
        _enabled = config.EnableDebugLogs;
    }
}
