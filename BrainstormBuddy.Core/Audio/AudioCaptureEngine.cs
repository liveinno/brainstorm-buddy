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
    private volatile bool _started;
    // Сериализует создание/присвоение/снапшот capture-объектов. Без него Stop()
    // делал Dispose() объекту, который TryStartLoopback держал внутри нативного
    // IAudioClient.Initialize → AccessViolation в COM-коде = мгновенная смерть
    // процесса без логов (живая репродукция: Audio.Stop() во время Audio.Start()).
    private readonly object _captureLock = new();
    // Id устройства из конфига, ФАКТИЧЕСКИ применённый последним TryStartLoopback
    // (пустая строка = системный дефолт). Для детекта смены выбора в UpdateConfig.
    private string _appliedLoopbackDeviceId = string.Empty;
    // Авто-следование: Id эндпойнта, на который пересели из-за того, что он шумел,
    // а прежний был тих. null = нет принудительной привязки (идём за дефолтом).
    private string? _autoLoopbackDeviceId;

    public event EventHandler<float>? RmsUpdated;
    public event EventHandler<string>? DeviceError;
    /// <summary>Сменилось дефолтное устройство ВЫВОДА (наушники и т.п.) — loopback перезапущен. Аргумент — имя устройства.</summary>
    public event EventHandler<string>? DefaultRenderChanged;
    /// <summary>В системе появилось активное аудио-устройство (подключили наушники/микрофон). Аргумент — имя.</summary>
    public event EventHandler<string>? DeviceArrived;

    /// <summary>
    /// Сэмплы, РЕАЛЬНО ушедшие в loopback-буфер (_buffer) на этом тике миксера — включая
    /// keepalive-тишину. Подписчик (стрим-диаризация Nemotron) обязан получать ровно ту же
    /// последовательность сэмплов: шкалы ChunkInfo и diar-стрима совпадают 1:1.
    /// Mic-канал сюда НЕ попадает. Вызывается с mixer-потока синхронно — handler не должен блокироваться.
    /// </summary>
    public event Action<float[]>? LoopbackSamples;

    public bool LoopbackActive => _loopbackCapture != null;
    public bool MicActive => _micCapture != null;

    // «Динамик выкл» (кнопка в оверлее): приём с loopback глушится БЕЗ остановки захвата —
    // рестарт WASAPI дорог и дребезжит на BT-устройствах, а Stop/Start терял бы хвост фразы.
    // Кадры просто выбрасываются ДО буфера VAD: существующий keepalive (loopPaused в MixTick)
    // сам докармливает буфер тишиной, открытая фраза закрывается по паузе, RMS честно ноль.
    // volatile: пишет UI-поток (клик по кнопке), читает mixer-поток.
    private volatile bool _loopbackMuted;
    public bool LoopbackMuted { get => _loopbackMuted; set => _loopbackMuted = value; }

    // «Мик выкл»: симметрично LoopbackMuted — мик-кадры выбрасываются в MixTick до буфера,
    // сам WaveInEvent продолжает бежать (тумблер мгновенный). Keepalive micPaused в MixTick
    // докармливает mic-буфер тишиной: висящая фраза закрывается по паузе, а не висит до размьюта.
    private volatile bool _micMuted;
    public bool MicMuted { get => _micMuted; set => _micMuted = value; }

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
        _started = true; // до TryStart*: те перепроверяют флаг после нативного init
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


    // Колбэки WASAPI приходят на COM-потоке — тяжёлой работы здесь быть не должно:
    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly AudioCaptureEngine _owner;
        public DeviceNotificationClient(AudioCaptureEngine owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // COM-колбэк нельзя грузить: UnregisterEndpointNotificationCallback в
            // StopDeviceWatcher ждёт завершения in-flight колбэков, а тяжёлый
            // RestartLoopback (StopRecording-join + пересоздание WASAPI) их держал —
            // UI-поток в Stop() стоял до watchdog-kill (2026-10-01). Уходим на worker.
            if (flow == DataFlow.Render && role == Role.Multimedia)
                Task.Run(() => _owner.HandleDefaultRenderChanged(defaultDeviceId));
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState == DeviceState.Active)
                Task.Run(() => _owner.HandleDeviceArrived(deviceId));
        }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    // Watchdog зомби-захвата: WASAPI после глитча эндпойнта умеет а) молчать без ошибок,
    // б) валить колбэки с пустым payload. Capture жив (LoopbackActive), а звука нет —
    // по коду это неотличимо от «рендер на паузе». Гейт по _buffer.IsSpeaking: пауза
    // закрывает фразу тишиной за ~2с, зомби — никогда.
    private int _loopbackRestartPending;
    private int _emptyLoopPackets;
    private MMDevice? _loopbackDevice; // активный render-эндпойнт захвата — для метра в IsLoopbackEndpointActive
    private const int EmptyPacketsBeforeRestart = 64; // срабатывает только при активном метре — см. IsLoopbackEndpointActive
    private const int LoopStallTicks = 334;           // ~10с при тике миксера 30мс
    private const int IdleScanEveryPackets = 32;      // ~1с при ~30 пакетах/с — скан метров на «звук ушёл на другой эндпойнт»
    private int _idleScanCounter;

    private void RequestLoopbackRestart(string reason)
    {
        if (Interlocked.CompareExchange(ref _loopbackRestartPending, 1, 0) != 0) return;
        _logger.Warn($"Loopback capture stalled ({reason}) — restarting", "Audio");
        Task.Run(() =>
        {
            try { RestartLoopback(); }
            catch (Exception ex) { _logger.Error("Stalled loopback restart failed", ex, "Audio"); }
            finally { Interlocked.Exchange(ref _loopbackRestartPending, 0); }
        });
    }

    private void HandleDefaultRenderChanged(string deviceId)
    {
        // Отложенный колбэк (см. Task.Run в DeviceNotificationClient) может прийти
        // после Stop()/Dispose — не воскрешаем мёртвый захват.
        if (_disposed || !_started) return;

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
        WasapiLoopbackCapture? old;
        lock (_captureLock)
        {
            if (_disposed || !_started) return; // сталевый COM-колбэк после Stop() — не воскрешать
            old = _loopbackCapture;
            _loopbackCapture = null;
            while (_loopbackQueue.TryDequeue(out _)) { }
        }
        // Тот же риск, что и в Stop(): StopRecording может зависнуть на битом
        // драйвере — снимаем девайс в фоне, а новый захват стартуем сразу.
        _ = Task.Run(() => { try { SafeStopRecording(old); old?.Dispose(); } catch { } });
        TryStartLoopback();
    }

    public void Stop()
    {
        _logger.Info("Audio.Stop() called", "Audio");
        _started = false;

        _mixerCts?.Cancel();

        // Снапшот + обнуление полей под _captureLock: иначе Stop мог Dispose'нуть
        // capture, который TryStart* ещё держит в нативном init → AV (см. поле).
        // TryEnter, а не голый lock: TryStart* может сидеть в зависшем
        // IAudioClient.Initialize на битом драйвере, и голый lock вернул бы
        // зависание на вызывающий (обычно UI) поток — снова watchdog-kill.
        WaveInEvent? mic = null;
        WasapiLoopbackCapture? loopback = null;
        var locked = Monitor.TryEnter(_captureLock, 1500);
        try
        {
            if (locked)
            {
                loopback = _loopbackCapture;
                mic = _micCapture;
                // Обнуляем до остановки: колбэки OnLoopbackData/OnMicData сверяются с
                // этими полями и перестают трогать WaveFormat до завершения StopRecording.
                _loopbackCapture = null;
                _micCapture = null;
                while (_micQueue.TryDequeue(out _)) { }
                while (_loopbackQueue.TryDequeue(out _)) { }
            }
        }
        finally { if (locked) Monitor.Exit(_captureLock); }
        if (!locked)
            _logger.Warn("Stop: _captureLock занят зависшим init — захваты подберём позже", "Audio");

        // Снапшотим вотчер в локалы и обнуляем поля СРАЗУ: фоновый unregister иначе
        // гонялся бы с StartDeviceWatcher() при быстрой пауза→резюм и занулял бы
        // свежий enumerator.
        var watcherEnum = _deviceEnum;
        var watcherClient = _deviceNotifications;
        _deviceEnum = null;
        _deviceNotifications = null;

        // ВСЯ блокирующая часть — в одном фоновом таске с дедлайном:
        //  - UnregisterEndpointNotificationCallback ждёт завершения in-flight
        //    COM-колбэков, а те на битом драйвере могут не вернуться;
        //  - NAudio StopRecording джойнит capture-поток — тоже может зависнуть.
        // Синхронный вариант на UI-потоке умирал до watchdog-kill (2026-10-01).
        // Зависший таск просто бросается — поток фоновый, выходу не мешает.
        var stopTask = Task.Run(() =>
        {
            try
            {
                if (watcherEnum != null && watcherClient != null)
                    watcherEnum.UnregisterEndpointNotificationCallback(watcherClient);
                watcherEnum?.Dispose();
            }
            catch { /* best-effort */ }
            try { _mixerTask?.Wait(1000); } catch { }
            try { SafeStopRecording(loopback); loopback?.Dispose(); }
            catch (Exception ex) { _logger.Warn($"Loopback stop error: {ex.Message}", "Audio"); }
            try { SafeStopRecording(mic); mic?.Dispose(); }
            catch (Exception ex) { _logger.Warn($"Mic stop error: {ex.Message}", "Audio"); }
            // Поздний проход: если TryEnter выше не успел, TryStart* мог доделать
            // нативный init и присвоить поле уже после нашего снапшота. Добираем
            // такие «опоздавшие» захваты — их TryStart* сам остановит при
            // _started==false, но надёжнее снять и здесь.
            if (!locked)
            {
                try
                {
                    WaveInEvent? lateMic;
                    WasapiLoopbackCapture? lateLoopback;
                    lock (_captureLock)
                    {
                        lateLoopback = _loopbackCapture;
                        lateMic = _micCapture;
                        _loopbackCapture = null;
                        _micCapture = null;
                    }
                    SafeStopRecording(lateLoopback); lateLoopback?.Dispose();
                    SafeStopRecording(lateMic); lateMic?.Dispose();
                }
                catch { }
            }
            _logger.Info("Audio stopped.", "Audio");
        });
        if (!stopTask.Wait(3000))
            _logger.Warn("Audio stop timed out after 3s — hung driver abandoned in background", "Audio");
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
        WasapiLoopbackCapture? capture = null;
        try
        {
            capture = CreateLoopbackCapture();
            var fmt = capture.WaveFormat;
            _logger.Info($"Loopback format: {fmt.SampleRate}Hz, {fmt.BitsPerSample}bit, {fmt.Channels}ch", "Audio");
            capture.DataAvailable += OnLoopbackData;
            WasapiLoopbackCapture? prev = null;
            lock (_captureLock)
            {
                if (!_started || _disposed)
                {
                    // Stop() пробежал пока мы были в CreateLoopbackCapture — захват
                    // не начинаем, объект просто утилизируем ниже.
                    capture.Dispose();
                    return;
                }
                // Перекрывающийся старт (watcher-рестарт + UpdateConfig): старый
                // захват не сиротим — забираем его и глушим в фоне после лока.
                prev = _loopbackCapture;
                _loopbackCapture = capture;
                // Нативный init под локом — поэтому Stop() берёт его через TryEnter.
                _loopbackCapture.StartRecording();
            }
            capture = null; // владение передано полю
            if (prev != null)
                _ = Task.Run(() => { try { SafeStopRecording(prev); prev.Dispose(); } catch { } });
            _logger.Info("WASAPI loopback started", "Audio");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Loopback init failed, continuing with mic only: {ex.Message}", "Audio");
            capture?.Dispose();
            lock (_captureLock) { _loopbackCapture = null; }
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
                    _loopbackDevice = device;
                    return new WasapiLoopbackCapture(device);
                }
                _logger.Warn($"Loopback device '{id}' недоступен (state={device?.State}) — фолбэк на системный дефолт", "Audio");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Loopback device '{id}' не открылся: {ex.Message} — фолбэк на системный дефолт", "Audio");
            }
        }
        // Авто-привязка к шумящему эндпойнту (звук звонка может идти не на дефолт —
        // BT-гарнитура/HDMI = Communications-дефолт при Multimedia-дефолте на колонках).
        if (!string.IsNullOrWhiteSpace(_autoLoopbackDeviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(_autoLoopbackDeviceId);
                if (device is { DataFlow: DataFlow.Render, State: DeviceState.Active })
                {
                    _loopbackDevice = device;
                    return new WasapiLoopbackCapture(device);
                }
                // шумевший эндпойнт пропал — возвращаемся к дефолту
                _autoLoopbackDeviceId = null;
            }
            catch { _autoLoopbackDeviceId = null; }
        }
        // Parameterless-ctor берёт GetDefaultLoopbackCaptureDevice() — резолвим тот же
        // эндпойнт сами, чтобы держать MMDevice для метра (см. TryGetLoopbackPeak).
        // Поле ставим ДО возврата: сталевый пустой-пакет от старого захвата сверит
        // пик уже по новому девайсу — ошибка приемлема (idle→нет рестарта).
        _loopbackDevice = WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
        return new WasapiLoopbackCapture();
    }

    // WASAPI-особенность: на простаивающем render-эндпойнте loopback-захват отдаёт
    // колбэки с BytesRecorded==0 — это НЕ зомби (движок вывода спит). Реальный зомби
    // отличить можно только по метру: эндпойнт шумит, а пакеты пустые.
    private bool IsLoopbackEndpointActive()
    {
        try
        {
            var dev = _loopbackDevice;
            return dev is { State: DeviceState.Active } &&
                   dev.AudioMeterInformation.MasterPeakValue > 0.0005f;
        }
        catch { return false; }
    }

    /// <summary>Id активного render-эндпойнта с макс. MasterPeakValue &gt; порога, иначе null.</summary>
    private string? FindLoudestRenderEndpointId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? best = null;
            float bestPeak = 0.0005f;
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                {
                    float peak = dev.AudioMeterInformation.MasterPeakValue;
                    if (peak > bestPeak) { bestPeak = peak; best = dev.ID; }
                }
            }
            return best;
        }
        catch { return null; }
    }

    private void TryStartMicrophone()
    {
        WaveInEvent? mic = null;
        try
        {
            int deviceIndex = ResolveMicDeviceIndex();
            _logger.Debug($"TryStartMicrophone: device index = {deviceIndex} of {WaveInEvent.DeviceCount}", "Audio");
            mic = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(48000, 16, 1)
            };
            mic.DataAvailable += OnMicData;
            WaveInEvent? prev = null;
            lock (_captureLock)
            {
                if (!_started || _disposed)
                {
                    mic.Dispose();
                    return;
                }
                prev = _micCapture;
                _micCapture = mic;
                _micCapture.StartRecording();
            }
            mic = null;
            if (prev != null)
                _ = Task.Run(() => { try { SafeStopRecording(prev); prev.Dispose(); } catch { } });
            _logger.Info($"Microphone started: device #{deviceIndex}, 48kHz/16bit/mono", "Audio");
        }
        catch (Exception ex)
        {
            _logger.Error("Microphone init failed", ex, "Audio");
            mic?.Dispose();
            lock (_captureLock) { _micCapture = null; }
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
        // Сверяем sender с опубликованным полем: осиротевший capture
        // (перезаписанный перекрывающимся TryStart) данные не должен слать.
        if (_disposed || !ReferenceEquals(sender, _loopbackCapture)) return;
        try
        {
            var fmt = ((WasapiLoopbackCapture)sender!).WaveFormat;
            var samples = ConvertBytesToFloats(e.Buffer, e.BytesRecorded, fmt.BitsPerSample, fmt.Channels);
            var mono = Resampler.DownmixToMono(samples, fmt.Channels);
            var resampled = Resampler.ResampleLinear(mono, fmt.SampleRate, 1, _config.SampleRate);
            if (EnqueueOrDrop(_loopbackQueue, resampled))
            {
                Interlocked.Exchange(ref _emptyLoopPackets, 0);
            }
            else
            {
                // Пустой кадр (BytesRecorded==0) на простаивающем эндпойнте — НОРМА,
                // а не зомби: движок вывода спит и не гонит loopback-буфер. Рестартуем
                // ТОЛЬКО когда метр эндпойнта шумит, а пакеты пустые — настоящий
                // оборванный транспорт (живой кейс 2026-10-07). Иначе на тихом дефолте
                // сидели бы в бесконечном цикле рестартов и глушили очередь
                // (прод 2026-10-11: 458 рестартов за 26 минут тишины).
                if (IsLoopbackEndpointActive())
                {
                    var streak = Interlocked.Increment(ref _emptyLoopPackets);
                    if (streak >= EmptyPacketsBeforeRestart)
                    {
                        Interlocked.Exchange(ref _emptyLoopPackets, 0);
                        RequestLoopbackRestart($"{streak} consecutive empty packets with ACTIVE render endpoint");
                    }
                }
                else
                {
                    // На idle-эндпойнте счётчик не копим: при появлении звука стрик
                    // начнётся с нуля и не выстрелит «надуманным» рестартом.
                    Interlocked.Exchange(ref _emptyLoopPackets, 0);

                    // Звук мог уйти на ДРУГОЙ эндпойнт (звонок рендерит в BT-гарнитуру/
                    // HDMI, а мы слушаем колонки): ~раз в секунду сверяем метры и при
                    // явном лидере пересаживаем захват — чинит «[Динамик] без S-меток»
                    // и молчаливый loopback при смене устройства звонилкой.
                    // В явном выборе устройства из настроек — не лезем.
                    if (string.IsNullOrWhiteSpace(_config.LoopbackDeviceId) &&
                        Interlocked.Increment(ref _idleScanCounter) >= IdleScanEveryPackets)
                    {
                        Interlocked.Exchange(ref _idleScanCounter, 0);
                        var activeId = FindLoudestRenderEndpointId();
                        if (activeId != null && activeId != _loopbackDevice?.ID)
                        {
                            var name = TryGetDeviceName(activeId);
                            _logger.Info($"Loopback idle, но шумит '{name}' — пересаживаю захват", "Audio");
                            _autoLoopbackDeviceId = activeId;
                            RequestLoopbackRestart($"audio moved to '{name}'");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Loopback data callback failed", ex, "Audio");
            _diagnostics.DeviceInitError("loopback", ex);
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _micCapture)) return;
        try
        {
            var format = ((WaveInEvent)sender!).WaveFormat;
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

    private static bool EnqueueOrDrop(ConcurrentQueue<AudioFrame> queue, float[] samples)
    {
        // Пустой кадр НЕ ставим: WASAPI после глитча эндпойнта шлёт колбэки с
        // BytesRecorded=0 — живой capture валил бы пустые пакеты, hasLoop оставался true,
        // keepalive-тишина (loopPaused/micPaused) подавлялась, и VAD навсегда висел с
        // открытой фразой без единого лога (живой кейс 2026-10-07).
        if (samples.Length == 0) return false;
        var frame = new AudioFrame(samples, Stopwatch.GetTimestamp());
        queue.Enqueue(frame);
        while (queue.Count > MaxQueueSize)
        {
            queue.TryDequeue(out _);
        }
        return true;
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
    // То же для mic-очереди: «Мик выкл» выбрасывает кадры до буфера, канал обязан вести себя
    // как «мик молчит» — без тишины открытая фраза в mic-буфере не закроется до размьюта.
    private int _micIdleTicks;
    // ~180мс при тике 30мс. Запас не случаен: NAudio может отдавать колбэки крупными
    // порциями (до ~100мс), и очередь штатно пустует 3-4 тика подряд ПРИ ЖИВОМ звуке — меньший
    // гистерезис вставлял бы кадры тишины посреди речи. На закрытие фраз не влияет
    // (порог паузы ≥ 800мс).
    private const int IdleTicksBeforeSilence = 6;

    private void MixTick()
    {
        bool hasMic = _micQueue.TryDequeue(out var micFrame);
        bool hasLoop = _loopbackQueue.TryDequeue(out var loopFrame);
        // «Динамик выкл»: кадр выброшен ДО буфера — дальше канал ведёт себя как «рендер на
        // паузе» (гистерезис loopPaused ниже докармливает VAD тишиной и закрывает фразу).
        if (hasLoop && _loopbackMuted) hasLoop = false;
        if (hasLoop) _loopIdleTicks = 0;
        // «Мик выкл»: то же — кадр выброшен до буфера, канал ведёт себя как «мик молчит»
        // (micPaused ниже докармливает VAD тишиной и закрывает висящую фразу).
        if (hasMic && _micMuted) hasMic = false;
        if (hasMic) _micIdleTicks = 0;
        bool loopPaused = !hasLoop && LoopbackActive && ++_loopIdleTicks >= IdleTicksBeforeSilence;
        bool micPaused = !hasMic && MicActive && ++_micIdleTicks >= IdleTicksBeforeSilence;

        // Watchdog зависшего loopback (двойной буфер: IsSpeaking — чистый loopback-VAD,
        // микрофон не влияет). Фраза открыта >10с при полном отсутствии живых кадров =
        // захват зомби (пауза рендера фразу бы закрыла тишиной). Пустые пакеты до буфера
        // не доходят — их отсекает EnqueueOrDrop и ловит счётчик в OnLoopbackData.
        // Single-buffer режим без гейта по микрофону не покрывается: там _buffer делят
        // два источника и «мик говорит + рендер на паузе» от зомби не отличить.
        if (_micBuffer != null && LoopbackActive && _buffer.IsSpeaking && _loopIdleTicks > LoopStallTicks)
        {
            // Сброс окна при планировании: иначе каждый тик >10с триггерил бы рестарт
            // (pending-флаг дедуплицирует, но тик-счётчик должен отмерять новое окно).
            _loopIdleTicks = 0;
            RequestLoopbackRestart("speech hung >10s with no live frames");
        }

        if (_micBuffer != null)
        {
            // Dual-buffer mode: route loopback and mic to separate VAD pipelines (no mixing)
            float loopRms = 0f;
            if (hasLoop)
            {
                _buffer.AddSamples(loopFrame.Samples);
                LoopbackSamples?.Invoke(loopFrame.Samples);
                loopRms = Resampler.CalculateRms(loopFrame.Samples);
                _diagnostics.LogLevels(0f, loopRms, loopRms);
            }
            else if (loopPaused)
            {
                // Рендер на паузе → кормим VAD тишиной: висящая фраза закрывается по паузе,
                // уровень честно падает в ноль.
                var silence = new float[_config.SampleRate * MixerIntervalMs / 1000];
                _buffer.AddSamples(silence);
                LoopbackSamples?.Invoke(silence);
            }
            float micRms = 0f;
            if (hasMic)
            {
                _micBuffer.AddSamples(micFrame.Samples);
                micRms = Resampler.CalculateRms(micFrame.Samples);
            }
            else if (micPaused)
            {
                // Мик заглушён/очередь пуста → кормим mic-VAD тишиной, иначе открытая фраза
                // висела бы в буфере до размьюта (симметрично loopback keepalive выше).
                _micBuffer.AddSamples(new float[_config.SampleRate * MixerIntervalMs / 1000]);
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

        // Оба канала пусты: keepalive для single-buffer режима (loopback на паузе ИЛИ
        // mic-only с заглушённым микрофоном — иначе фраза висела бы до размьюта).
        if (loopPaused || micPaused)
        {
            var silence = new float[_config.SampleRate * MixerIntervalMs / 1000];
            _buffer.AddSamples(silence);
            // Тишина идёт в diar-стрим тоже (даже если это mic-keepalive): шкала стрима обязана
            // быть бит-в-бит шкалой буфера — иначе абсолютные индексы ChunkInfo разъезжаются.
            LoopbackSamples?.Invoke(silence);
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
        // В diar-стрим — только если кадр несёт loopback-составляющую (чистый mic-чанк не едем).
        // В смешанном кадре (mic+loop) отдаём mix целиком: он byte-в-byte совпадает с тем, что
        // ушёл в буфер, а шкала важнее чистоты сигнала (доопределено для редкой конфигурации).
        if (loopback.Length > 0)
            LoopbackSamples?.Invoke(mix);

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

    public bool TryGetChunk(out byte[] wav) => TryGetChunk(out wav, out _);

    /// <summary>Как <see cref="TryGetChunk(out byte[])"/>, плюс метаданные чанка
    /// (границы речи в абсолютных сэмплах входного потока — см. AudioBuffer.ChunkInfo).</summary>
    public bool TryGetChunk(out byte[] wav, out ChunkInfo info)
    {
        if (_buffer.TryGetReadyChunk(out wav, out info) && wav.Length > 0)
        {
            _diagnostics.ChunkSent(wav.Length);
            _diagnostics.VadTriggered();
            _logger?.Debug($"VAD chunk emitted: {wav.Length} bytes", "Audio");
            return true;
        }
        wav = Array.Empty<byte>();
        info = default;
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
        // Явный выбор/сброс устройства в настройках сильнее авто-привязки: чистим её,
        // чтобы захват не прилип к вчерашнему «шумящему» эндпойнту.
        if (wantedLoopbackId != _appliedLoopbackDeviceId) _autoLoopbackDeviceId = null;
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
