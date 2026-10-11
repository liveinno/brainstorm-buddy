namespace BrainstormBuddy.Core.Speaker;

/// <summary>
/// Потоковая диаризация «кто говорит когда» поверх непрерывного loopback-аудио.
/// Контракт fail-safe: методы никогда не бросают,
/// ошибки -> WARN в лог + null/disabled.
/// </summary>
public interface IStreamingSpeakerDiarizer : IDisposable
{
    /// <summary>Готов к работе (нативный стрим открыт, не disposed).</summary>
    bool IsReady { get; }

    /// <summary>Поколение нативного стрима: инкрементится на каждый пересозданный
    /// стрим (Reset() и авто-ресинк после переполнения очереди push). Смена эпохи
    /// означает рестарт нумерации S&lt;n&gt; — закэшированные метки («последний
    /// спикер») прошлой эпохи невалидны.</summary>
    int StreamEpoch { get; }

    /// <summary>Fire&amp;forget: чанк float-моно аудио уходит во внутреннюю очередь стрима.
    /// Rate фиксируется первым вызовом; push с другим rate отбрасывается (WARN).</summary>
    void PushLoopback(float[] samples, int sampleRate);

    /// <summary>
    /// Доминирующий спикер диапазона по покрытию сегментов. Индексы — сэмплы
    /// входного rate, отсчитываемые от первого PushLoopback (0 = первый pushed
    /// сэмпл); домен кумулятивен — Reset() его НЕ обнуляет. Диапазон до начала
    /// текущего нативного стрима клипается, целиком прошлый -> null.
    /// null также при отсутствии пересечения с сегментами.
    /// Результат дополнительно несёт <c>Spans</c> — хронологическую раскладку
    /// спикеров ВНУТРИ диапазона (см. SpeakerTurnSpan; null — только доминант)
    /// для разметки «[S1] … [S2] …» внутри одной строки транскриба.
    /// </summary>
    SpeakerIdentificationResult? IdentifyRange(long onsetSample, long endSample, int sampleRate);

    /// <summary>Новый звонок: закрыть текущий стрим и открыть чистый (S-метки только внутри звонка).</summary>
    void Reset();
}
