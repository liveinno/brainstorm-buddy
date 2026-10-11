namespace BrainstormBuddy.Stt;

/// <summary>
/// Распознанное слово с границами внутри входного аудио: секунды от начала
/// переданного WAV/слайса. Источник таймкодов — CTC-фреймы GigaAM (точность
/// ~шаг encoder-выхода, десятки мс): достаточно для разметки спикеров по
/// diar-спанам, это НЕ выравнивание уровня forced-aligner.
/// </summary>
public readonly record struct SttWordSpan(string Text, double StartSec, double EndSec);

/// <summary>
/// Текст + пословные таймкоды. Text — конкатенация Words через пробел
/// (идентичен выходу текстового Decode на том же log_probs).
/// </summary>
public sealed record TimedTranscript(string Text, IReadOnlyList<SttWordSpan> Words);
