namespace BrainstormBuddy.Audio;

/// <summary>Режим нарезки речи (эндпойнтинга).</summary>
public enum EndpointMode
{
    /// <summary>Ручной: порог тишины задаёт юзер (пресеты/слайдер). Дефолт, старое поведение.</summary>
    Manual = 0,
    /// <summary>Авто по распределению пауз: порог сам подстраивается под темп говорящего.</summary>
    AdaptivePauses = 1,
    /// <summary>Авто + текстовая склейка незаконченных мыслей (семантика на уровне текста).</summary>
    Semantic = 2,
}

/// <summary>Параметры адаптивного эндпойнтинга. Все с дефолтами (обратная совместимость config.json).</summary>
public sealed class AdaptiveEndpointConfig
{
    // Дефолты подобраны эмпирически на 3 реальных видео (см. ENDPOINTING_PLAN §5): сходятся к
    // 0.7–0.8с — в «золотую зону» (мин. слипание при низкой микро-фрагментации). Выборка
    // независимая (сырая VAD-маска), поэтому петли обратной связи нет и низкий Multiplier безопасен.
    public double Percentile      { get; set; } = 0.80;
    public int    Window          { get; set; } = 48;
    public int    MinSamples      { get; set; } = 8;
    public int    MinGapMs        { get; set; } = 150;   // пол осознанной паузы (отсечь дрожь VAD)
    public int    MaxGapMs        { get; set; } = 4000;  // потолок: межходовые тишины не искажают p85
    public double Multiplier      { get; set; } = 1.15;  // headroom над перцентилем
    public int    MarginMs        { get; set; } = 200;   // абсолютный запас для быстрых ораторов
    public double MinSeconds      { get; set; } = 0.6;
    public double MaxSeconds      { get; set; } = 2.2;
    public double EmaAlpha        { get; set; } = 0.25;
    public int    DwellMs         { get; set; } = 3000;
    public int    DeadbandMs      { get; set; } = 150;
    public double StepCapSeconds  { get; set; } = 0.3;
    public double ColdStartSeconds{ get; set; } = 1.2;
    public bool   UseSttRate      { get; set; } = true;
    public double BaselineCps     { get; set; } = 12.0;  // симв/с обычной русской речи
    public int    FrameMs         { get; set; } = 30;
}

/// <summary>
/// Оценивает темп говорящего по распределению его пауз и ведёт порог эндпойнтинга (SilenceSeconds).
/// Чистый, юнит-тестируемый, потокобезопасный (внутренний lock — трогается из mixer-потока
/// [RecordGap/TryConsumeChange под AudioBuffer._lock], STT-worker [NoteTranscript без него] и UI).
///
/// Анти-коллапс: паузы подаются из НЕЗАВИСИМОЙ сырой VAD-маски (не из эмитов буфера), поэтому
/// выборка не цензурируется порогом — петля обратной связи разомкнута и не сходит в пол.
/// Потолок MaxGapMs отсекает межходовые тишины (иначе p85 раздувается).
/// </summary>
public sealed class PauseAdaptiveController
{
    private readonly AdaptiveEndpointConfig _cfg;
    private readonly TimeProvider _time;
    private readonly object _lock = new();

    private readonly int[] _ring;   // последние Window пауз, мс
    private int _count;
    private int _head;

    private double _emaTargetMs;
    private double _appliedSec;
    private DateTimeOffset _lastApplyUtc;
    private double _corridorBiasSec;
    private double _rateEmaCps;
    private double _lastP85Ms;
    private bool _changed;

    public PauseAdaptiveController(AdaptiveEndpointConfig cfg, double coldStartSeconds, TimeProvider? time = null)
    {
        _cfg = cfg;
        _time = time ?? TimeProvider.System;
        _ring = new int[Math.Max(1, cfg.Window)];
        _appliedSec = Clamp(coldStartSeconds > 0 ? coldStartSeconds : cfg.ColdStartSeconds);
        _emaTargetMs = _appliedSec * 1000;
        _lastApplyUtc = _time.GetUtcNow();
    }

    /// <summary>Минимальная длина паузы в кадрах (для фильтра дрожи на стороне AudioBuffer).</summary>
    public int MinGapFrames => Math.Max(1, _cfg.MinGapMs / _cfg.FrameMs);
    public int MaxGapFrames => Math.Max(1, _cfg.MaxGapMs / _cfg.FrameMs);

    public double AppliedSeconds { get { lock (_lock) return _appliedSec; } }
    public int SampleCount       { get { lock (_lock) return _count; } }
    public double LastP85Ms      { get { lock (_lock) return _lastP85Ms; } }
    public bool IsWarm           { get { lock (_lock) return _count >= _cfg.MinSamples; } }

    /// <summary>Тёплый старт: подставить выученное в прошлой сессии значение.</summary>
    public void SeedWarmStart(double seconds)
    {
        if (seconds <= 0) return;
        lock (_lock) { _appliedSec = Clamp(seconds); _emaTargetMs = _appliedSec * 1000; }
    }

    /// <summary>Записать наблюдённую внутриречевую паузу (в кадрах). Вне [MinGap,MaxGap] — игнор.</summary>
    public void RecordGap(int gapFrames)
    {
        int ms = gapFrames * _cfg.FrameMs;
        if (ms < _cfg.MinGapMs || ms > _cfg.MaxGapMs) return;
        lock (_lock)
        {
            _ring[_head] = ms;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            Recompute();
        }
    }

    /// <summary>Вторичный сигнал темпа из STT (симв/с завершённого чанка) — мягкий сдвиг коридора.</summary>
    public void NoteTranscript(int chars, double audioSeconds)
    {
        if (!_cfg.UseSttRate || audioSeconds < 0.5) return;
        double rate = chars / audioSeconds;
        lock (_lock)
        {
            _rateEmaCps = _rateEmaCps <= 0 ? rate : 0.3 * rate + 0.7 * _rateEmaCps;
            _corridorBiasSec = Math.Clamp((_cfg.BaselineCps - _rateEmaCps) / _cfg.BaselineCps * 0.4, -0.25, 0.25);
        }
    }

    /// <summary>true, если применённый порог сменился с прошлого вызова (пора переписать в буфер).</summary>
    public bool TryConsumeChange(out double appliedSeconds)
    {
        lock (_lock)
        {
            appliedSeconds = _appliedSec;
            if (_changed) { _changed = false; return true; }
            return false;
        }
    }

    private void Recompute()
    {
        if (_count < _cfg.MinSamples) return;
        double p = PercentileLocked(_cfg.Percentile);
        _lastP85Ms = p;
        double rawTargetMs = Math.Max(p * _cfg.Multiplier, p + _cfg.MarginMs);
        _emaTargetMs = _cfg.EmaAlpha * rawTargetMs + (1 - _cfg.EmaAlpha) * _emaTargetMs;
        MaybeApplyLocked();
    }

    private void MaybeApplyLocked()
    {
        double targetSec = Clamp(_emaTargetMs / 1000.0 + _corridorBiasSec);
        if (Math.Abs(targetSec - _appliedSec) * 1000 < _cfg.DeadbandMs) return;              // deadband
        if ((_time.GetUtcNow() - _lastApplyUtc).TotalMilliseconds < _cfg.DwellMs) return;    // dwell
        double step = Math.Clamp(targetSec - _appliedSec, -_cfg.StepCapSeconds, _cfg.StepCapSeconds);
        _appliedSec = Clamp(_appliedSec + step);                                             // step + clamp
        _lastApplyUtc = _time.GetUtcNow();
        _changed = true;
    }

    private double PercentileLocked(double q)
    {
        int n = _count;
        Span<int> tmp = n <= 64 ? stackalloc int[n] : new int[n];
        for (int i = 0; i < n; i++) tmp[i] = _ring[i];
        tmp.Sort();
        int rank = Math.Clamp((int)Math.Ceiling(q * n) - 1, 0, n - 1);
        return tmp[rank];
    }

    private double Clamp(double s) => Math.Clamp(s, _cfg.MinSeconds, _cfg.MaxSeconds);
}
