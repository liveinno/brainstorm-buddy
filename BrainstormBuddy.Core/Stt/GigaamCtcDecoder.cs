namespace BrainstormBuddy.Stt;

/// <summary>
/// Жадный CTC-декодер для GigaAM v2 (посимвольный словарь: пробел + русский алфавит,
/// 33 метки; CTC-blank = индекс blank, обычно последний = 33, vocab=34).
/// argmax по кадрам → схлопнуть повторы → убрать blank → символы.
/// </summary>
public sealed class GigaamCtcDecoder
{
    private readonly string[] _labels; // 33 символа
    private readonly int _blank;

    public GigaamCtcDecoder(string[] labels, int blankIndex)
    {
        _labels = labels;
        _blank = blankIndex;
    }

    /// <summary>Загрузить словарь из labels.json (JSON-массив строк).</summary>
    public static GigaamCtcDecoder FromLabelsFile(string labelsJsonPath)
    {
        var json = File.ReadAllText(labelsJsonPath);
        var labels = System.Text.Json.JsonSerializer.Deserialize<string[]>(json)
                     ?? throw new InvalidDataException("labels.json пуст");
        return new GigaamCtcDecoder(labels, labels.Length); // blank = следующий за метками (33)
    }

    public string Decode(float[][] logProbs)
    {
        var sb = new System.Text.StringBuilder();
        int prev = -1;
        foreach (var frame in logProbs)
        {
            int best = 0; float bestV = frame[0];
            for (int v = 1; v < frame.Length; v++)
                if (frame[v] > bestV) { bestV = frame[v]; best = v; }

            if (best != prev && best != _blank && best < _labels.Length)
                sb.Append(_labels[best]);
            prev = best;
        }
        return sb.ToString().Trim();
    }
}
