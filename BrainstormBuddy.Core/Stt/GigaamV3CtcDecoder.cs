namespace BrainstormBuddy.Stt;

/// <summary>
/// Жадный CTC-декодер GigaAM v3 (e2e CTC, num_classes=257, BPE-словарь SentencePiece).
/// vocab.txt — строки «token&lt;пробел/таб&gt;id»; ids 0..256, где «&lt;unk&gt;»=0, «&lt;blk&gt;»=256 (blank).
/// argmax по кадрам → схлопнуть повторы → убрать blank/unk → конкатенация токенов,
/// маркер пробела «▁» (U+2581) в начале подслова → ' ', ведущий пробел отрезается.
/// </summary>
public sealed class GigaamV3CtcDecoder
{
    public const string ModelFileName = "v3_e2e_ctc.int8.onnx";
    public const string VocabFileName = "v3_e2e_ctc_vocab.txt";

    private const char SpaceMark = '▁'; // U+2581 — маркер начала слова в SentencePiece-BPE
    private readonly string[] _tokens;  // id → токен
    private readonly int _blankId;      // id токена "<blk>" (256 в релизном vocab)
    private readonly int _unkId;        // id токена "<unk>" (0) — пропускаем при сборке

    public GigaamV3CtcDecoder(string[] idToToken, int blankId, int unkId = 0)
    {
        _tokens = idToToken;
        _blankId = blankId;
        _unkId = unkId;
    }

    /// <summary>Загрузить словарь из v3_e2e_ctc_vocab.txt («token id» построчно).</summary>
    public static GigaamV3CtcDecoder FromVocabFile(string vocabPath)
    {
        var pairs = new List<(string token, int id)>();
        int maxId = -1;
        foreach (var raw in File.ReadLines(vocabPath))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            int sp = line.LastIndexOf(' ');
            int tab = line.LastIndexOf('\t');
            int sep = Math.Max(sp, tab);
            if (sep <= 0 || !int.TryParse(line.AsSpan(sep + 1), out int id)) continue;
            pairs.Add((line[..sep], id));
            if (id > maxId) maxId = id;
        }
        if (maxId < 0) throw new InvalidDataException($"vocab пуст: {vocabPath}");

        var tokens = new string[maxId + 1];
        Array.Fill(tokens, "");
        foreach (var (token, id) in pairs) tokens[id] = token;

        int blank = Array.FindIndex(tokens, t => t == "<blk>");
        if (blank < 0) blank = tokens.Length - 1; // запасной вариант: последний id
        int unk = Array.FindIndex(tokens, t => t == "<unk>");
        return new GigaamV3CtcDecoder(tokens, blank, unk < 0 ? -1 : unk);
    }

    /// <summary>log_probs [T][vocab] → текст (greedy CTC + BPE-сборка).</summary>
    public string Decode(float[][] logProbs)
    {
        var sb = new System.Text.StringBuilder();
        int prev = -1;
        foreach (var frame in logProbs)
        {
            int best = 0; float bestV = frame[0];
            for (int v = 1; v < frame.Length; v++)
                if (frame[v] > bestV) { bestV = frame[v]; best = v; }

            if (best != prev && best != _blankId && best != _unkId && best < _tokens.Length)
                sb.Append(_tokens[best]);
            prev = best;
        }
        return sb.Replace(SpaceMark, ' ').ToString().Trim();
    }

    /// <summary>
    /// То же жадное разбиение, что и Decode, но выдаёт слова с границами:
    /// StartSec — кадр первого токена слова, EndSec — кадр после последнего
    /// токена слова. Слово закрывает токен-разделитель: SentencePiece помечает
    /// начало слова маркером '_', поэтому слово рвётся на токене, начинающемся
    /// с '_' (и на редком отдельном токене-пробеле). secondsPerFrame —
    /// длительность одного encoder-кадра (T log_probs / длительность аудио).
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
            if (best == prev || best == _blankId || best == _unkId || best >= _tokens.Length)
            {
                // Повтор/blank/unk — продолжение текущего токена; граница не двигается.
            }
            else
            {
                var piece = _tokens[best];
                if (piece.StartsWith(SpaceMark))
                {
                    FlushWord();           // новый токен с '_' — начало нового слова
                    wordStart = t;
                    wordEnd = t;
                    word.Append(piece.AsSpan(1));
                }
                else if (string.IsNullOrWhiteSpace(piece))
                    FlushWord();           // редкий отдельный пробел-токен
                else
                {
                    if (word.Length == 0)
                        wordStart = t;
                    wordEnd = t;           // последний токен-кадр слова
                    word.Append(piece);
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
