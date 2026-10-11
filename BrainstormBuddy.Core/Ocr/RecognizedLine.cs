

namespace BrainstormBuddy.Core.Ocr;

/// <summary>Строка распознанного текста — упорядоченный слева направо набор слов.</summary>
public sealed class RecognizedLine
{
    public RecognizedLine(IReadOnlyList<RecognizedWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        Words = words;
    }

    public IReadOnlyList<RecognizedWord> Words { get; }

    /// <summary>Слова через один пробел. Исходные внутрисловные пробелы движок уже разделил.</summary>
    public string Text => string.Join(' ', Words.Select(w => w.Text));

    /// <summary>Объединяющий прямоугольник всех слов строки.</summary>
    public PixelRect Bounds => Words.Aggregate(PixelRect.Empty, (acc, w) => acc.Union(w.Bounds));

    /// <summary>
    /// Средняя уверенность по словам, у которых она известна.
    /// Ни у одного слова уверенности нет — <see cref="RecognizedWord.UnknownConfidence"/>.
    /// </summary>
    public double AverageConfidence
    {
        get
        {
            var known = Words.Where(w => w.HasConfidence).Select(w => w.Confidence).ToList();
            return known.Count == 0 ? RecognizedWord.UnknownConfidence : known.Average();
        }
    }

    public bool IsEmpty => Words.Count == 0 || Words.All(w => w.IsEmpty);

    public override string ToString() => Text;
}
