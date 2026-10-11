using System.Diagnostics;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Фоновый наблюдатель экрана: снимает активный монитор, распознаёт текст и поднимает
/// событие <see cref="CandidateDetected"/>, когда на экране устойчиво появился новый вопрос.
/// </summary>
/// <remarks>
/// <para>
/// Конвейер одного опроса: захват → OCR → чистка текста → нормализация → гейт стабильности
/// (текст не менялся <see cref="StabilityPolls"/> опросов подряд) → гейт смены (текст заметно
/// отличается от КАЖДОГО из недавно отправленных — история последних <see cref="SentHistoryCapacity"/>)
/// → эвристика вопроса → событие.
/// </para>
/// <para>
/// Пока идёт OCR, следующий тик пропускается: Tesseract на большом кадре легко занимает
/// больше интервала опроса, а параллельные проходы только жгут CPU и память.
/// </para>
/// <para>
/// Все исключения тика — в журнал через <c>warn</c>, цикл продолжается: сбой одного захвата
/// (смена разрешения, заблокированный рабочий стол) не должен убивать наблюдателя до выхода
/// из приложения.
/// </para>
/// <para>
/// <b>Приватность.</b> Кадры и распознанный текст никуда не пишутся: наружу уходит только
/// текст-кандидат и метрики в журнал (длины, тайминги).
/// </para>
/// </remarks>
public sealed class ScreenTextWatcher : IDisposable
{
    /// <summary>Сколько опросов подряд текст должен быть стабилен, прежде чем он «замер».</summary>
    private const int StabilityPolls = 2;

    /// <summary>
    /// Глубина истории отправленных вопросов для дедупа. Сравнение только с последним
    /// отправленным пропускало скролл-назад: ушёл с вопроса на другой и вернулся — тот же
    /// текст уходил в LLM второй раз. История достаточно короткая, чтобы вытеснение не
    /// «забывало» вопросы, просмотренные недавно.
    /// </summary>
    private const int SentHistoryCapacity = 16;

    private readonly IScreenCapturer _capturer;
    private readonly ITextRecognizer _recognizer;
    private readonly ScreenOcrConfig _config;
    private readonly Action<string>? _log;
    private readonly Action<string>? _warn;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private string _previousNormalized = string.Empty;
    private readonly List<string> _sentHistory = new(SentHistoryCapacity);
    private int _stableCount;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private bool _availabilityWarned;

    public ScreenTextWatcher(
        IScreenCapturer capturer,
        ITextRecognizer recognizer,
        ScreenOcrConfig config,
        Action<string>? log = null,
        Action<string>? warn = null)
    {
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log;
        _warn = warn;
    }

    /// <summary>Распознан новый вопрос (сырой, до нормализации). Подписчик отвечает за ответ.
    /// Второй аргумент — длительность OCR-прохода в мс (захват+препроц+движок), для мета-строки.</summary>
    public event Func<string, double, Task>? CandidateDetected;

    /// <summary>
    /// Кандидат устойчив, но <see cref="ScreenOcrConfig.AutoAnswer"/> выключен и в LLM он
    /// не уходит — текст показывают пользователю (оверлей/журнал) через это событие,
    /// чтобы включённое наблюдение не выглядело мёртвым.
    /// Второй аргумент — длительность OCR-прохода в мс.
    /// </summary>
    public event Action<string, double>? PreviewRequested;

    /// <summary>Запущен ли цикл наблюдения.</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Запустить цикл. Повторный вызов при живом цикле — no-op.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _stableCount = 0;
            _previousNormalized = string.Empty;
            _cooldownUntil = DateTimeOffset.MinValue;
            _availabilityWarned = false;
            _loop = Task.Run(() => RunAsync(_cts.Token));
            _log?.Invoke($"Наблюдение за экраном запущено: интервал {_config.PollIntervalMs} мс, монитор {_config.MonitorIndex}");
        }
    }

    /// <summary>Остановить цикл и дождаться его завершения (не дольше 1.5 с).</summary>
    public void Stop()
    {
        Task? loop;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            loop = _loop;
            cts = _cts;
            _loop = null;
            _cts = null;
        }

        try
        {
            cts?.Cancel();
            loop?.Wait(TimeSpan.FromSeconds(1.5));
        }
        catch (AggregateException)
        {
            // Отмена — ожидаемый путь выхода, шум в журнал не нужен.
        }
        finally
        {
            cts?.Dispose();
            _log?.Invoke("Наблюдение за экраном остановлено");
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Чистка распознанного текста: краевые пробелы, пустые строки, строки короче двух
    /// символов (мусор из рамок и иконок) и повторяющиеся пустые строки — в один перевод.
    /// </summary>
    public static string CleanupText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 2)
            {
                continue;
            }

            kept.Add(trimmed);
        }

        return string.Join("\n", kept).Trim();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = Math.Clamp(_config.PollIntervalMs, 250, 60_000);

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _warn?.Invoke($"Сбой опроса экрана: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var availability = _recognizer.Availability;

        if (availability == RecognizerAvailability.Available)
        {
            _availabilityWarned = false;
        }
        else
        {
            // Движок не поднялся (нет моделей, нет нативки) — наблюдатель молча ждёт,
            // но причину в журнал пишет один раз, а не каждый тик.
            if (!_availabilityWarned && availability == RecognizerAvailability.Unavailable)
            {
                _availabilityWarned = true;
                _warn?.Invoke($"OCR недоступен, наблюдение простаивает: {_recognizer.UnavailabilityReason ?? "причина не сообщена"}");
            }

            return;
        }

        var tickStopwatch = Stopwatch.StartNew();

        var region = MonitorEnumerator.CaptureBoundsFor(_config.MonitorIndex);
        var frame = _capturer.CaptureRegion(region);
        var captureMs = tickStopwatch.Elapsed.TotalMilliseconds;

        // Кадры шире MaxOcrWidth ужимаются ДО OCR: полный экран 4K распознаётся 5–7 с,
        // после /2 — ~1–2 с при читаемом тексте (замер бэклога 2.8.x п.7).
        var pixels = ImagePreprocessor.LimitWidth(frame.Pixels, _config.MaxOcrWidth);

        var options = RecognitionOptions.Default with
        {
            Languages = RecognitionLanguages.RussianAndEnglish,
        };

        var result = await _recognizer.RecognizeAsync(pixels, options, cancellationToken).ConfigureAwait(false);
        var totalMs = tickStopwatch.Elapsed.TotalMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        // Тайминги этапов одного прохода — по ним видно, кто съедает бюджет: захват,
        // препроцессинг (грей+апскейл+инверсия) или сам движок (result.Elapsed).
        var preprocessMs = totalMs - captureMs - result.Elapsed.TotalMilliseconds;
        _log?.Invoke(
            $"OCR tick: {region.Width}×{region.Height} → {pixels.Width}×{pixels.Height}, " +
            $"захват {captureMs:F0} мс, препроц {preprocessMs:F0} мс, " +
            $"движок {result.Elapsed.TotalMilliseconds:F0} мс, итого {totalMs:F0} мс, " +
            $"{result.WordCount} слов ({result.EngineName})");
        var text = CleanupText(result.RawText);
        if (text.Length < Math.Max(0, _config.MinTextLength))
        {
            ResetStability();
            return;
        }

        var normalized = ScreenTextDiffer.Normalize(text);
        if (normalized.Length == 0)
        {
            ResetStability();
            return;
        }

        // Гейт стабильности: OCR одного и того же кадра слегка дрожит между опросами.
        // Текст считается «замершим» после StabilityPolls совпадающих опросов подряд.
        if (ScreenTextDiffer.IsStable(_previousNormalized, normalized))
        {
            _stableCount++;
        }
        else
        {
            _stableCount = 0;
        }

        _previousNormalized = normalized;

        if (_stableCount < StabilityPolls)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Кулдаун: пока он идёт, событие не поднимается — иначе тот же вопрос уходил бы
        // в LLM каждые 2 секунды. Базой сравнения смены служит вся история _sentHistory.
        if (now < _cooldownUntil)
        {
            return;
        }

        // Дедуп по истории: отправляем, только если текст не похож НИ НА ОДИН из недавно
        // отправленных. Возврат к ранее увиденному вопросу (скролл назад по документу)
        // повторного события не даёт и не жжёт токены LLM.
        if (!ScreenTextDiffer.IsSignificantChange(_sentHistory, normalized, _config.ChangeThreshold))
        {
            return;
        }

        if (!ScreenTextDiffer.LooksLikeQuestion(text, _config.MinTextLength))
        {
            return;
        }

        if (_sentHistory.Count >= SentHistoryCapacity)
        {
            _sentHistory.RemoveAt(0);
        }

        _sentHistory.Add(normalized);
        _cooldownUntil = now + TimeSpan.FromSeconds(Math.Max(0.0, _config.CooldownSec));
        _stableCount = 0;

        var handler = CandidateDetected;
        if (handler is not null)
        {
            _log?.Invoke($"Найден вопрос на экране ({text.Length} симв.), отправка в LLM");
            await handler(text, totalMs).ConfigureAwait(false);
        }
        else
        {
            // AutoAnswer выключен (подписчик не ставил CandidateDetected): текст всё равно
            // прошёл все гейты — отдаём его наружу как превью, чтобы юзер видел результат.
            _log?.Invoke($"Найден вопрос на экране ({text.Length} симв.), автоответ выкл — показываем превью");
            PreviewRequested?.Invoke(text, totalMs);
        }
    }

    private void ResetStability()
    {
        _stableCount = 0;
        _previousNormalized = string.Empty;
    }
}
