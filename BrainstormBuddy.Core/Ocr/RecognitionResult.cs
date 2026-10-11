

namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Результат одного прохода распознавания. Возвращается любой реализацией ITextRecognizer
/// (интерфейс — соседний ITextRecognizer.cs: домен не должен знать про движки).
/// </summary>
public sealed class RecognitionResult
{
    public static readonly RecognitionResult Empty =
        new(Array.Empty<RecognizedLine>(), engineName: string.Empty, elapsed: TimeSpan.Zero);

    public RecognitionResult(
        IReadOnlyList<RecognizedLine> lines,
        string engineName,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(engineName);

        Lines = lines;
        EngineName = engineName;
        Elapsed = elapsed;
    }

    public IReadOnlyList<RecognizedLine> Lines { get; }

    /// <summary>Имя распознавателя для диагностики и окна результата (ТЗ §4).</summary>
    public string EngineName { get; }
    /// <summary>Время распознавания. Показывается в метаданных окна результата.</summary>
    public TimeSpan Elapsed { get; }

    public IEnumerable<RecognizedWord> AllWords => Lines.SelectMany(l => l.Words);

    public bool IsEmpty => Lines.Count == 0 || Lines.All(l => l.IsEmpty);

    /// <summary>Сырой текст «как на экране»: строки через <c>\n</c>, без постобработки.</summary>
    public string RawText => string.Join("\n", Lines.Select(l => l.Text));

    public PixelRect Bounds => Lines.Aggregate(PixelRect.Empty, (acc, l) => acc.Union(l.Bounds));

    /// <summary>
    /// Средняя уверенность по всем словам с известной уверенностью.
    /// Нет ни одного — <see cref="RecognizedWord.UnknownConfidence"/>.
    /// Используется для оценки необходимости повторного прохода с усиленным препроцессингом.
    /// </summary>
    public double AverageConfidence
    {
        get
        {
            var known = AllWords.Where(w => w.HasConfidence).Select(w => w.Confidence).ToList();
            return known.Count == 0 ? RecognizedWord.UnknownConfidence : known.Average();
        }
    }

    public int WordCount => AllWords.Count(w => !w.IsEmpty);

    public int CharCount => AllWords.Sum(w => w.Text.Length);
}
