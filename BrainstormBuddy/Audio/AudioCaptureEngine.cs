using System.Collections.Concurrent;
using System.Diagnostics;
using BrainstormBuddy.Config;
using BrainstormBuddy.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace BrainstormBuddy.Audio;

public class AudioCaptureEngine : IDisposable
{
    private readonly AudioConfig _config;
    private readonly AudioBuffer _buffer;
    private readonly AudioBuffer? _micBuffer;
    private readonly AudioDiagnostics _diagnostics;
    private readonly LoggingService _logger;

    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveInEvent? _micCapture;
    private bool _disposed;

    private readonly ConcurrentQueue<AudioFrame> _micQueue = new();
    private readonly ConcurrentQueue<AudioFrame> _loopbackQueue = new();
    private const int MaxQueueSize = 32;
    private const int MixerIntervalMs = 30;

    private CancellationTokenSource? _mixerCts;
    private Task? _mixerTask;
    // Захват запущен (Start вызван, Stop ещё нет). Нужен UpdateConfig: рестартовать loopback
    // при смене устройства можно только у живого движка (на паузе новый Id подхватит Start).
    private bool _started;
    // Id устройства из конфига, ФАКТИЧЕСКИ применённый последним TryStartLoopback
    // (пустая строка = системный дефолт). Для детекта смены выбора в UpdateConfig.
    private string _appliedLoopbackDeviceId = string.Empty;

    public event EventHandler<float>? RmsUpdated;
    public event EventHandler<string>? DeviceError;
    /// <summary>Сменилось дефолтное устройство ВЫВОДА (наушники и т.п.) — loopback перезапущен. Аргумент — имя устройства.</summary>
    public event EventHandler<string>? DefaultRenderChanged;
    /// <summary>В системе появилось активное аудио-устройство (подключили наушники/микрофон). Аргумент — имя.</summary>
    public event EventHandler<string>? DeviceArrived;

    public bool LoopbackActive => _loopbackCapture != null;
    public bool MicActive => _micCapture != null;

    // «Динамик выкл» (кнопка в оверлее): приём с loopback глушится БЕЗ остановки захвата —
    // рестарт WASAPI дорог и дребезжит на BT-устройствах, а Stop/Start терял бы хвост фразы.
    // Кадры просто выбрасываются ДО буфера VAD: существующий keepalive (loopPaused в MixTick)
    // сам докармливает буфер тишиной, открытая фраза закрывается по паузе, RMS честно ноль.
    // volatile: пишет UI-поток (клик по кнопке), читает mixer-поток.
    private volatile bool _loopbackMuted;
    public bool LoopbackMuted { get => _loopbackMuted; set => _loopbackMuted = value; }

    public AudioCaptureEngine(AudioConfig config, AudioBuffer buffer, AudioDiagnostics diagnostics, LoggingService logger, AudioBuffer? micBuffer = null)
    {
        _config = config;
        _buffer = buffer;
        _micBuffer = micBuffer;
        _diagnostics = diagnostics;
        _logger = logger;
        _logger.Debug($"AudioCaptureEngine created. MicOnly={config.MicOnly}, CaptureMic={config.CaptureMic}, DualBuffer={_micBuffer != null}, MicDevice='{config.MicDeviceId}'", "Audio");
    }

    public static IReadOnlyList<AudioDeviceInfo> GetAvailableDevices() => AudioDeviceEnumerator.GetAvailableDevices();

    public void Start()
    {
        _logger.Info("Audio.Start() called", "Audio");
        if (!_config.MicOnly)
            TryStartLoopback();
        else
            _logger.Info("MicOnly mode → skipping loopback", "Audio");
        if (_config.CaptureMic)
            TryStartMicrophone();
        else
            _logger.Info("CaptureMic=false → skipping microphone (loopback only)", "Audio");

        _mixerCts = new CancellationTokenSource();
        _mixerTask = Task.Run(() => MixerLoop(_mixerCts.Token));

        // Слежение за аудио-устройствами. Раньше смена дефолтного вывода (Bluetooth-наушники!)
        // молча убивала loopback: захват оставался привязан к старому устройству до перезапуска
        // приложения (живой лог: последний loopback-чанк в 20:06, дальше 15 минут тишины).
        StartDeviceWatcher();
        _started = true;
        _logger.Info($"Audio started. Loopback={LoopbackActive}, Mic={MicActive}, Mixer=active", "Audio");
    }

    private MMDeviceEnumerator? _deviceEnum;
    private DeviceNotificationClient? _deviceNotifications;
    private long _lastLoopbackRestartTicks;

    private void StartDeviceWatcher()
    {
        try
        {
            _deviceEnum = new MMDeviceEnumerator();
            _deviceNotifications = new DeviceNotificationClient(this);
            _deviceEnum.RegisterEndpointNotificationCallback(_deviceNotifications);
            _logger.Info("Device watcher registered (default-render change → loopback restart)", "Audio");
        }
        catch (Exception ex) { _logger.Warn($"Device watcher init failed: {ex.Message}", "Audio"); }
    }

    private void StopDeviceWatcher()
    {
        try
        {
            if (_deviceEnum != null && _deviceNotifications != null)
                _deviceEnum.UnregisterEndpointNotificationCallback(_deviceNotifications);
            _deviceEnum?.Dispose();
        }
        catch { /* best-effort */ }
        _deviceEnum = null;
        _deviceNotifications = null;
    }

    // Колбэки WASAPI приходят на COM-потоке — здесь только лёгкая работа + рестарт захвата.
    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly AudioCaptureEngine _owner;
        public DeviceNotificationClient(AudioCaptureEngine owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
                _owner.HandleDefaultRenderChanged(defaultDeviceId);
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState == DeviceState.Active) _owner.HandleDeviceArrived(deviceId);
        }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private void HandleDefaultRenderChanged(string deviceId)
    {
        // Юзер явно выбрал устройство в настройках → оно неприкосновенно: автоследование
        // за сменой системного дефолта работает ТОЛЬКО в режиме «По умолчанию» (пустой Id).
        // Лог оставляем для диагностики, событие (тост) не шлём.
        if (!string.IsNullOrWhiteSpace(_config.LoopbackDeviceId))
        {
            _logger.Info($"Default render device changed → '{TryGetDeviceName(deviceId)}' — игнорирую: loopback закреплён за устройством из настроек", "Audio");
            return;
        }
        // Дебаунс: при подключении BT система даёт серию событий (A2DP/hands-free).
        var now = Environment.TickCount64;
        if (now - _lastLoopbackRestartTicks < 1500) return;
        _lastLoopbackRestartTicks = now;
        var name = TryGetDeviceName(deviceId);
        _logger.Info($"Default render device changed → '{name}' — перезапускаю loopback", "Audio");
        if (!_config.MicOnly)
        {
            try { RestartLoopback(); }
            catch (Exception ex) { _logger.Error("Loopback restart failed", ex, "Audio"); }
        }
        DefaultRenderChanged?.Invoke(this, name);
    }

    private void HandleDeviceArrived(string deviceId)
    {
        var name = TryGetDeviceName(deviceId);
        _logger.Info($"Audio device arrived: '{name}'", "Audio");
        DeviceArrived?.Invoke(this, name);
    }

    private string TryGetDeviceName(string deviceId)
    {
        try { return _deviceEnum?.GetDevice(deviceId)?.FriendlyName ?? deviceId; }
        catch { return deviceId; }
    }

    /// <summary>Перезапуск loopback-захвата на ТЕКУЩЕМ дефолтном устройстве вывода.</summary>
    public void RestartLoopback()
    {
        try { SafeStopRecording(_loopbackCapture); _loopbackCapture?.Dispose(); } catch { }
        _loopbackCapture = null;
        while (_loopbackQueue.TryDequeue(out _)) { }
        TryStartLoopback();
    }

    public void Stop()
    {
        _logger.Info("Audio.Stop() called", "Audio");
        _started = false;

        StopDeviceWatcher();
        _mixerCts?.Cancel();
        try { _mixerTask?.Wait(1000); } catch { }

        try { SafeStopRecording(_loopbackCapture); _loopbackCapture?.Dispose(); } catch (Exception ex) { _logger.Warn($"Loopback stop error: {ex.Message}", "Audio"); }
        try { SafeStopRecording(_micCapture); _micCapture?.Dispose(); } catch (Exception ex) { _logger.Warn($"Mic stop error: {ex.Message}", "Audio"); }
        _loopbackCapture = null;
        _micCapture = null;

        while (_micQueue.TryDequeue(out _)) { }
        while (_loopbackQueue.TryDequeue(out _)) { }

        _logger.Info("Audio stopped.", "Audio");
    }

    private static void SafeStopRecording(object? capture)
    {
        if (capture == null) return;
        try
        {
            var stopMethod = capture.GetType().GetMethod("StopRecording");
            if (stopMethod != null)
                stopMethod.Invoke(capture, null);
        }
        catch { }
    }

    private void TryStartLoopback()
    {
        _logger.Debug("TryStartLoopback: creating WasapiLoopbackCapture...", "Audio");
        // Запоминаем, какой Id из конфига применён: UpdateConfig сравнивает с ним и
        // рестартует захват только при реальной смене выбора (не на каждом «Сохранить»).
        _appliedLoopbackDeviceId = _config.LoopbackDeviceId ?? string.Empty;
        try
        {
            _loopbackCapture = CreateLoopbackCapture();
            var fmt = _loopbackCapture.WaveFormat;
            _logger.Info($"Loopback format: {fmt.SampleRate}Hz, {fmt.BitsPerSample}bit, {fmt.Channels}ch", "Audio");
            _loopbackCapture.DataAvailable += OnLoopbackData;
            _loopbackCapture.StartRecording();
            _logger.Info("WASAPI loopback started", "Audio");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Loopback init failed, continuing with mic only: {ex.Message}", "Audio");
            _loopbackCapture?.Dispose();
            _loopbackCapture = null;
            DeviceError?.Invoke(this, "loopback");
        }
    }

    // Честная привязка к устройству из настроек (раньше LoopbackDeviceId никем не читался,
    // выпадашка была декоративной). Пустой Id = «По умолчанию»: системный дефолт вывода +
    // автоследование за его сменой. Если выбранное устройство пропало/не открылось —
    // фолбэк на дефолт с Warn, а не мёртвый захват без звука.
    private WasapiLoopbackCapture CreateLoopbackCapture()
    {
        var id = _config.LoopbackDeviceId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(id);
                if (device is { DataFlow: DataFlow.Render, State: DeviceState.Active })
                {
                    _logger.Info($"Loopback закреплён за устройством из настроек: '{device.FriendlyName}'", "Audio");
                    return new WasapiLoopbackCapture(device);
                }
                _logger.Warn($"Loopback device '{id}' недоступен (state={device?.State}) — фолбэк на системный дефолт", "Audio");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Loopback device '{id}' не открылся: {ex.Message} — фолбэк на системный дефолт", "Audio");
            }
        }
        return new WasapiLoopbackCapture();
    }

    private void TryStartMicrophone()
    {
        try
        {
            int deviceIndex = ResolveMicDeviceIndex();
            _logger.Debug($"TryStartMicrophone: device index = {deviceIndex} of {WaveInEvent.DeviceCount}", "Audio");
            _micCapture = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(48000, 16, 1)
            };
            _micCapture.DataAvailable += OnMicData;
            _micCapture.StartRecording();
            _logger.Info($"Microphone started: device #{deviceIndex}, 48kHz/16bit/mono", "Audio");
        }
        catch (Exception ex)
        {
            _logger.Error("Microphone init failed", ex, "Audio");
            _micCapture?.Dispose();
            _micCapture = null;
            DeviceError?.Invoke(this, "microphone");
        }
    }

    private int ResolveMicDeviceIndex()
    {
        if (string.IsNullOrEmpty(_config.MicDeviceId))
        {
            _logger.Debug("MicDeviceId empty → using device 0", "Audio");
            return 0;
        }
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            if (caps.ProductName.Contains(_config.MicDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"Mic device matched: #{i} '{caps.ProductName}'", "Audio");
                return i;
            }
        }
        _logger.Warn($"MicDeviceId '{_config.MicDeviceId}' not found, falling back to device 0", "Audio");
        return 0;
    }

    private void OnLoopbackData(object? sender, WaveInEventArgs e)
    {
        if (_disposed || _loopbackCapture == null) return;
        try
        {
            var fmt = _loopbackCapture.WaveFormat;
            var samples = ConvertBytesToFloats(e.Buffer, e.BytesRecorded, fmt.BitsPerSample, fmt.Channels);
            var mono = Resampler.DownmixToMono(samples, fmt.Channels);
            var resampled = Resampler.ResampleLinear(mono, fmt.SampleRate, 1, _config.SampleRate);
            EnqueueOrDrop(_loopbackQueue, resampled);
        }
        catch (Exception ex)
        {
            _logger.Error("Loopback data callback failed", ex, "Audio");
            _diagnostics.DeviceInitError("loopback", ex);
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (_disposed || _micCapture == null) return;
        try
        {
            var format = _micCapture.WaveFormat;
            var samples = ConvertBytesToFloats(e.Buffer, e.BytesRecorded, format.BitsPerSample, format.Channels);
            var resampled = Resampler.ResampleLinear(samples, format.SampleRate, format.Channels, _config.SampleRate);
            EnqueueOrDrop(_micQueue, resampled);
        }
        catch (Exception ex)
        {
            _logger.Error("Mic data callback failed", ex, "Audio");
            _diagnostics.DeviceInitError("microphone", ex);
        }
    }

    private static void EnqueueOrDrop(ConcurrentQueue<AudioFrame> queue, float[] samples)
    {
        var frame = new AudioFrame(samples, Stopwatch.GetTimestamp());
        queue.Enqueue(frame);
        while (queue.Count > MaxQueueSize)
        {
            queue.TryDequeue(out _);
        }
    }

    private void MixerLoop(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(MixerIntervalMs);
        var timer = new PeriodicTimer(interval);
        _logger.Info("Mixer loop started", "Audio");

        try
        {
            while (!ct.IsCancellationRequested && timer.WaitForNextTickAsync(ct).AsTask().GetAwaiter().GetResult())
            {
                MixTick();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            timer.Dispose();
            _logger.Info("Mixer loop stopped", "Audio");
        }
    }

    // Подряд пустых тиков loopback-очереди. WASAPI loopback при паузе рендера вообще не шлёт
    // буферы: без подпитки тишиной VAD не может закрыть висящую фразу (чанк уезжает в STT только
    // на следующем звуке), а уровень «замерзает». Гистерезис — чтобы не дребезжать на джиттере.
    private int _loopIdleTicks;
    // ~180мс при тике 30мс. Запас не случаен: NAudio может отдавать loopback-колбэки крупными
    // порциями (до ~100мс), и очередь штатно пустует 3-4 тика подряд ПРИ ЖИВОМ звуке — меньший
    // гистерезис вставлял бы кадры тишины посреди речи. На закрытие фраз не влияет
    // (порог паузы ≥ 800мс).
    private const int LoopIdleTicksBeforeSilence = 6;

    private void MixTick()
    {
        bool hasMic = _micQueue.TryDequeue(out var micFrame);
        bool hasLoop = _loopbackQueue.TryDequeue(out var loopFrame);
        // «Динамик выкл»: кадр выброшен ДО буфера — дальше канал ведёт себя как «рендер на
        // паузе» (гистерезис loopPaused ниже докармливает VAD тишиной и закрывает фразу).
        if (hasLoop && _loopbackMuted) hasLoop = false;
        if (hasLoop) _loopIdleTicks = 0;
        bool loopPaused = !hasLoop && LoopbackActive && ++_loopIdleTicks >= LoopIdleTicksBeforeSilence;

        if (_micBuffer != null)
        {
            // Dual-buffer mode: route loopback and mic to separate VAD pipelines (no mixing)
            float loopRms = 0f;
            if (hasLoop)
            {
                _buffer.AddSamples(loopFrame.Samples);
                loopRms = Resampler.CalculateRms(loopFrame.Samples);
                _diagnostics.LogLevels(0f, loopRms, loopRms);
            }
            else if (loopPaused)
            {
                // Рендер на паузе → кормим VAD тишиной: висящая фраза закрывается по паузе,
                // уровень честно падает в ноль.
                _buffer.AddSamples(new float[_config.SampleRate * MixerIntervalMs / 1000]);
            }
            float micRms = 0f;
            if (hasMic)
            {
                _micBuffer.AddSamples(micFrame.Samples);
                micRms = Resampler.CalculateRms(micFrame.Samples);
            }
            // Live RMS: единое значение по обоим каналам. Раньше событие стреляло только от
            // loopback — индикатор в настройках был слеп к микрофону и «замерзал» при паузе.
            RmsUpdated?.Invoke(this, Math.Max(loopRms, micRms));
            return;
        }

        // Single-buffer mode (mixed): original behaviour
        if (hasMic && !hasLoop)
        {
            PushThrough(micFrame.Samples, Array.Empty<float>());
            return;
        }
        if (!hasMic && hasLoop)
        {
            PushThrough(Array.Empty<float>(), loopFrame.Samples);
            return;
        }
        if (hasMic && hasLoop)
        {
            var mixed = Resampler.Mix(micFrame.Samples, loopFrame.Samples, 0.85f, 0.85f);
            PushThrough(micFrame.Samples, loopFrame.Samples, mixed);
            return;
        }

        // Оба канала пусты: тот же keepalive для single-buffer (loopback-only) режима.
        if (loopPaused)
        {
            _buffer.AddSamples(new float[_config.SampleRate * MixerIntervalMs / 1000]);
            RmsUpdated?.Invoke(this, 0f);
        }
    }

    private void PushThrough(float[] mic, float[] loopback, float[]? preMixed = null)
    {
        var mix = preMixed;
        if (mix == null)
        {
            if (mic.Length > 0 && loopback.Length > 0)
                mix = Resampler.Mix(mic, loopback, 0.85f, 0.85f);
            else if (mic.Length > 0)
                mix = mic;
            else if (loopback.Length > 0)
                mix = loopback;
            else
                return;
        }

        _buffer.AddSamples(mix);

        var micRms = mic.Length > 0 ? Resampler.CalculateRms(mic) : 0f;
        var loopRms = loopback.Length > 0 ? Resampler.CalculateRms(loopback) : 0f;
        var mixRms = Resampler.CalculateRms(mix);
        _diagnostics.LogLevels(micRms, loopRms, mixRms);
        RmsUpdated?.Invoke(this, mixRms);
    }

    private static float[] ConvertBytesToFloats(byte[] buffer, int bytesRecorded, int bitsPerSample, int channels)
    {
        if (bitsPerSample == 16)
        {
            var sampleCount = bytesRecorded / 2;
            var result = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(buffer, i * 2);
                result[i] = s / 32768f;
            }
            return result;
        }
        if (bitsPerSample == 32)
        {
            var sampleCount = bytesRecorded / 4;
            var result = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                result[i] = BitConverter.ToSingle(buffer, i * 4);
            }
            return result;
        }
        System.Diagnostics.Debug.WriteLine($"Unsupported bits per sample: {bitsPerSample}");
        return Array.Empty<float>();
    }

    public bool TryGetChunk(out byte[] wav)
    {
        if (_buffer.TryGetReadyChunk(out wav) && wav.Length > 0)
        {
            _diagnostics.ChunkSent(wav.Length);
            _diagnostics.VadTriggered();
            _logger?.Debug($"VAD chunk emitted: {wav.Length} bytes", "Audio");
            return true;
        }
        wav = Array.Empty<byte>();
        return false;
    }

    public bool TryGetMicChunk(out byte[] wav)
    {
        if (_micBuffer != null && _micBuffer.TryGetReadyChunk(out wav) && wav.Length > 0)
        {
            _diagnostics.ChunkSent(wav.Length);
            _diagnostics.VadTriggered();
            _logger?.Debug($"VAD mic chunk emitted: {wav.Length} bytes", "Audio");
            return true;
        }
        wav = Array.Empty<byte>();
        return false;
    }

    public void UpdateConfig(AudioConfig config)
    {
        _logger.Info($"Audio.UpdateConfig: RmsThreshold={config.RmsThreshold}, Silence={config.SilenceSeconds}s, " +
                     $"PreRoll={config.PreRollMs}ms, PostRoll={config.PostRollMs}ms, MinSpeech={config.MinSpeechMs}ms, " +
                     $"MicOnly={config.MicOnly}", "Audio");
        _buffer.UpdateParameters(config.RmsThreshold, config.SilenceSeconds, config.PreRollMs, config.PostRollMs, config.MinSpeechMs, config.AutoCalibrateThreshold);
        // Раньше mic-буфер не обновлялся вовсе (застарелый баг): его порог тишины оставался
        // замороженным с конструктора. Пробрасываем те же параметры симметрично.
        _micBuffer?.UpdateParameters(config.RmsThreshold, config.SilenceSeconds, config.PreRollMs, config.PostRollMs, config.MinSpeechMs, config.AutoCalibrateThreshold);
        _diagnostics.ApplyConfig(config);

        // Смена «Динамик (loopback)» в настройках → рестарт захвата на новом устройстве
        // (симметрично автоследованию за системным дефолтом). Синхронизируем _config на случай,
        // если пришёл другой экземпляр AudioConfig (TryStartLoopback читает именно _config).
        var wantedLoopbackId = config.LoopbackDeviceId ?? string.Empty;
        _config.LoopbackDeviceId = wantedLoopbackId;
        if (_started && !_config.MicOnly && wantedLoopbackId != _appliedLoopbackDeviceId)
        {
            _logger.Info($"LoopbackDeviceId changed ('{_appliedLoopbackDeviceId}' → '{wantedLoopbackId}') — перезапускаю loopback", "Audio");
            try { RestartLoopback(); }
            catch (Exception ex) { _logger.Error("Loopback restart after device change failed", ex, "Audio"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logger.Info("AudioCaptureEngine.Dispose()", "Audio");
        Stop();
        _logger.Info("AudioCaptureEngine disposed", "Audio");
    }

    private readonly struct AudioFrame(float[] samples, long timestamp)
    {
        public float[] Samples { get; } = samples;
        public long Timestamp { get; } = timestamp;

        public void Deconstruct(out float[] s, out long t) { s = Samples; t = Timestamp; }
    }
}
