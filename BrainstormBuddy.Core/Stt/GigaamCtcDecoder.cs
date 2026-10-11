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

    /// <summary>
    /// То же жадное разбиение, что и Decode, но дополнительно выдаёт слова с
    /// границами: StartSec — кадр первого токена слова, EndSec — кадр после
    /// последнего токена слова (пробел закрывает слово, сам в слова не входит).
    /// secondsPerFrame — длительность одного encoder-кадра (T log_probs /
    /// длительность аудио); melFrames/2 (домен log2 с center=true) НЕ равен T.
    /// Нулевая длительность слова → EndSec = StartSec + secondsPerFrame.
    /// </summary>
    public List<SttWordSpan> DecodeWords(float[][] logProbs, double secondsPerFrame)
    {
        var words = new List<SttWordSpan>(Math.Min(logProbs.Length / 8, 512));
        var word = new System.Text.StringBuilder(32);
        int wordStart = 0, wordEnd = 0;
        int prev = -1;
        for (int t = 0; t < logProbs.Length; t++)
        {
            var frame = logProbs[t];
            int best = 0; float bestV = frame[0];
            for (int v = 1; v < frame.Length; v++)
                if (frame[v] > bestV) { bestV = frame[v]; best = v; }
            if (best == prev || best == _blank)
            {
                // Повтор/blank — продолжение текущего символа; граница не двигается.
            }
            else
            {
                var label = _labels[best];
                if (string.IsNullOrWhiteSpace(label))
                    FlushWord();           // пробел-метка = конец слова
                else
                {
                    if (word.Length == 0)
                        wordStart = t;
                    wordEnd = t;           // последний НЕпробельный кадр слова
                    word.Append(label);
                }
            }
            prev = best;
        }
        FlushWord();
        return words;

        void FlushWord()
        {
            if (word.Length == 0)
                return;
            var start = wordStart * secondsPerFrame;
            var end = Math.Max(start + secondsPerFrame, (wordEnd + 1) * secondsPerFrame);
            words.Add(new SttWordSpan(word.ToString(), start, end));
            word.Clear();
        }
    }
}
