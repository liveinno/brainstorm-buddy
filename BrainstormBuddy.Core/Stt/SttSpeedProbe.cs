using System.Diagnostics;
using System.Globalization;
using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Результат одного замера скорости STT на эталонном образце: реальное время
/// вызова (Stopwatch), RTF (аудио-секунды / секунды обработки) и статус ответа.
/// </summary>
public sealed record SttSpeedMeasurement(
    bool Success,
    SttTranscribeStatus Status,
    double ElapsedMs,
    double Rtf,          // NaN, если замер НЕУСПЕШЕН (успешного RTF не существует)
    string? Text,
    string? Error,
    int? HttpStatus,
    double AudioSeconds,
    string EngineName)
{
    /// <summary>true только при успешном замере: у неуспеха RTF не существует (NaN).</summary>
    public bool HasRtf => Success && !double.IsNaN(Rtf);
}

/// <summary>
/// Честный замер скорости внешнего STT: фаза 1 «прогрев» (время НЕ измеряется —
/// сервер должен просто ответить), затем фаза 2 — один вызов под Stopwatch,
/// чьё время и даёт RTF. Никаких констант и подмены скорости; любой неуспех
/// возвращает Rtf=NaN — успешного RTF у неуспешного замера не существует.
/// </summary>
public static class SttSpeedProbe
{
    private static readonly TimeSpan DefaultWarmupTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultMeasureTimeout = TimeSpan.FromSeconds(60);

    public static async Task<SttSpeedMeasurement> MeasureAsync(
        ISttEngine engine, byte[] wav, double audioSeconds,
        CancellationToken ct = default,
        TimeSpan? warmupTimeout = null, TimeSpan? measureTimeout = null)
    {
        if (engine == null)
            return Fail(SttTranscribeStatus.Failed, elapsedMs: 0, audioSeconds,
                engineName: "", error: "движок STT не выбран", httpStatus: null);

        // Фаза 1 «прогрев»: время прогона не измеряется и не попадает в RTF.
        var warmup = await RunEngineCallAsync(engine, wav, ct, warmupTimeout ?? DefaultWarmupTimeout);
        if (warmup.TimedOut)
            return Fail(SttTranscribeStatus.Failed, 0, audioSeconds, engine.Name, warmup.Error, null);
        if (warmup.Canceled)
            return Fail(SttTranscribeStatus.Canceled, 0, audioSeconds, engine.Name, "отменено", null);
        if (warmup.Error != null) // неожиданное исключение движка
            return Fail(SttTranscribeStatus.Failed, 0, audioSeconds, engine.Name, warmup.Error, null);

        var warmupResult = warmup.Result!;
        if (warmupResult.Status == SttTranscribeStatus.Failed)
        {
            // Прогрев не удался — замер останавливается, измерительный вызов не выполняется.
            return Fail(SttTranscribeStatus.Failed, 0, audioSeconds, engine.Name,
                "прогрев не удался: " + (string.IsNullOrWhiteSpace(warmupResult.Error) ? "ошибка STT" : warmupResult.Error),
                warmupResult.HttpStatus);
        }
        if (warmupResult.Status == SttTranscribeStatus.Canceled)
            return Fail(SttTranscribeStatus.Canceled, 0, audioSeconds, engine.Name, "прогрев отменён", null);

        // NoSpeech/Text: сервер жив — фаза 2 «измерение», один вызов под Stopwatch.
        var measure = await RunEngineCallAsync(engine, wav, ct, measureTimeout ?? DefaultMeasureTimeout);
        if (measure.TimedOut)
            return Fail(SttTranscribeStatus.Failed, measure.ElapsedMs, audioSeconds, engine.Name, measure.Error, null);
        if (measure.Canceled)
            return Fail(SttTranscribeStatus.Canceled, measure.ElapsedMs, audioSeconds, engine.Name, "отменено", null);
        if (measure.Error != null)
            return Fail(SttTranscribeStatus.Failed, measure.ElapsedMs, audioSeconds, engine.Name, measure.Error, null);

        var result = measure.Result!;
        switch (result.Status)
        {
            case SttTranscribeStatus.Text:
            {
                var elapsedMs = measure.ElapsedMs;
                return new SttSpeedMeasurement(
                    true, result.Status, elapsedMs,
                    audioSeconds > 0 ? elapsedMs / 1000.0 / audioSeconds : 0,
                    result.Text, null, result.HttpStatus, audioSeconds, engine.Name);
            }
            case SttTranscribeStatus.NoSpeech:
                return Fail(SttTranscribeStatus.NoSpeech, measure.ElapsedMs, audioSeconds,
                    engine.Name, "речь не распознана (пустой ответ)", null);
            case SttTranscribeStatus.Failed:
                return Fail(SttTranscribeStatus.Failed, measure.ElapsedMs, audioSeconds,
                    engine.Name,
                    string.IsNullOrWhiteSpace(result.Error) ? "ошибка STT" : result.Error,
                    result.HttpStatus);
            default: // Canceled
                return Fail(SttTranscribeStatus.Canceled, measure.ElapsedMs, audioSeconds,
                    engine.Name, "отменено", null);
        }
    }

    /// <summary>Один вызов движка внутри фазы: Stopwatch вокруг вызова, связанный
    /// CTS добавляет таймаут фазы к внешнему токену. Таймаут фазы и внешняя отмена
    /// различаются: таймаут → Failed «превышено время ожидания (N с)», внешняя
    /// отмена → Canceled «отменено».</summary>
    private static async Task<EngineCallOutcome> RunEngineCallAsync(
        ISttEngine engine, byte[] wav, CancellationToken externalCt, TimeSpan phaseTimeout)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        timeoutCts.CancelAfter(phaseTimeout);
        try
        {
            SttTranscribeResult result;
            if (engine is IDetailedSttEngine detailed)
            {
                result = await detailed.TranscribeDetailedAsync(wav, timeoutCts.Token);
            }
            else
            {
                var text = await engine.TranscribeAsync(wav, timeoutCts.Token);
                result = string.IsNullOrWhiteSpace(text)
                    ? SttTranscribeResult.NoSpeechResult()
                    : SttTranscribeResult.Recognized(text);
            }

            stopwatch.Stop();
            // Движок, получив отменённый токен фазы, может вернуть Canceled-исход
            // вместо исключения (так делает OpenAiClient). Если сгорел внутренний
            // таймаут фазы, а внешний токен жив — это таймаут ожидания, а не отмена
            // вызывающим: различаем здесь, иначе замер врал бы про «отменено».
            if (!externalCt.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                return new EngineCallOutcome(null, false, true, stopwatch.Elapsed.TotalMilliseconds,
                    TimeoutError(phaseTimeout));
            // Внешняя отмена могла случиться, пока запрос уже отвечал: движок мог
            // проигнорировать токен — тогда замер всё равно считается отменённым.
            externalCt.ThrowIfCancellationRequested();
            return new EngineCallOutcome(result, false, false, stopwatch.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            // Различаем источник отмены: внешний токен → «отменено», внутренний
            // таймаут фазы → Failed с текстом про время ожидания (НЕ «отменено»).
            if (!externalCt.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                return new EngineCallOutcome(null, false, true, stopwatch.Elapsed.TotalMilliseconds,
                    TimeoutError(phaseTimeout));
            return new EngineCallOutcome(null, true, false, stopwatch.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            // UI не должен падать из-за неожиданной ошибки движка.
            return new EngineCallOutcome(null, false, false, stopwatch.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    /// <summary>Единый текст внутреннего таймаута фазы (секунды — инвариантно).</summary>
    private static string TimeoutError(TimeSpan phaseTimeout)
        => $"превышено время ожидания ({phaseTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} с)";

    /// <summary>Неуспешное измерение: RTF не существует — всегда NaN.</summary>
    private static SttSpeedMeasurement Fail(
        SttTranscribeStatus status, double elapsedMs, double audioSeconds,
        string engineName, string? error, int? httpStatus)
    {
        return new SttSpeedMeasurement(
            false, status, elapsedMs, double.NaN,
            null, error, httpStatus, audioSeconds, engineName);
    }

    /// <summary>Исход одного вызова движка внутри фазы: обычный результат либо
    /// локальная катастрофа (внешняя отмена / внутренний таймаут / исключение).</summary>
    private readonly record struct EngineCallOutcome(
        SttTranscribeResult? Result,   // движок ответил; null при отмене/таймауте/исключении
        bool Canceled,                 // внешняя отмена либо движок сам бросил OCE
        bool TimedOut,                 // сработал внутренний таймаут фазы
        double ElapsedMs,              // Stopwatch фазы (для прогрева не используется)
        string? Error);                // текст таймаута либо ex.Message
}
