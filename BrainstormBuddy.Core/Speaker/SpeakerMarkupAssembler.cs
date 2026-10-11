using BrainstormBuddy.Stt;

namespace BrainstormBuddy.Core.Speaker;

/// <summary>
/// Сборка внутричанковой разметки «[S1] текст [S2] текст»: раскладывает слова
/// и/или текст по хронологическим спанам спикеров (speaker, секунды от начала
/// wav-чанка). Спаны покрывают диапазон чанка без дыр — слова до/после/между
/// озвученными сегментами относятся к ближайшему спану. Чистый класс: без
/// diarizer/STT зависимостей, тестируется юнитами.
/// </summary>
public static class SpeakerMarkupAssembler
{
    /// <summary>
    /// Раскладка слов со таймкодами по спанам. Каждое слово относится к спану,
    /// в котором лежит середина слова. Слова вне покрытия → ближайший край
    /// (spans[i] без дыр, поэтому край достаётся первым/последним спаном).
    /// Пустые текстовые куски (спан без слов) НЕ создаются.
    /// </summary>
    public static List<(string SpeakerId, string Text)> Assign(
        IReadOnlyList<SttWordSpan> words, IReadOnlyList<(string SpeakerId, double StartSec, double EndSec)> spans)
    {
        var runs = new List<(string SpeakerId, string Text)>();
        if (words.Count == 0 || spans.Count == 0)
            return runs;

        var sb = new System.Text.StringBuilder(128);
        var cur = -1;
        foreach (var w in words)
        {
            var mid = (w.StartSec + w.EndSec) / 2;
            var idx = SpanAt(spans, mid);
            if (idx != cur && sb.Length > 0)
            {
                runs.Add((spans[cur].SpeakerId, sb.ToString()));
                sb.Clear();
            }
            if (idx != cur)
                cur = idx;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(w.Text);
        }
        if (cur >= 0 && sb.Length > 0)
            runs.Add((spans[cur].SpeakerId, sb.ToString()));
        return runs;
    }

    /// <summary>
    /// Грубый фолбэк без таймкодов: слова плоского текста распределяются по
    /// спанам пропорционально их длительности. Используется, когда движок не
    /// выдаёт таймкоды и аудио-сплит недоступен/упал.
    /// </summary>
    public static List<(string SpeakerId, string Text)> AssignProportional(
        string text, IReadOnlyList<(string SpeakerId, double StartSec, double EndSec)> spans)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var runs = new List<(string SpeakerId, string Text)>();
        if (words.Length == 0 || spans.Count == 0)
            return runs;

        var total = 0.0;
        foreach (var s in spans) total += Math.Max(0.0, s.EndSec - s.StartSec);
        if (total <= 0)
            return runs;

        var w = 0;
        for (var i = 0; i < spans.Count && w < words.Length; i++)
        {
            var share = Math.Max(0.0, spans[i].EndSec - spans[i].StartSec) / total;
            // Последний спан забирает остаток слов (округление долей).
            var count = i == spans.Count - 1
                ? words.Length - w
                : Math.Clamp((int)Math.Round(share * words.Length), 0, words.Length - w);
            if (count <= 0)
                continue;
            var piece = string.Join(' ', words, w, count);
            w += count;
            if (runs.Count > 0 && runs[^1].SpeakerId == spans[i].SpeakerId)
                runs[^1] = (spans[i].SpeakerId, runs[^1].Text + " " + piece);
            else
                runs.Add((spans[i].SpeakerId, piece));
        }
        return runs;
    }

    /// <summary>
    /// «[S1] фраза [S2] фраза». null — меньше двух разных спикеров (одно
    /// спикерная реплика рендерится вызывающим кодом обычным «[S1] текст»).
    /// Пустые текстовые куски пропускаются: «[S2]» без слов не рождается.
    /// </summary>
    public static string? Render(IReadOnlyList<(string SpeakerId, string Text)> runs)
    {
        if (runs.Count == 0)
            return null;
        var distinct = 0;
        string? last = null;
        var sb = new System.Text.StringBuilder(runs.Count * 32);
        foreach (var (sid, txt) in runs)
        {
            if (sid != last)
            {
                distinct++;
                last = sid;
            }
            if (string.IsNullOrWhiteSpace(txt))
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append('[').Append(sid).Append("] ").Append(txt);
        }
        return distinct < 2 ? null : sb.ToString();
    }

    /// <summary>
    /// Вырезает кусок PCM16-WAV (одноканальный) по секундам от начала файла.
    /// Заголовок копируется с обновлением размеров RIFF/data. null — кривой
    /// заголовок или пустой срез. Используется аудио-сплит фолбэком.
    /// </summary>
    public static byte[]? SlicePcm16Wav(byte[] wav, double startSec, double endSec)
    {
        const int hdr = 44; // канонический PCM-WAV (WriteWav16kMono пишет именно его)
        if (wav == null || wav.Length <= hdr || endSec <= startSec)
            return null;
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
            wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
            return null;
        var dataSize = BitConverter.ToInt32(wav, 40);
        var totalSamples = dataSize / 2;
        var rate = BitConverter.ToInt32(wav, 24);
        if (rate <= 0 || totalSamples <= 0)
            return null;

        var start = Math.Clamp((int)(startSec * rate), 0, totalSamples);
        var end = Math.Clamp((int)(endSec * rate), 0, totalSamples);
        if (end <= start)
            return null;

        var slice = new byte[hdr + (end - start) * 2];
        Buffer.BlockCopy(wav, 0, slice, 0, hdr);
        BitConverter.GetBytes(hdr - 8 + (end - start) * 2).CopyTo(slice, 4);
        BitConverter.GetBytes((end - start) * 2).CopyTo(slice, 40);
        Buffer.BlockCopy(wav, hdr + start * 2, slice, hdr, (end - start) * 2);
        return slice;
    }

    private static int SpanAt(IReadOnlyList<(string SpeakerId, double StartSec, double EndSec)> spans, double t)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            if (t >= spans[i].StartSec && t < spans[i].EndSec)
                return i;
        }
        return t < spans[0].StartSec ? 0 : spans.Count - 1; // ближайший край
    }
}
