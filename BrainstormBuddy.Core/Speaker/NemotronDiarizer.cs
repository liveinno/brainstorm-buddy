using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Speaker;

/// <summary>
/// IStreamingSpeakerDiarizer поверх Nemotron-3 Sortformer (GGUF, nemo_speech C-ABI).
/// Стримы нативной библиотеки однопоточные по контракту, поэтому вся работа
/// сериализуется: аудио уходит в bounded-канал (drop-oldest), единственный
/// worker делает push_f32, а IdentifyRange/Reset/Dispose берут тот же _gate,
/// что гарантирует отсутствие конкурентных вызовов внутри одного стрима.
/// Индексы IdentifyRange считаются в кумулятивной шкале PushLoopback
/// (_offeredSamples). Drop-oldest рвёт эту шкалу (потерянные сэмплы не доходят
/// до стрима, но индексы вызывающего их учитывают), поэтому при устойчивом
/// переполнении очереди (&gt; ~5 с подряд) worker делает авто-ресинк: дропает
/// накопленное, пересоздаёт стрим и заново привязывает базу — IdentifyRange
/// снова получает честные спаны ценой потери истории диаризации и рестарта
/// нумерации S&lt;n&gt; (см. StreamEpoch).
/// Методы не бросают — любой сбой -&gt; WARN + null/disabled.
/// </summary>
public sealed class NemotronDiarizer : IStreamingSpeakerDiarizer
{
    private const string LogCategory = "Speaker";
    private const string DefaultModelFile = "Nemotron-3-Diarization.Q8_0.gguf";
    private const int ChannelCapacity = 512;
    // Гистерезис «очередь переполнена»: эпизод считается живым, пока очередь не
    // опустеет ниже RecoverLevel — при ~33 push/сек очередь колеблется
    // 512↔511 и сброс по Count<Capacity глушил ресинк навсегда (прод 2.27.1).
    // Чистый state-автомат — логика покрыта юнит-тестами (OverflowTracker).
    private const int QueueRecoverLevel = ChannelCapacity / 4;
    // Worker склеивает накопленные мелкие фреймы до этого размера — меньше
    // нативных вызовов на секунду аудио, drain быстрее (см. WorkerLoop).
    private const int CoalesceSamples = 16000; // ~1с при 16 кГц
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly record struct PushItem(float[] Samples, int SampleRate);

    private readonly IntPtr _model;
    private readonly LoggingService? _logger;
    private readonly string _preset;
    private readonly Channel<PushItem> _queue =
        Channel.CreateBounded<PushItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly Task _worker;
    private readonly SemaphoreSlim _gate = new(1, 1); // сериализация всех вызовов в нативный стрим
    private readonly HashSet<string> _seenSpeakers = new();

    private IntPtr _stream;
    private int _pushRate;            // rate первого push за жизнь объекта (0 = ещё нет)
    private long _pushedSamples;      // сэмплы, реально принятые worker'ом, кумулятивно от первого push
    private long _offeredSamples;     // сэмплы, предложенные PushLoopback, кумулятивно — шкала вызывающего (не убывает при drop)
    private long _streamBaseSamples;  // база стрима в шкале вызывающего (_offeredSamples на момент открытия)
    private int _streamEpoch;         // поколение нативного стрима (Reset/авто-ресинк) — рестарт нумерации S<n>
    private int _resyncWarned;        // WARN про авто-ресинк — один раз на эпизод (повторные идут в Info)
    private int _resyncRequested;     // worker должен выполнить авто-ресинк стрима (устойчивое переполнение)
    private readonly OverflowTracker _overflow = new(ChannelCapacity, QueueRecoverLevel, (long)OverflowResyncAfter.TotalMilliseconds);
    private volatile bool _disposed;

    // Фактический бэкенд последнего живого инстанса — читает VM настроек
    // (Core-VM не видит экземпляр). Доступ только через Volatile/Interlocked.
    private static string? _lastActiveBackend;

    /// <summary>Бэкенд, реально задействованный активным диаризатором; null — не запущен.</summary>
    public static string? LastActiveBackend => Volatile.Read(ref _lastActiveBackend);

    /// <summary>Бэкенд, на котором реально поднялся этот инстанс (cpu/vulkan/cuda).</summary>
    internal string? EffectiveBackend { get; }
    private NemotronDiarizer(IntPtr model, IntPtr stream, LoggingService? logger,
        string preset, string? effectiveBackend)
    {
        _model = model;
        _stream = stream;
        _logger = logger;
        _preset = preset;
        EffectiveBackend = effectiveBackend;
        _worker = Task.Run(WorkerLoop);
    }

    /// <summary>Готов, если нативный стрим открыт и объект не disposed.</summary>
    public bool IsReady => !_disposed && Volatile.Read(ref _stream) != IntPtr.Zero;

    /// <summary>
    /// Null, если диаризация выключена, нет бандла/модели или нативный
    /// create вернул ошибку.
    /// </summary>
    public static NemotronDiarizer? TryCreate(SpeakerDiarizationConfig cfg, LoggingService? logger = null)
    {
        try
        {
            if (!cfg.Enabled) return null;
            var (ok, dir, error) = NemotronBundle.TryLoad(cfg.NemotronBackend, logger);
            if (!ok || dir == null)
            {
                logger?.Warn($"Speaker diarization disabled: {error}", LogCategory);
                return null;
            }

            var modelPath = ResolveModelPath(cfg);
            if (modelPath == null)
            {
                logger?.Warn($"Speaker diarization disabled: {DefaultModelFile} not found " +
                             "(NemotronModelPath, %APPDATA%/BrainstormBuddy/models, <app>/models, artifacts/speaker)",
                             LogCategory);
                return null;
            }

            var preset = string.IsNullOrWhiteSpace(cfg.NemotronPreset) ? null : cfg.NemotronPreset.Trim();
            var diarizer = CreateModel(dir, cfg.NemotronBackend, modelPath, preset, logger);
            if (diarizer != null)
                Volatile.Write(ref _lastActiveBackend, diarizer.EffectiveBackend); // статус: requested → actual
            return diarizer;
        }
        catch (Exception ex)
        {
            logger?.Warn($"Speaker diarization init failed: {ex.Message}", LogCategory);
            return null;
        }
    }

    // Ищем GPU=0 на gpu-бандлах (vulkan/cuda), иначе сразу CPU. Неудача create
    // на одном индексе -> следующий кандидат (gpu=0 может не иметь устройства).
    private static NemotronDiarizer? CreateModel(string bundleDir, string backend,
        string modelPath, string? preset, LoggingService? logger)
    {
        var cpuOnly = backend?.Trim().Equals("cpu", StringComparison.OrdinalIgnoreCase) == true
                      || bundleDir.Contains("x-cpu", StringComparison.OrdinalIgnoreCase);
        int[] gpuCandidates = cpuOnly ? new[] { -1 } : new[] { 0, -1 };

        IntPtr modelPtr = IntPtr.Zero;
        var gpuUsed = -1; // индекс gpu, на котором create удался (-1 = cpu-режим)
        var pathPtr = Marshal.StringToCoTaskMemUTF8(modelPath);
        var presetPtr = preset != null ? Marshal.StringToCoTaskMemUTF8(preset) : IntPtr.Zero;
        try
        {
            var mc = new NemoDiarModelConfig
            {
                Size = (nuint)Marshal.SizeOf<NemoDiarModelConfig>(),
                ModelPath = pathPtr,
                Preset = presetPtr,
                // остальные int-поля = 0 -> preset-дефолты; left_context: <0 keep preset
                LeftContextFrames = -1,
            };
            foreach (var gpu in gpuCandidates)
            {
                mc.Gpu = gpu;
                var st = NemoSpeechNative.nemo_speech_diar_create(in mc, out modelPtr);
                if (st == NemoStatus.Ok && modelPtr != IntPtr.Zero)
                {
                    gpuUsed = gpu;
                    if (gpu == -1 && gpuCandidates.Length > 1)
                        logger?.Warn("Nemotron: GPU create failed, running on CPU", LogCategory);
                    break;
                }
                logger?.Warn($"Nemotron diar_create(gpu={gpu}) failed: {st} {NemoSpeechNative.LastErrorString()}", LogCategory);
                modelPtr = IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
            if (presetPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(presetPtr);
        }

        try
        {
            if (modelPtr == IntPtr.Zero)
                return null;

            var stOpen = NemoSpeechNative.nemo_speech_diar_stream_open(modelPtr, out var streamPtr);
            if (stOpen != NemoStatus.Ok || streamPtr == IntPtr.Zero)
            {
                logger?.Warn($"Nemotron stream_open failed: {stOpen} {NemoSpeechNative.LastErrorString()}", LogCategory);
                NemoSpeechNative.nemo_speech_diar_destroy(modelPtr);
                return null;
            }

            var eff = EffectiveBackendName(bundleDir, gpuUsed, backend);
            var d = new NemotronDiarizer(modelPtr, streamPtr, logger, preset ?? "default", eff);
            logger?.Info($"Nemotron diarizer ready: backend={eff}, gpu={gpuUsed}, model={modelPath}, " +
                         $"preset={d._preset}, " +
                         $"speakers={NemoSpeechNative.nemo_speech_diar_num_speakers(modelPtr)}, " +
                         $"spf={NemoSpeechNative.nemo_speech_diar_seconds_per_frame(modelPtr):F3}s, " +
                         $"bundle={bundleDir}", LogCategory);
            return d;
        }
        catch (Exception ex)
        {
            logger?.Warn($"Nemotron init failed: {ex.Message}", LogCategory);
            if (modelPtr != IntPtr.Zero)
                NemoSpeechNative.nemo_speech_diar_destroy(modelPtr);
            return null;
        }
    }

    // Фактический бэкенд по итогу create: gpu<0 → cpu-режим (vulkan-бандл умеет),
    // gpu>=0 → тип бандла по сателлиту (ggml-cuda/ggml-vulkan в каталоге) или
    // dev-тегу x-<be> в пути; крайний фолбэк — явный backend из конфига.
    private static string? EffectiveBackendName(string bundleDir, int gpuUsed, string? requested)
    {
        if (gpuUsed < 0)
            return "cpu";
        if (File.Exists(Path.Combine(bundleDir, "ggml-cuda.dll"))
            || bundleDir.Contains("x-cuda", StringComparison.OrdinalIgnoreCase))
            return "cuda";
        if (File.Exists(Path.Combine(bundleDir, "ggml-vulkan.dll"))
            || bundleDir.Contains("x-vulkan", StringComparison.OrdinalIgnoreCase))
            return "vulkan";
        var req = requested?.Trim().ToLowerInvariant();
        return req is "cpu" or "vulkan" or "cuda" ? req : "gpu";
    }

    // Очередь держится полной дольше этого окна → бэкенд стабильно не успевает,
    // drop-oldest разорвал шкалу индексов → авто-ресинк стрима (worker).
    private static readonly TimeSpan OverflowResyncAfter = TimeSpan.FromSeconds(5);

    /// <summary>Поколение нативного стрима (см. IStreamingSpeakerDiarizer.StreamEpoch).</summary>
    public int StreamEpoch => Volatile.Read(ref _streamEpoch);

    public void PushLoopback(float[] samples, int sampleRate)
    {
        if (_disposed || samples is not { Length: > 0 } || sampleRate <= 0)
            return;
        try
        {
            // Шкала вызывающего — КАЖДЫЙ предложенный сэмпл (IdentifyRange
            // адресуется ею); drop-oldest ниже не отматывает этот счётчик,
            // что и ломает маппинг при переполнении.
            Interlocked.Add(ref _offeredSamples, samples.Length);

            // Drop-oldest молча вытесняет хвост очереди. Каждый вытесненный чанк —
            // дыра в стриме: нативная ось времени отстаёт от шкалы вызывающего, и
            // IdentifyRange начинает получать сдвинутые/пустые диапазоны навсегда.
            // Мгновенно починить маппинг нельзя (положения дыр неизвестны), поэтому
            // при УСТОЙЧИВОМ переполнении (> OverflowResyncAfter) ставим флаг
            // ресинка — worker пересоздаст стрим с честной базой. Короткие
            // всплески (контенция на IdentifyRange, CPU-спайк) сдвигают метки
            // лишь на доли секунды — переживаем их без сброса истории.
            var ov = _overflow.Track(_queue.Reader.Count, Environment.TickCount64);
            if (ov.DropWarn)
                _logger?.Warn("Nemotron push queue full — dropping oldest audio chunks", LogCategory);
            if (ov.EpisodeEnded)
                Interlocked.Exchange(ref _resyncWarned, 0);
            if (ov.RequestResync)
                Interlocked.Exchange(ref _resyncRequested, 1);
            _queue.Writer.TryWrite(new PushItem(samples, sampleRate));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Nemotron push failed: {ex.Message}", LogCategory);
        }
    }

    public SpeakerIdentificationResult? IdentifyRange(long onsetSample, long endSample, int sampleRate)
    {
        if (!IsReady || endSample <= onsetSample || sampleRate <= 0)
            return null;
        var sw = Stopwatch.StartNew();
        try
        {
            _gate.Wait();
            try
            {
                if (_stream == IntPtr.Zero)
                    return null;
                // Домен аргументов — сэмплы от первого PushLoopback: курсор
                // кумулятивен через Reset (нативный стрим пересоздаётся, а база
                // нового стрима зафиксирована в _streamBaseSamples).
                var streamRate = _pushRate > 0 ? _pushRate : sampleRate;
                var onsetSec = Math.Max(0.0, onsetSample - _streamBaseSamples) / (double)streamRate;
                var endSec = Math.Max(0.0, endSample - _streamBaseSamples) / (double)streamRate;
                if (endSec <= onsetSec)
                    return null; // диапазон целиком до начала текущего стрима
                var result = DominantSpeaker(onsetSec, endSec, onsetSample, endSample, streamRate);
                _logger?.Debug($"IdentifyRange: speaker={result?.SpeakerId ?? "-"} " +
                               $"diar_segments_ms={sw.ElapsedMilliseconds} " +
                               $"diar_frames={NemoSpeechNative.nemo_speech_diar_frame_count(_stream)} " +
                               $"pushed_s={_pushedSamples / (double)Math.Max(1, streamRate):F1}", LogCategory);
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Nemotron IdentifyRange failed: {ex.Message}", LogCategory);
            return null;
        }
    }

    // Снимок сегментов текущего стрима. Под _gate (нативный стрим однопоточный).
    internal NemoDiarSegment[] SegmentsSnapshot()
    {
        if (!IsReady)
            return Array.Empty<NemoDiarSegment>();
        try
        {
            _gate.Wait();
            try
            {
                return _stream == IntPtr.Zero
                    ? Array.Empty<NemoDiarSegment>()
                    : FetchSegmentsLocked();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Nemotron segments snapshot failed: {ex.Message}", LogCategory);
            return Array.Empty<NemoDiarSegment>();
        }
    }

    // Сэмплы, реально принятые worker'ом (кумулятивно от первого push) — офлайн-прогоны
    // (bench/тесты) ждут drain: PushedSamples == сумме переданного аудио.
    internal long PushedSamples => Interlocked.Read(ref _pushedSamples);

    // Необработанные чанки в очереди — офлайн-прогоны ждут PushLoopback-drain.
    internal int PendingPushes => _queue.Reader.Count;

    // Доминирующий спикер + внутричанковая раскладка спикеров по пересечению
    // сегментов с [onset,end). Спаны — в сэмплах входного домена вызова (база
    // стрима возвращена обратно), чтобы вызывающий резал текст/аудио чанка без
    // знания внутренней шкалы. Выполняется под _gate.
    private SpeakerIdentificationResult? DominantSpeaker(double onsetSec, double endSec,
        long onsetSample, long endSample, int streamRate)
    {
        var segments = FetchSegmentsLocked();
        var hit = SpeakerSegmentMapper.BestOverlapSpeaker(segments, onsetSec, endSec);
        if (hit == null)
            return null;

        SpeakerTurnSpan[]? spans = null;
        var subs = SpeakerSegmentMapper.SpeakerSpans(segments, onsetSec, endSec);
        if (subs is { Length: > 0 })
        {
            var list = new List<SpeakerTurnSpan>(subs.Length);
            foreach (var s in subs)
            {
                var start = onsetSample + (long)Math.Round((s.StartSec - onsetSec) * streamRate);
                var end = onsetSample + (long)Math.Round((s.EndSec - onsetSec) * streamRate);
                if (end <= start)
                    continue; // нулевой спан после округления — не отдаём
                var id = $"S{s.Speaker}";
                _seenSpeakers.Add(id);
                list.Add(new SpeakerTurnSpan(id, Math.Max(onsetSample, start), Math.Min(endSample, end)));
            }
            spans = list.Count > 0 ? list.ToArray() : null;
        }

        var speakerId = $"S{hit.Value.Speaker}";
        var isNew = _seenSpeakers.Add(speakerId);
        return new SpeakerIdentificationResult(speakerId, hit.Value.Coverage, isNew, spans);
    }

    // Все сегменты текущего стрима (two-call: count -> буфер). Под _gate.
    private NemoDiarSegment[] FetchSegmentsLocked()
    {
        var st = NemoSpeechNative.nemo_speech_diar_segments(_stream, IntPtr.Zero, null, 0, out var count);
        if (st != NemoStatus.Ok || count == 0 || count > 1_000_000) // лимит — защита от мусора
            return Array.Empty<NemoDiarSegment>();
        var buf = new NemoDiarSegment[(int)count];
        st = NemoSpeechNative.nemo_speech_diar_segments(_stream, IntPtr.Zero, buf, count, out count);
        if (st != NemoStatus.Ok)
        {
            _logger?.Warn($"Nemotron segments fetch failed: {st} {NemoSpeechNative.LastErrorString()}", LogCategory);
            return Array.Empty<NemoDiarSegment>();
        }
        var n = Math.Min((int)count, buf.Length);
        return n == buf.Length ? buf : buf[..n];
    }

    public void Reset()
    {
        if (_disposed)
            return;
        try
        {
            // Явный Reset сильнее отложенного авто-ресинка — снимаем флаг,
            // чтобы worker не пересоздавал стрим второй раз подряд.
            Interlocked.Exchange(ref _resyncRequested, 0);
            // Сначала выбрасываем накопленное, потом под gate пересоздаём стрим.
            // Чанк, уже взятый worker'ом, ещё может попасть в новый стрим —
            // окно в микросекунды, аудио-хвост безвреден.
            while (_queue.Reader.TryRead(out _)) { }

            _gate.Wait();
            try
            {
                CloseStreamLocked();
                var st = NemoSpeechNative.nemo_speech_diar_stream_open(_model, out _stream);
                if (st != NemoStatus.Ok || _stream == IntPtr.Zero)
                {
                    _stream = IntPtr.Zero;
                    _logger?.Warn($"Nemotron stream reopen failed: {st} {NemoSpeechNative.LastErrorString()}", LogCategory);
                    return;
                }
                _seenSpeakers.Clear();
                // База нового стрима — сколько сэмплов уже предложено вызывающим
                // (_offeredSamples, не _pushedSamples: при drop'ах очереди pushed
                // меньше offered, и база по pushed сдвинула бы шкалу навсегда).
                // Кумулятивные счётчики и _pushRate не сбрасываются — вызывающая
                // сторона считает индексы от первого push.
                _streamBaseSamples = Interlocked.Read(ref _offeredSamples);
                Interlocked.Increment(ref _streamEpoch);
                _logger?.Info($"Nemotron stream reset (new call), base_sample={_streamBaseSamples}", LogCategory);
                _overflow.Reset();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Nemotron reset failed: {ex.Message}", LogCategory);
        }
    }

    // Авто-ресинк после устойчивого переполнения очереди (флаг ставит PushLoopback,
    // выполняет worker). Drop-oldest уже выкинул сэмплы из хвоста — нативная ось
    // времени безвозвратно отстаёт от кумулятивной шкалы вызывающего, и IdentifyRange
    // жил бы на «мёртвом» стриме (старые/нулевые спаны). Чиним честно: чистим очередь,
    // пересоздаём стрим, базу привязываем к _offeredSamples — новые спаны снова
    // корректны. Цена — история диаризации и рестарт нумерации S<n> (эпоха++).
    // Вызывается с worker-потока между чанками; один Warn на эпизод.
    private void ResyncStream()
    {
        try
        {
            // Флаг мог быть перевыставлен, пока шёл предыдущий ресинк (очередь
            // всё ещё полна) — снимаем: повтор решит таймер полноты заново.
            Interlocked.Exchange(ref _resyncRequested, 0);
            while (_queue.Reader.TryRead(out _)) { }

            _gate.Wait();
            try
            {
                CloseStreamLocked();
                var st = NemoSpeechNative.nemo_speech_diar_stream_open(_model, out _stream);
                if (st != NemoStatus.Ok || _stream == IntPtr.Zero)
                {
                    _stream = IntPtr.Zero;
                    _logger?.Warn($"Nemotron stream reopen failed: {st} {NemoSpeechNative.LastErrorString()}", LogCategory);
                    return;
                }
                _seenSpeakers.Clear();
                _streamBaseSamples = Interlocked.Read(ref _offeredSamples);
                Interlocked.Increment(ref _streamEpoch);
                // Пейсинг повторов: следующий ресинк не раньше, чем очередь снова
                // продержится полной OverflowResyncAfter (переполнение продолжается →
                // шкала опять дрейфует → ресинк ~раз в 5 с держит её свежей,
                // но не душит поток непрерывными рестартами).
                _overflow.OnResync();
                var msg = $"Nemotron: ресинк стрима после переполнения очереди — " +
                          $"история диаризации сброшена, base_sample={_streamBaseSamples}";
                if (Interlocked.CompareExchange(ref _resyncWarned, 1, 0) == 0)
                    _logger?.Warn(msg, LogCategory); // один Warn на эпизод переполнения
                else
                    _logger?.Info(msg, LogCategory);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Nemotron resync failed: {ex.Message}", LogCategory);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Снимаем публикацию бэкенда только если она наша — более новый инстанс
        // (hot-swap настроек) мог уже записать свой EffectiveBackend.
        Interlocked.CompareExchange(ref _lastActiveBackend, null, EffectiveBackend);
        try
        {
            _queue.Writer.TryComplete();
            try
            {
                if (!_worker.Wait(DrainTimeout))
                    _logger?.Warn("Nemotron worker drain timeout on dispose", LogCategory);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Nemotron worker join failed: {ex.Message}", LogCategory);
            }

            if (!_gate.Wait(DrainTimeout))
            {
                _logger?.Warn("Nemotron dispose: stream gate timeout, leaking native handles", LogCategory);
                return;
            }
            try
            {
                CloseStreamLocked();
                NemoSpeechNative.nemo_speech_diar_destroy(_model);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Nemotron dispose failed: {ex.Message}", LogCategory);
        }
    }

    // finish+close текущего стрима. Под _gate. Ошибка finish не мешает close.
    private void CloseStreamLocked()
    {
        if (_stream == IntPtr.Zero)
            return;
        var st = NemoSpeechNative.nemo_speech_diar_stream_finish(_stream);
        if (st != NemoStatus.Ok)
            _logger?.Warn($"Nemotron stream_finish: {st} {NemoSpeechNative.LastErrorString()}", LogCategory);
        NemoSpeechNative.nemo_speech_diar_stream_close(_stream);
        _stream = IntPtr.Zero;
    }

    // Единственный потребитель канала; push сериализован _gate'ом вместе с
    // IdentifyRange/Reset/Dispose.
    private async Task WorkerLoop()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // Устойчивое переполнение: ресинк между чанками — один Warn
                // на эпизод ставится внутри ResyncStream.
                if (Interlocked.CompareExchange(ref _resyncRequested, 0, 1) == 1)
                    ResyncStream();
                try
                {
                    // Coalescing: при отставании в очереди копятся десятки
                    // 30-мс фреймов — склеиваем их в один push (нативная цена
                    // вызова не зависит от длины), иначе бэкенд тратит больше
                    // времени на диспетчеризацию, чем на саму диаризацию.
                    var batch = CoalesceFromQueue(item);
                    _gate.Wait();
                    try
                    {
                        if (_stream == IntPtr.Zero)
                            continue;
                        // Rate фиксируется первым push'ем объекта (нативный контракт:
                        // rate не может меняться внутри стрима; кумулятивный курсор
                        // считается в сэмплах этого rate).
                        if (_pushRate == 0)
                            _pushRate = batch.SampleRate;
                        else if (batch.SampleRate != _pushRate)
                        {
                            _logger?.Warn($"Nemotron: sampleRate changed {batch.SampleRate} vs {_pushRate}, chunk dropped", LogCategory);
                            continue;
                        }
                        PushLocked(batch);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Nemotron push failed: {ex.Message}", LogCategory);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Nemotron worker exited: {ex.Message}", LogCategory);
        }
    }

    // Дотаскивает накопленные фреймы того же rate в один массив (≤ CoalesceSamples).
    // Первый элемент уже прочитан — досклеиваем к нему без лишней аллокации.
    private PushItem CoalesceFromQueue(in PushItem first)
    {
        var extra = 0;
        var peeked = 0;
        var tail = new PushItem[64];
        // single-reader канал: TryPeek+TryRead атомарны относительно writer'ов
        while (peeked < tail.Length
               && first.Samples.Length + extra < CoalesceSamples
               && _queue.Reader.TryPeek(out var nxt)
               && nxt.SampleRate == first.SampleRate
               && _queue.Reader.TryRead(out nxt))
        {
            tail[peeked++] = nxt;
            extra += nxt.Samples.Length;
        }
        if (extra == 0)
            return first;

        var merged = new float[first.Samples.Length + extra];
        first.Samples.CopyTo(merged, 0);
        var off = first.Samples.Length;
        for (var i = 0; i < peeked; i++)
        {
            tail[i].Samples.CopyTo(merged, off);
            off += tail[i].Samples.Length;
        }
        return new PushItem(merged, first.SampleRate);
    }

    private void PushLocked(in PushItem item)
    {
        var sw = Stopwatch.StartNew();
        var st = NemoSpeechNative.nemo_speech_diar_stream_push_f32(
            _stream, item.Samples, (nuint)item.Samples.Length, _pushRate);
        if (st == NemoStatus.Ok)
        {
            _pushedSamples += item.Samples.Length;
            _logger?.Debug($"diar_push_ms={sw.ElapsedMilliseconds} n={item.Samples.Length} pushed_s={_pushedSamples / (double)_pushRate:F1}", LogCategory);
        }
        else
        {
            _logger?.Warn($"Nemotron stream_push_f32: {st} {NemoSpeechNative.LastErrorString()}", LogCategory);
        }
    }

    private static string? ResolveModelPath(SpeakerDiarizationConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.NemotronModelPath))
            return File.Exists(cfg.NemotronModelPath) ? cfg.NemotronModelPath : null;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BrainstormBuddy", "models", DefaultModelFile);
        if (File.Exists(appData))
            return appData;

        var appDir = Path.Combine(AppContext.BaseDirectory, "models", DefaultModelFile);
        if (File.Exists(appDir))
            return appDir;

        // dev: artifacts\speaker у корня репо (вверх от bin\…\).
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var dev = Path.Combine(dir.FullName, "artifacts", "speaker", DefaultModelFile);
            if (File.Exists(dev))
                return dev;
        }
        return null;
    }
}

    /// <summary>
    /// Эпизод «очередь переполнена» с гистерезисом. Эпизод открывается при
    /// Count &gt;= capacity и закрывается только когда очередь реально опустела
    /// (&lt;= recoverLevel): на медленном бэкенде Count колеблется 512↔511, и
    /// сброс по любому провалу ниже capacity навсегда глушил бы авто-ресинк
    /// (прод-баг 2.27.1: «queue full» часами, диаризация мёртвая, всегда S1).
    /// Потокобезопасен (lock внутри — Track вызывается из одного продюсера,
    /// но IdentifyRange/Reset могут дёрнуть OnResync/Reset с другого потока).
    /// </summary>
    internal sealed class OverflowTracker
    {
        internal readonly record struct Flags(bool DropWarn, bool RequestResync, bool EpisodeEnded);

        private readonly int _capacity;
        private readonly int _recoverLevel;
        private readonly long _resyncAfterMs;
        private readonly object _gate = new();
        private long _fullSinceMs = -1; // тик открытия эпизода (-1 = не открыт; 0 — валидный тик)
        private bool _dropWarned;

        internal OverflowTracker(int capacity, int recoverLevel, long resyncAfterMs)
        {
            _capacity = capacity;
            _recoverLevel = recoverLevel;
            _resyncAfterMs = resyncAfterMs;
        }

        /// <summary>Текущий размер очереди + монотонное время (TickCount64).</summary>
        internal Flags Track(int count, long nowMs)
        {
            lock (_gate)
            {
                if (count >= _capacity)
                {
                    if (_fullSinceMs < 0)
                        _fullSinceMs = nowMs;
                    var dropWarn = !_dropWarned;
                    _dropWarned = true;
                    var resync = nowMs - _fullSinceMs >= _resyncAfterMs;
                    return new Flags(dropWarn, resync, false);
                }
                if (count <= _recoverLevel && _fullSinceMs >= 0)
                {
                    _fullSinceMs = -1;
                    _dropWarned = false;
                    return new Flags(false, false, true);
                }
                // Между recoverLevel и capacity эпизод остаётся открытым:
                // состояние не меняется, флагов нет.
                return new Flags(false, false, false);
            }
        }

        /// <summary>Пейсинг после выполненного ресинка: эпизод держится открытым,
        /// но окно отсчитывается заново — следующий ресинк не раньше resyncAfterMs.</summary>
        internal void OnResync()
        {
            lock (_gate)
                _fullSinceMs = Environment.TickCount64;
        }

        /// <summary>Полный сброс (новый звонок): эпизод закрыт, варны переиздаются.</summary>
        internal void Reset()
        {
            lock (_gate)
            {
                _fullSinceMs = -1;
                _dropWarned = false;
            }
        }
    }
