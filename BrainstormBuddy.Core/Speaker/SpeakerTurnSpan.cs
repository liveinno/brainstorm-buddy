namespace BrainstormBuddy.Core.Speaker;

/// <summary>
/// Один поворот речи внутри IdentifyRange: границы — сэмплы во входном домене
/// вызова (от первого PushLoopback, как аргументы IdentifyRange), оба конца
/// клипнуты в запрошенный диапазон. Списки спанов покрывают диапазон БЕЗ дыр:
/// паузы между нативными сегментами отдаются соседнему спану (см.
/// SpeakerSegmentMapper.SpeakerSpans). EndSample > StartSample всегда.
/// </summary>
public readonly record struct SpeakerTurnSpan(string SpeakerId, long StartSample, long EndSample)
{
    /// <summary>Длительность спана в сэмплах.</summary>
    public long LengthSamples => EndSample - StartSample;
}
