namespace BrainstormBuddy.Core.Speaker;

/// <summary>
/// Итог IdentifyRange. SpeakerId — доминирующий спикер диапазона (метка чанка,
/// как раньше). Spans — ВНУТРИЧАНКОВАЯ раскладка: хронологические повороты
/// спикеров, покрывающие весь запрошенный диапазон; нужна разметке «[S1] …
/// [S2] …» внутри одной строки транскриба. null — только доминант (пустой
/// снимок сегментов), вызывающий код помечает всю реплику SpeakerId.
/// </summary>
public readonly record struct SpeakerIdentificationResult(
    string SpeakerId, double Similarity, bool IsNewSpeaker,
    IReadOnlyList<SpeakerTurnSpan>? Spans = null);
