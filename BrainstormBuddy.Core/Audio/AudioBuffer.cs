using System.Text.RegularExpressions;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Audio;

/// <summary>
/// Pure C# Voice Activity Detection based on RMS energy + Zero-Crossing Rate.
/// Tuned to work on noisy audio (system loopback, video, music in background).
/// Mode 0..3 controls aggressiveness (lower threshold + tighter ZCR for higher modes).
/// No native dependencies, no ONNX, no WebRTC native DLL.
/// </summary>
public static class SimpleVad
{
    public readonly struct Config
    {
        public readonly double RmsThreshold;
        public readonly double ZcrMin;
        public readonly double ZcrMax;
        public readonly double SpectralFluxMin;

        public Config(double rmsThreshold, double zcrMin, double zcrMax, double spectralFluxMin)
        {
            RmsThreshold = rmsThreshold;
            ZcrMin = zcrMin;
            ZcrMax = zcrMax;
            SpectralFluxMin = spectralFluxMin;
        }

        public static Config ForMode(int mode) => mode switch
        {
            0 => new Config(0.030, 2.0, 60.0, 0.0),   // Quality — высокий RMS, широкий ZCR
            1 => new Config(0.022, 3.0, 55.0, 0.0),   // LowBitrate
            2 => new Config(0.014, 4.0, 45.0, 0.0005), // Aggressive — дефолт
            3 => new Config(0.006, 3.0, 40.0, 0.0010), // VeryAggressive — самый чувствительный (ZcrMin=3 для гласных)
            _ => new Config(0.014, 4.0, 45.0, 0.0005)
        };
    }

    /// <summary>
    /// Один 30ms фрейм (480 сэмплов @ 16kHz). Возвращает true если похоже на речь.
    /// Mode 0/1/2: только RMS (ZCR выключен — для совместимости со старыми тестами/silence-frames).
    /// Mode 3 (VeryAggressive): RMS + ZCR фильтр (отсекает broadband noise с малым ZCR).
    /// </summary>
    public static bool IsSpeech(float[] frame, in Config cfg, int mode, double? effectiveRmsThreshold = null)
    {
        if (frame == null || frame.Length == 0) return false;

        double sumSq = 0;
        int zeroCrossings = 0;
        int sign = frame[0] >= 0 ? 1 : -1;
        for (int i = 0; i < frame.Length; i++)
        {
            float s = frame[i];
            sumSq += s * s;
            int newSign = s >= 0 ? 1 : -1;
            if (newSign != sign)
            {
                zeroCrossings++;
                sign = newSign;
            }
        }
        double rms = Math.Sqrt(sumSq / frame.Length);
        double threshold = effectiveRmsThreshold ?? cfg.RmsThreshold;
        if (rms < threshold) return false;

        if (mode >= 3)
        {
            double zcr = (double)zeroCrossings / frame.Length * 100.0;
            if (zcr < cfg.ZcrMin || zcr > cfg.ZcrMax) return false;
        }

        return true;
    }
}

/// <summary>
/// Метаданные эмитнутого чанка для офлайн-анализа эндпойнтинга. Позиции — в сэмплах входного
/// потока (от начала подачи). Reason: "silence" (закрыт паузой), "forced" (лимит длины),
/// "flush" (хвост на EOF).
/// </summary>
public readonly record struct ChunkInfo(long OnsetSample, long SpeechEndSample, long EmitSample, int SampleRate, string Reason)
{
    public double OnsetSec => OnsetSample / (double)SampleRate;
    public double SpeechEndSec => SpeechEndSample / (double)SampleRate;
    public double EmitSec => EmitSample / (double)SampleRate;
    /// <summary>Задержка эндпойнта: от фактического конца речи до эмита (≈ порог тишины).</summary>
    public double EndpointLatencySec => Math.Max(0, (EmitSample - SpeechEndSample) / (double)SampleRate);
    /// <summary>Длительность речевой части чанка (без хвоста тишины).</summary>
    public double SpeechDurationSec => Math.Max(0, (SpeechEndSample - OnsetSample) / (double)SampleRate);
}

/// <summary>
/// Silence-based audio chunker with pure-C# VAD (RMS + ZCR).
/// Pipeline: PreRoll (300ms) → ActiveBuffer → PostRoll (300ms) → emit.
/// On emit, last OverlapMs (500ms) is kept as the next PreRoll/overlap.
/// Safety valve: force emit if MaxChunkSec reached.
/// </summary>
public class AudioBuffer
{
    private readonly object _lock = new();
    private readonly int _sampleRate;
    private readonly int _frameSamples;
    private readonly int _frameBytes;
    private int _preRollSamples;
    private int _postRollSamples;
    private readonly int _overlapSamples;
    private int _silenceMs;
    private readonly int _maxChunkSamples;
    private int _minSpeechMs;
    private readonly LoggingService? _logger;
    private readonly SimpleVad.Config _vadConfig;
    private readonly int _vadMode;
    private double _rmsFallbackThreshold;
    // Авто-калибровка порога распознавания под реальный уровень звука (шумовой пол × запас).
    // Чинит «тихий» звук (видео/loopback ~0.004 при фикс-пороге 0.01 не ловился). Выкл → ручной порог.
    // Дефолт false: включается ТОЛЬКО прокидкой из конфига (UpdateParameters) — прямые конструкции
    // (тесты, офлайн-харнессы эндпойнтинга) сохраняют детерминированный ручной порог.
    private bool _autoCalibrate;
    private double _noiseFloor = 0.003;
    // EMA сырого RMS для оценки пола: без сглаживания пол сползает к МИНИМУМАМ рваного шума
    // и порог оказывается ниже среднего шума → фантомная «речь». -1 = ещё не инициализирован.
    private double _smoothedRms = -1;
    // TickCount64 последнего принятого кадра: WASAPI loopback при паузе рендера перестаёт слать
    // буферы, без таймстампа _cachedRms «замерзает» и шкала уровня вечно рисует старое значение.
    private long _lastSampleTicks;

    private readonly List<float> _preRoll = new();
    private readonly List<float> _activeBuffer = new();
    private readonly List<float> _rawBuffer = new();
    private readonly List<float> _silenceTail = new();

    private bool _isSpeaking;
    private DateTime _speechStartTime = DateTime.MinValue;
    private int _silenceFrameCounter;
    private int _silenceFrameThreshold;
    private int _consecutiveSpeechFrames;
    private const int MinSpeechFrames = 2;
    // Латч-брейкер: кадры подряд НАД порогом при открытой фразе (сбрасывается любым тихим
    // кадром). Если звук сплошной ≥10с — эмитим чанк принудительно (катящаяся нарезка),
    // не дожидаясь ChunkMax: иначе непрерывное аудио даёт один бесполезный чанк в минуту.
    private int _aboveThrRun;
    private const int LatchFrames = 334; // ~10с при кадре 30мс
    // Микропровалы (щелчки компрессии, межсловные ямки видео 30-60мс) латч НЕ сбрасывают:
    // иначе непрерывное видео не ловится ни латчем (нужен сплошняк), ни паузой (нужна 1-2с)
    // и висит до 60с/ручной «молнии» (живой лог: active=56s, silFrames=3/33). Сброс — только
    // после заметной паузы ~240мс: у живой речи такие есть в каждой фразе, у стены звука — нет.
    private int _latchSilentRun;
    private const int LatchResetSilentFrames = 8; // ~240мс
    private int _framesProcessed;
    private int _vadTrueCount;
    private int _utterancesEmitted;
    private int _chunksSuppressed;
    private int _forcedEmits;
    private float _lastFrameRms;
    // Трекинг абсолютной позиции во входном потоке (сэмплы), для восстановления границ чанков
    // в офлайн-харнессе эндпойнтинга. Не влияет на аудио-логику.
    private long _consumedSamples;
    private long _onsetSample;
    private long _lastSpeechSample;

    // Адаптивный эндпойнтинг (opt-in). По умолчанию null → старое поведение байт-в-байт.
    private PauseAdaptiveController? _adaptive;
    private EndpointMode _endpointMode = EndpointMode.Manual;
    private int _rawSilenceRun;      // независимая сырая VAD-маска: длина текущей паузы (кадры)
    private int _rawSpeechConfirm;   // подряд speech-кадров (подтверждение возобновления речи)
    private readonly System.Timers.Timer? _vadLogTimer;
    private readonly TimeProvider _timeProvider;

    public AudioBuffer(
        int sampleRate,
        int chunkMaxSeconds,
        double silenceSeconds,
        double rmsThreshold)
        : this(sampleRate, chunkMaxSeconds, silenceSeconds, rmsThreshold,
               vadMode: 3, preRollMs: 400, postRollMs: 500, overlapMs: 800, minSpeechMs: 1000, logger: null)
    {
    }

    public AudioBuffer(
        int sampleRate,
        int chunkMaxSeconds,
        double silenceSeconds,
        double rmsThreshold,
        int vadMode,
        int preRollMs,
        int postRollMs,
        int overlapMs,
        int minSpeechMs,
        LoggingService? logger = null,
        TimeProvider? timeProvider = null)
    {
        if (sampleRate != 8000 && sampleRate != 16000 && sampleRate != 32000 && sampleRate != 48000)
            throw new ArgumentException($"SampleRate must be 8000/16000/32000/48000, got {sampleRate}", nameof(sampleRate));
        if (vadMode < 0 || vadMode > 3)
            throw new ArgumentException("VAD mode must be 0..3", nameof(vadMode));

        _sampleRate = sampleRate;
        _frameSamples = sampleRate * 30 / 1000;
        _frameBytes = _frameSamples * 2;
        _preRollSamples = sampleRate * preRollMs / 1000;
        _postRollSamples = sampleRate * postRollMs / 1000;
        _overlapSamples = sampleRate * overlapMs / 1000;
        _silenceMs = (int)(silenceSeconds * 1000);
        _maxChunkSamples = sampleRate * chunkMaxSeconds;
        _minSpeechMs = minSpeechMs;
        _rmsFallbackThreshold = rmsThreshold;
        _logger = logger;
        _vadConfig = SimpleVad.Config.ForMode(vadMode);
        _vadMode = vadMode;

        _silenceFrameThreshold = Math.Max(1, _silenceMs / 30);
        _timeProvider = timeProvider ?? TimeProvider.System;

        _logger?.Info($"AudioBuffer: {sampleRate}Hz, max={chunkMaxSeconds}s, silence={_silenceMs}ms, preRoll={preRollMs}ms, postRoll={postRollMs}ms, overlap={overlapMs}ms, minSpeech={minSpeechMs}ms, vad_mode={vadMode} (rms>={_vadConfig.RmsThreshold:F4}, zcr=[{_vadConfig.ZcrMin:F1},{_vadConfig.ZcrMax:F1}])", "Audio");

        // Periodic VAD level logger (every 5s)
        if (_logger != null)
        {
            _vadLogTimer = new System.Timers.Timer(5000) { AutoReset = true };
            _vadLogTimer.Elapsed += (s, e) =>
            {
                lock (_lock)
                {
                    var bufSec = _activeBuffer.Count / (double)_sampleRate;
                    var preSec = _preRoll.Count / (double)_sampleRate;
                    _logger?.Info(
                        $"VAD-Level: state={(_isSpeaking ? "SPEECH" : "silence")} rms={_lastFrameRms:F4} thr={EffectiveRmsThreshold():F4}{(_autoCalibrate ? $" (auto,floor={_noiseFloor:F4})" : "")} " +
                        $"active={bufSec:F1}s preRoll={preSec:F2}s silFrames={_silenceFrameCounter}/{_silenceFrameThreshold} " +
                        $"frames={_framesProcessed} speech%={(_framesProcessed == 0 ? 0 : 100.0 * _vadTrueCount / _framesProcessed):F0}%",
                        "Audio");
                }
            };
            _vadLogTimer.Start();
        }
    }

    public int CurrentSampleCount
    {
        get { lock (_lock) return _activeBuffer.Count + _preRoll.Count; }
    }

    private float _lastComputedRms;
    private volatile float _cachedRms;

    // Возвращает 0, если кадры давно не приходили: WASAPI loopback при паузе рендера вообще
    // не шлёт буферы — без «протухания» последнее речевое значение замерзает и шкала уровня
    // в оверлее рисует бесконечные красные столбики (реальный баг с ноута).
    public float CurrentRms
    {
        get
        {
            var age = Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastSampleTicks);
            return age > 400 ? 0f : _cachedRms;
        }
    }

    /// <summary>Текущий фактический порог детекции речи (в авто-режиме — калиброванный по шуму).</summary>
    public double CurrentEffectiveThreshold { get { lock (_lock) return EffectiveRmsThreshold(); } }
    public bool IsAutoCalibrating { get { lock (_lock) return _autoCalibrate; } }

    public int UtterancesEmitted => _utterancesEmitted;
    public int ChunksSuppressed => _chunksSuppressed;
    public int ForcedEmits => _forcedEmits;
    public bool VadAvailable => true;
    public double VadAgreement => _framesProcessed == 0 ? 0 : (double)_vadTrueCount / _framesProcessed;

    public void AddSamples(float[] samples)
    {
        if (samples == null || samples.Length == 0) return;

        lock (_lock)
        {
            _rawBuffer.AddRange(samples);

            while (_rawBuffer.Count >= _frameSamples)
            {
                var frame = new float[_frameSamples];
                _rawBuffer.CopyTo(0, frame, 0, _frameSamples);
                _rawBuffer.RemoveRange(0, _frameSamples);

                _lastFrameRms = Resampler.CalculateRms(frame);
                _cachedRms = _lastFrameRms;
                _lastSampleTicks = Environment.TickCount64;
                // Оценка шумового пола для авто-калибровки. Робастность:
                //  • пол считается по СГЛАЖЕННОМУ RMS (EMA), а не по сырым кадрам — иначе на рваном
                //    микрофонном шуме он сползает к минимумам и порог падает ниже среднего шума;
                //  • кадры цифровой тишины (rms < 1e-4: loopback на паузе, нули плеера) — это
                //    отсутствие сигнала, а не шумовой пол — игнорируем, порог не рушится к клампу;
                //  • во время речи пол заморожен — речь не должна калибровать порог сама под себя;
                //  • вверх восстанавливаемся за секунды (0.005/кадр), а не за минуты.
                if (_autoCalibrate && _lastFrameRms >= 1e-4f)
                {
                    if (_smoothedRms < 0) _smoothedRms = _lastFrameRms;
                    _smoothedRms += (_lastFrameRms - _smoothedRms) * 0.15;
                    if (!_isSpeaking)
                    {
                        if (_smoothedRms < _noiseFloor) _noiseFloor += (_smoothedRms - _noiseFloor) * 0.05;
                        else _noiseFloor += (_smoothedRms - _noiseFloor) * 0.005;
                    }
                    else if (_smoothedRms > _noiseFloor)
                    {
                        // Анти-защёлка: ПОЛНАЯ заморозка пола при _isSpeaking давала мёртвый клин
                        // (баг 2.5.5) — на непрерывном звуке порог оставался пришпилен к тихой
                        // комнате, «тишина» не наступала никогда и фраза не закрывалась вообще.
                        // Медленный дрейф вверх (τ≈60с) обычную реплику в 2-10с не трогает,
                        // но затяжной сплошной звук постепенно переклассифицирует в фон.
                        _noiseFloor += (_smoothedRms - _noiseFloor) * 0.0005;
                    }
                }
                bool isSpeech = SimpleVad.IsSpeech(frame, _vadConfig, _vadMode, EffectiveRmsThreshold());
                _framesProcessed++;
                if (isSpeech) _vadTrueCount++;

                // Независимая сырая VAD-маска для адаптивного порога: наблюдаем паузы ДО
                // логики эндпойнтинга, поэтому выборка не цензурируется порогом (анти-коллапс).
                if (_adaptive != null && _endpointMode == EndpointMode.AdaptivePauses)
                    TrackRawPauseLocked(isSpeech);

                ProcessFrame(isSpeech, frame);
                _consumedSamples += _frameSamples;
            }
        }
    }

    // Множитель запаса над шумовым полом в авто-калибровке — возвращает режимам VAD смысл:
    // раньше режимы 0/1/2 отличались только абсолютным RmsThreshold, который эффективный порог
    // (авто или ползунок) полностью перекрывал — юзер щёлкал режимы, не менялось ничего.
    private static double AutoMarginForMode(int mode) => mode switch
    {
        0 => 4.5,   // консервативный — минимум ложных срабатываний
        1 => 4.0,
        2 => 3.5,   // стандарт
        3 => 2.8,   // чувствительный (+ ZCR-фильтр шума в SimpleVad)
        _ => 3.5
    };

    // Эффективный порог RMS для детекции речи. Авто: шумовой пол × запас режима (зажат в разумный
    // диапазон, чтобы не ловить шум и не глохнуть на тихом). Ручной: значение ползунка —
    // авторитетно на всём диапазоне (0.001–0.1). Раньше здесь был Math.Min с порогом VAD-режима
    // (0.006 для агрессивного) — он капал ползунок сверху, и верхняя половина была мёртвой:
    // юзер не мог поднять порог, чтобы отсечь шум. Теперь ручной порог = ровно то, что выставил юзер.
    private double EffectiveRmsThreshold()
        => _autoCalibrate
            ? Math.Clamp(_noiseFloor * AutoMarginForMode(_vadMode), 0.0018, 0.05)
            : _rmsFallbackThreshold;

    private void ProcessFrame(bool isSpeech, float[] frame)
    {
        // Roll pre-roll buffer (always last PreRollSamples).
        // RemoveRange одним вызовом вместо RemoveAt(0) в цикле — поведение идентично,
        // но O(n) один раз, а не на каждый элемент (важно для офлайн-bulk прогонов).
        _preRoll.AddRange(frame);
        if (_preRoll.Count > _preRollSamples)
            _preRoll.RemoveRange(0, _preRoll.Count - _preRollSamples);

        if (isSpeech)
        {
            _consecutiveSpeechFrames++;
            if (!_isSpeaking)
            {
                if (_consecutiveSpeechFrames < MinSpeechFrames)
                    return;
                _isSpeaking = true;
                _onsetSample = _consumedSamples;
                _speechStartTime = _timeProvider.GetUtcNow().DateTime;
                _silenceFrameCounter = 0;
                _silenceTail.Clear();
                _activeBuffer.AddRange(_preRoll);
                _activeBuffer.AddRange(frame);
                _logger?.Debug($"VAD: speech start (consecutive={_consecutiveSpeechFrames}, preRoll={_preRollSamples}spls copied, active={_activeBuffer.Count})", "Audio");
            }
            else
            {
                if (_silenceTail.Count > 0)
                {
                    _activeBuffer.AddRange(_silenceTail);
                    _silenceTail.Clear();
                }
                _activeBuffer.AddRange(frame);
            }
            _silenceFrameCounter = 0;
            _lastSpeechSample = _consumedSamples + _frameSamples;
        }
        else
        {
            _consecutiveSpeechFrames = 0;
            if (_isSpeaking)
            {
                _silenceFrameCounter++;
                _silenceTail.AddRange(frame);
                int maxTail = _postRollSamples * 2;
                if (_silenceTail.Count > maxTail)
                    _silenceTail.RemoveRange(0, _silenceTail.Count - maxTail);
            }
        }

        // Латч-брейкер: кадры звука «без заметной паузы» открытой фразы.
        if (_isSpeaking && isSpeech) { _aboveThrRun++; _latchSilentRun = 0; }
        else if (!isSpeech && ++_latchSilentRun >= LatchResetSilentFrames) _aboveThrRun = 0;
    }

    public bool HasCompleteUtterance()
    {
        lock (_lock)
        {
            if (!_isSpeaking)
                return _activeBuffer.Count >= _maxChunkSamples;
            return _silenceFrameCounter >= _silenceFrameThreshold
                || _activeBuffer.Count >= _maxChunkSamples
                || _aboveThrRun >= LatchFrames;
        }
    }

    public bool TryGetReadyChunk(out byte[] wav) => TryGetReadyChunk(out wav, out _);

    /// <summary>
    /// Как <see cref="TryGetReadyChunk(out byte[])"/>, но дополнительно отдаёт метаданные чанка
    /// (границы речи в сэмплах входного потока + причину эмита) для офлайн-харнесса эндпойнтинга.
    /// </summary>
    public bool TryGetReadyChunk(out byte[] wav, out ChunkInfo info)
    {
        lock (_lock)
        {
            wav = Array.Empty<byte>();
            info = default;
            if (!_isSpeaking)
            {
                if (_activeBuffer.Count >= _maxChunkSamples)
                {
                    _forcedEmits++;
                    _utterancesEmitted++;
                    info = new ChunkInfo(_onsetSample, _lastSpeechSample, _consumedSamples, _sampleRate, "forced");
                    wav = ExtractWavLocked();
                }
                return wav.Length > 0;
            }

            var byMax = _activeBuffer.Count >= _maxChunkSamples;
            var bySilence = _silenceFrameCounter >= _silenceFrameThreshold;
            // Латч-брейкер: ≥10с сплошного звука без единого тихого кадра → катящийся эмит.
            // Без него непрерывное аудио (видео без пауз / порог ниже уровня фона) не резалось
            // до ChunkMax=60с — «красная стена, а текста нет» (баг 2.5.5).
            var byLatch = _aboveThrRun >= LatchFrames;
            if (bySilence || byMax || byLatch)
            {
                var speechDuration = (_timeProvider.GetUtcNow().DateTime - _speechStartTime).TotalMilliseconds;
                if (speechDuration < _minSpeechMs)
                {
                    _chunksSuppressed++;
                    _logger?.Debug($"VAD: suppressed too-short utterance (speech_ms={speechDuration:F0}, min={_minSpeechMs})", "Audio");
                    ResetStateLocked();
                    return false;
                }
                // Форс по лимиту длины/латчу во время речи (silence-порог не сработал) — forced.
                // Раньше _forcedEmits не считал этот основной кейс (только ветку !_isSpeaking).
                bool forced = (byMax || byLatch) && !bySilence;
                if (forced) _forcedEmits++;
                _utterancesEmitted++;
                var reason = bySilence ? "silence" : (byLatch && !byMax ? "latch" : "forced");
                info = new ChunkInfo(_onsetSample, _lastSpeechSample, _consumedSamples, _sampleRate, reason);
                _logger?.Debug($"VAD: {reason} emission (silence_frames={_silenceFrameCounter}, threshold={_silenceFrameThreshold}, aboveThrRun={_aboveThrRun}, speech_ms={speechDuration:F0}, active_samples={_activeBuffer.Count})", "Audio");
                wav = ExtractWavLocked();
                return true;
            }

            return false;
        }
    }

    public bool Flush(out byte[] wav) => Flush(out wav, out _);

    public bool Flush(out byte[] wav, out ChunkInfo info)
    {
        lock (_lock)
        {
            info = default;
            if (!_isSpeaking || _activeBuffer.Count < _sampleRate * _minSpeechMs / 1000)
            {
                wav = Array.Empty<byte>();
                return false;
            }
            info = new ChunkInfo(_onsetSample, _lastSpeechSample, _consumedSamples, _sampleRate, "flush");
            _utterancesEmitted++;
            wav = ExtractWavLocked();
            return true;
        }
    }

    private byte[] ExtractWavLocked()
    {
        if (_activeBuffer.Count == 0) return Array.Empty<byte>();

        var postRoll = _silenceTail.Count >= _postRollSamples
            ? _silenceTail.GetRange(_silenceTail.Count - _postRollSamples, _postRollSamples)
            : new List<float>(_silenceTail);

        var samples = new List<float>(_activeBuffer);
        samples.AddRange(postRoll);

        var wav = WavEncoder.EncodeWav(samples.ToArray(), _sampleRate);
        ResetStateLocked();
        return wav;
    }

    public byte[] GetChunkForTranscription()
    {
        lock (_lock)
        {
            if (_activeBuffer.Count == 0) return Array.Empty<byte>();

            var postRoll = _silenceTail.Count >= _postRollSamples
                ? _silenceTail.GetRange(_silenceTail.Count - _postRollSamples, _postRollSamples)
                : new List<float>(_silenceTail);

            var samples = new List<float>(_activeBuffer);
            samples.AddRange(postRoll);

            return WavEncoder.EncodeWav(samples.ToArray(), _sampleRate);
        }
    }

    public void Reset()
    {
        lock (_lock) ResetStateLocked();
    }

    private void ResetStateLocked([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var overlapSource = _activeBuffer.Count >= _overlapSamples
            ? _activeBuffer.GetRange(_activeBuffer.Count - _overlapSamples, _overlapSamples)
            : new List<float>(_activeBuffer);

        // NE Очищаем preRoll — сохраняем контекст для следующей фразы.
        // preRoll непрерывно滚動 через ProcessFrame, и при сбросе
        // перезаполняется overlap'ом из activeBuffer (хвост предыдущей фразы).
        // Это гарантирует что начало следующей фразы НЕ обрезается.
        _preRoll.Clear();
        _preRoll.AddRange(overlapSource);
        _activeBuffer.Clear();
        _silenceTail.Clear();
        _isSpeaking = false;
        _silenceFrameCounter = 0;
        _consecutiveSpeechFrames = 0;
        _aboveThrRun = 0;
        _latchSilentRun = 0;
        _speechStartTime = DateTime.MinValue;

        _logger?.Debug($"Buffer reset (via {caller}), kept {overlapSource.Count} overlap samples (={overlapSource.Count * 1000 / _sampleRate}ms)", "Audio");
    }

    /// <summary>Включить адаптивный эндпойнтинг: порог тишины ведёт контроллер по паузам говорящего.</summary>
    public void EnableAdaptiveEndpointing(PauseAdaptiveController controller)
    {
        lock (_lock)
        {
            _adaptive = controller;
            _endpointMode = EndpointMode.AdaptivePauses;
            ApplyAdaptiveThresholdLocked(controller.AppliedSeconds);
        }
    }

    public void SetEndpointMode(EndpointMode mode)
    {
        lock (_lock) _endpointMode = mode;
    }

    public EndpointMode EndpointMode { get { lock (_lock) return _endpointMode; } }

    /// <summary>Текущий фактический порог тишины (в авто-режиме — выбранный контроллером).</summary>
    public double CurrentSilenceSeconds { get { lock (_lock) return _silenceMs / 1000.0; } }

    // Наблюдение сырой паузной маски (под _lock, из AddSamples).
    private void TrackRawPauseLocked(bool isSpeech)
    {
        if (isSpeech)
        {
            _rawSpeechConfirm++;
            if (_rawSpeechConfirm == MinSpeechFrames && _rawSilenceRun > 0)
            {
                // Подтверждённое возобновление речи → _rawSilenceRun это была внутриречевая пауза.
                _adaptive!.RecordGap(_rawSilenceRun);
                _rawSilenceRun = 0;
                if (_adaptive.TryConsumeChange(out double sec))
                    ApplyAdaptiveThresholdLocked(sec);
            }
        }
        else
        {
            _rawSpeechConfirm = 0;
            if (_rawSilenceRun <= _adaptive!.MaxGapFrames + 2) _rawSilenceRun++; // не растим бесконечно
        }
    }

    private void ApplyAdaptiveThresholdLocked(double sec)
    {
        _silenceMs = (int)(sec * 1000);
        _silenceFrameThreshold = Math.Max(1, _silenceMs / 30);
        _logger?.Info($"Adaptive endpoint → {sec:F2}s (frames={_silenceFrameThreshold}, " +
                      $"p85={_adaptive!.LastP85Ms:F0}ms, n={_adaptive.SampleCount})", "Audio");
    }

    public void UpdateParameters(double rmsThreshold, double silenceSeconds, int preRollMs, int postRollMs, int minSpeechMs, bool autoCalibrate)
    {
        lock (_lock)
        {
            _rmsFallbackThreshold = rmsThreshold;
            _autoCalibrate = autoCalibrate;
            // В авто-режиме порог тишины держит контроллер — ручной аргумент игнорируем.
            if (_endpointMode != EndpointMode.AdaptivePauses)
            {
                _silenceMs = (int)(silenceSeconds * 1000);
                _silenceFrameThreshold = Math.Max(1, _silenceMs / 30);
            }
            _preRollSamples = _sampleRate * preRollMs / 1000;
            _postRollSamples = _sampleRate * postRollMs / 1000;
            _minSpeechMs = minSpeechMs;
            _logger?.Info($"AudioBuffer live update: rms={rmsThreshold:F4}, silence={silenceSeconds:F2}s ({_silenceMs}ms, frames={_silenceFrameThreshold}), preRoll={preRollMs}ms, postRoll={postRollMs}ms, minSpeech={minSpeechMs}ms, mode={_endpointMode}", "Audio");
        }
    }
}

public static class WavEncoder
{
    public static byte[] EncodeWav(float[] samples, int sampleRate)
    {
        const short bitsPerSample = 16;
        const short channels = 1;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var dataSize = samples.Length * 2;
        var fileSize = 36 + dataSize;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(fileSize);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        foreach (var s in samples)
        {
            var clipped = Math.Max(-1f, Math.Min(1f, s));
            writer.Write((short)(clipped * short.MaxValue));
        }

        return ms.ToArray();
    }
}

/// <summary>
/// Очистка STT-текста: убирает ааа/эээ/ууу, повторы слов, обрывки. Дропает мусор до LLM.
/// </summary>
public static class TextPostProcessor
{
    private static readonly Regex RepeatedCharRegex = new(@"([а-яёa-z])\1{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Повтор слова через запятую/пробел: "хорох, хорох, хорох" → "хорох"
    private static readonly Regex RepeatedWordRegex = new(@"\b(\S+)(\s*[,\s]+\s*\1\b){1,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "слово, слово, слово, слово" → "слово" (тот же regex, но без word boundary на повторах, ловит через запятую)
    private static readonly Regex CommaRepeatedWordRegex = new(@"\b(\w+)(?:\s*,\s*\1)+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MultiPunctRegex = new(@"([,.!?;:])\1{1,}", RegexOptions.Compiled);
    private static readonly Regex FillerRunRegex = new(@"\b(э+[э]?|а+[а]?|у+[у]?|м+[м]?|и+[и]?|ну+|вот+|типа|как\s+бы|это\s+самое|короче)\b[\s,]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // «да»/«нет»/«ага»/«угу» здесь БЫЛИ — и молча съедали реальные ответы юзера
    // (живой тест: «да», «ага», «давай» распознавались, но не показывались). Это осмысленные
    // реплики, а не паразиты; фильтр оставлен только для настоящего мусора.
    private static readonly HashSet<string> FillerOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "э", "ээ", "эээ", "а", "аа", "ааа", "у", "уу", "ууу", "м", "мм", "и", "ии",
        "ну", "вот", "типа", "как бы", "это", "это самое", "короче"
    };

    public record CleanResult(string Text, bool IsValid, string Reason);

    public static CleanResult Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new CleanResult(string.Empty, false, "empty");

        var s = raw.Trim();

        s = RepeatedCharRegex.Replace(s, "$1$1");
        // Сначала режем "слово, слово, слово" → "слово"
        s = CommaRepeatedWordRegex.Replace(s, "$1");
        // Потом "слово слово слово" → "слово"
        s = RepeatedWordRegex.Replace(s, "$1");
        s = MultiPunctRegex.Replace(s, "$1");
        s = FillerRunRegex.Replace(s, " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"\s+([,.!?;:])", "$1");
        // Убираем висящие запятые в конце
        s = s.TrimEnd(',', '.', ' ');

        if (string.IsNullOrWhiteSpace(s)) return new CleanResult(string.Empty, false, "empty-after-cleanup");

        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int meaningful = 0;
        foreach (var w in words)
        {
            var wLower = w.Trim(',', '.', '!', '?', ';', ':', '"', '«', '»', '(', ')').ToLowerInvariant();
            if (wLower.Length < 2) continue;
            if (FillerOnly.Contains(wLower)) continue;
            meaningful++;
        }

        if (meaningful < 3)
            return new CleanResult(s, false, $"only {meaningful} meaningful words");

        if (s.Length < 8)
            return new CleanResult(s, false, "too short");

        return new CleanResult(s, true, "ok");
    }
}
