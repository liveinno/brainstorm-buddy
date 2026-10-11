using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Embeddings;

/// <summary>Режим удержания модели в RAM (спека memory2 §10-§13).</summary>
public enum LocalEmbeddingRuntimeMode
{
    /// <summary>Загрузить при первом использовании и держать до закрытия приложения.</summary>
    AlwaysLoaded,
    /// <summary>Дефолт: lazy-load + выгрузка после idle-таймаута.</summary>
    Balanced,
    /// <summary>Выгружать сразу после завершения текущей операции (батч = одна операция).</summary>
    MemorySaver,
}

/// <summary>Состояние lazy-loader'а (спека memory2 §16).</summary>
public enum LazyModelState
{
    Unloaded,
    Loading,
    Loaded,
    Unloading,
    Failed,
}

/// <summary>Снапшот состояния для UI/диагностики (спека memory2 §19, §42).</summary>
public readonly record struct LazyModelStatus(
    LazyModelState State,
    int ActiveLeases,
    int FailedLoads,
    DateTime? LoadedAtUtc,
    DateTime? LastUsedUtc,
    DateTime? NextUnloadUtc)
{
    public bool IsLoaded => State == LazyModelState.Loaded && LoadedAtUtc != null;
}

/// <summary>
/// Потокобезопасный lazy-loader тяжёлого ресурса (ONNX InferenceSession +
/// tokenizer) с single-flight загрузкой, ref-counted lease'ами и idle-выгрузкой
/// (спека memory2 §10-§18):
///  - конкурирующие Acquire во время Loading ждут одну и ту же загрузку;
///  - unload допустим только при ActiveLeases==0 и истёкшем idle (нет race с
///    inference — таймер и Release сериализованы через _gate);
///  - MemorySaver выгружает после завершения текущей операции; батч держит один
///    lease на все элементы, иначе «задание» распалось бы на per-item reload;
///  - время через TimeProvider → idle-тесты на фейковых часах (§38).
/// </summary>
public sealed class LazyModelLoader<T> : IDisposable where T : class, IDisposable
{
    private const string LogCat = "Memory";

    private readonly Func<CancellationToken, Task<T>> _factory;
    private readonly string _name;
    private readonly LoggingService? _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);


    private T? _model;
    private LazyModelState _state = LazyModelState.Unloaded;
    private int _activeLeases;
    private int _failedLoads;
    private DateTime? _loadedAt;
    private DateTime _lastUsed;
    private ITimer? _idleTimer;
    private TimeSpan _idleAfter;
    private LocalEmbeddingRuntimeMode _mode = LocalEmbeddingRuntimeMode.Balanced;
    private volatile bool _disposed;

    public LazyModelLoader(Func<CancellationToken, Task<T>> factory, string name,
        LocalEmbeddingRuntimeMode mode, TimeSpan idleAfter,
        LoggingService? log = null, TimeProvider? clock = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _name = name;
        _mode = mode;
        _idleAfter = idleAfter;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public LazyModelState State => _state;
    public int FailedLoads => _failedLoads;

    /// <summary>Модель загружена и можно брать lease.</summary>
    public bool IsLoaded => _state == LazyModelState.Loaded;

    /// <summary>
    /// Смена режима/таймаута на лету, без рестарта (спека §39): пересчитывает
    /// текущий idle-deadline; AlwaysLoaded отменяет запланированную выгрузку,
    /// MemorySaver при бездействии выгружает сразу.
    /// </summary>
    public void ApplySettings(LocalEmbeddingRuntimeMode mode, TimeSpan idleAfter)
    {
        _gate.Wait();
        try
        {
            _mode = mode;
            _idleAfter = idleAfter;
            RescheduleIdleLocked();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Lease на модель: загружает при необходимости (single-flight).</summary>
    public async Task<ModelLease> AcquireAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_model == null)
            {
                _state = LazyModelState.Loading;
                var sw = _clock.GetTimestamp();
                try
                {
                    _model = await _factory(ct).ConfigureAwait(false);
                }
                catch
                {
                    _model = null;
                    _state = LazyModelState.Failed;
                    _failedLoads++;
                    throw;
                }
                _loadedAt = _clock.GetUtcNow().UtcDateTime;
                _state = LazyModelState.Loaded;
                _log?.Info($"{_name}: model loaded in " +
                    $"{_clock.GetElapsedTime(sw).TotalMilliseconds:F0} ms", LogCat);
            }
            // Использование отменяет любой запланированный idle-unload.
            CancelIdleTimerLocked();
            _activeLeases++;
            _lastUsed = _clock.GetUtcNow().UtcDateTime;
            return new ModelLease(this, _model);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Текущая модель без lease — только под уже взятым lease корректна.</summary>
    internal void Release()
    {
        _gate.Wait();
        try
        {
            _activeLeases = Math.Max(0, _activeLeases - 1);
            _lastUsed = _clock.GetUtcNow().UtcDateTime;
            RescheduleIdleLocked();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Планирование выгрузки по режиму. Вызывается под _gate.</summary>
    private void RescheduleIdleLocked()
    {
        CancelIdleTimerLocked();
        if (_model == null || _activeLeases > 0) return;
        switch (_mode)
        {
            case LocalEmbeddingRuntimeMode.AlwaysLoaded:
                return;   // держим до Dispose
            case LocalEmbeddingRuntimeMode.MemorySaver:
                // «После завершения текущего задания» = все lease отпущены → unload.
                _ = UnloadCoreAsync();
                return;
            default: // Balanced
                var remaining = _idleAfter - (_clock.GetUtcNow().UtcDateTime - _lastUsed);
                if (remaining <= TimeSpan.Zero)
                {
                    _ = UnloadCoreAsync();
                }
                else
                {
                    _idleTimer = _clock.CreateTimer(static s =>
                        ((LazyModelLoader<T>)s!).OnIdleTimer(), this,
                        remaining, Timeout.InfiniteTimeSpan);
                }
                return;
        }
    }

    private void OnIdleTimer() => _ = UnloadCoreAsync();

    private void CancelIdleTimerLocked()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    /// <summary>
    /// Ручная выгрузка («Выгрузить модель сейчас»). false — модель не загружена
    /// или есть активный inference (кнопка должна быть disabled, но гонку чиним
    /// здесь же).
    /// </summary>
    public async Task<bool> TryUnloadAsync() => await UnloadCoreAsync().ConfigureAwait(false);

    private async Task<bool> UnloadCoreAsync()
    {
        T? victim = null;
        try { await _gate.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return false; }   // loader уже Dispose
        try
        {
            if (_model == null || _activeLeases > 0) return false;
            _state = LazyModelState.Unloading;
            victim = _model;
            _model = null;
            _loadedAt = null;
            CancelIdleTimerLocked();
            // Состояние фиксируем под gate: следующий Acquire сразу увидит
            // Unloaded и сделает re-load — не дожидаясь конца Dispose victim'а.
            _state = LazyModelState.Unloaded;
        }
        finally { _gate.Release(); }

        // Dispose вне gate: длительная нативная выгрузка не блокирует новых
        // Acquire (они просто загрузят модель заново — штатный re-load после unload).
        try { victim.Dispose(); }
        catch (Exception ex) { _log?.Warn($"{_name}: model dispose failed: {ex.Message}", LogCat); }
        _log?.Info($"{_name}: model unloaded", LogCat);
        return true;
    }

    /// <summary>Снапшот для статус-панели/диагностики.</summary>
    public LazyModelStatus GetStatus()
    {
        _gate.Wait();
        try
        {
            DateTime? nextUnload = null;
            if (_state == LazyModelState.Loaded && _activeLeases == 0 &&
                _mode == LocalEmbeddingRuntimeMode.Balanced)
            {
                nextUnload = _lastUsed + _idleAfter;
            }
            return new LazyModelStatus(_state, _activeLeases, _failedLoads,
                _loadedAt, _model != null ? _lastUsed : null, nextUnload);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait();
        try
        {
            CancelIdleTimerLocked();
            _model?.Dispose();
            _model = null;
            _state = LazyModelState.Unloaded;
        }
        finally { _gate.Release(); }
        _gate.Dispose();
    }

    /// <summary>Lease: держит модель живой на время inference/батча.</summary>
    public sealed class ModelLease : IDisposable
    {
        private LazyModelLoader<T>? _owner;
        public T Model { get; }

        internal ModelLease(LazyModelLoader<T> owner, T model)
        {
            _owner = owner;
            Model = model;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
