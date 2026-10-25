using System.Text;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Извлечение текста из TXT/MD. Порядок кодировок:
/// BOM (UTF-32LE/BE, UTF-16LE/BE, UTF-8) → строгий UTF-8 → сниффер UTF-16 без BOM
/// (нулевой байт-чередование) → эвристика «похоже на Windows-1251» →
/// <see cref="EncodingAmbiguousException"/> с SuggestedEncoding «windows-1251»
/// (вызывающий спрашивает пользователя и повторяет с явной кодировкой);
/// если и на кириллицу не похоже — честный отказ.
/// Повреждённую кириллицу молча не подменяем.
/// </summary>
internal static class TxtExtractor
{
    static TxtExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Task<ExtractedDocument> ExtractAsync(
        string storedPath, string displayName, string? encodingWebName, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(storedPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AttachmentRejectedException("Не удалось прочитать файл");
        }

        ct.ThrowIfCancellationRequested();
        var encoding = ResolveEncoding(bytes, encodingWebName);
        string text;
        try
        {
            text = Decode(bytes, encoding);
        }
        catch (DecoderFallbackException)
        {
            // явно выбранная пользователем кодировка тоже упала — не наша вина, но честно
            throw new AttachmentRejectedException("Не удалось определить кодировку файла");
        }

        var normalized = TextSanitizer.Normalize(text);
        if (normalized.Length == 0)
            throw new AttachmentRejectedException("Файл не содержит читаемого текста");

        var segments = new List<DocSegment>();
        var order = 0;
        var total = 0;
        var lines = normalized.Split('\n');
        var sb = new StringBuilder();
        var chunkStartLine = 1;

        // сегмент = окно непустых строк ~до 4000 символов с локатором «строки N–M»
        void Flush(int endLine)
        {
            var chunk = sb.ToString().TrimEnd('\n');
            if (chunk.Length == 0) { sb.Clear(); return; }
            var label = chunkStartLine == endLine
                ? $"строка {chunkStartLine}"
                : $"строки {chunkStartLine}–{endLine}";
            SegmentAccumulator.Add(segments, chunk, new TextAnchor("lines", label), ref order, ref total);
            sb.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            if (sb.Length > 0 && sb.Length + lines[i].Length + 1 > 4000)
            {
                Flush(lineNo - 1);
                chunkStartLine = lineNo;
            }
            if (sb.Length == 0) chunkStartLine = lineNo;
            if (lines[i].Length > 0) { sb.Append(lines[i]); sb.Append('\n'); }
            else if (sb.Length > 0) sb.Append('\n');
        }
        Flush(lines.Length);

        return Task.FromResult(new ExtractedDocument(segments, total, Array.Empty<string>()));
    }

    private static Encoding ResolveEncoding(byte[] bytes, string? encodingWebName)
    {
        if (encodingWebName is not null)
        {
            try { return Encoding.GetEncoding(encodingWebName); }
            catch (ArgumentException)
            {
                throw new AttachmentRejectedException("Неизвестная кодировка файла");
            }
        }

        if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00)) return Encoding.UTF32;
        if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF)) return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (StartsWith(bytes, 0xFF, 0xFE)) return Encoding.Unicode;
        if (StartsWith(bytes, 0xFE, 0xFF)) return Encoding.BigEndianUnicode;
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF)) return Encoding.UTF8;

        // UTF-16 no-BOM ДО strict-UTF8: кириллица UTF-16LE — пары вида XX 04,
        // все байты < 0x80, strict-UTF8 декодирует их «успешно» в контрольный
        // мусор + выживший ASCII. Сниффер должен сработать первым.
        var utf16 = SniffUtf16NoBom(bytes);
        if (utf16 is not null) return utf16;

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            strictUtf8.GetString(bytes);
            return strictUtf8;
        }
        catch (DecoderFallbackException)
        {
            // не UTF-8 — может быть однобайтная кодировка (Windows-1251 у русских текстов)
        }

        if (LooksLikeWindows1251(bytes))
            throw new EncodingAmbiguousException(
                "Не удалось определить кодировку — возможно Windows-1251", "windows-1251");

        throw new AttachmentRejectedException("Не удалось определить кодировку файла");
    }

    /// <summary>
    /// Признак «однобайтной кириллицы»: декодируем как 1251 и смотрим долю
    /// кириллических букв среди букв и отсутствие контрольного мусора. Латиница
    /// и прочие кодировки дают долю ниже — тогда честный отказ.
    /// </summary>
    private static bool LooksLikeWindows1251(byte[] bytes)
    {
        string probe;
        try { probe = Encoding.GetEncoding(1251).GetString(bytes); }
        catch (ArgumentException) { return false; }

        var letters = 0; var cyr = 0; var bad = 0;
        foreach (var ch in probe)
        {
            if (char.IsLetter(ch))
            {
                letters++;
                if (ch is >= '\u0400' and <= '\u04FF') cyr++;
            }
            else if (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))
            {
                bad++;
            }
        }
        return cyr >= 4 && cyr * 2 >= letters && bad * 500 <= probe.Length;
    }

    /// <summary>
    /// UTF-16 без BOM: блокнот сохраняет «Юникод» с BOM, но чужие экспортеры —
    /// нет. Кириллица UTF-16LE — пары «младший байт, 0x04» и целиком &lt; 0x80,
    /// поэтому strict-UTF8 «успешно» декодирует файл в мусор; эта ветка идёт
    /// ПЕРЕД strict-UTF8.
    /// Кандидаты по паритету: доля правдоподобных high-байтов {0x00, 0x03..06,
    /// 0x20..22, 0x30, 0x4E..9F} на нечётных смещениях ≥0.25 → LE, на чётных →
    /// BE. Подтверждение — script-aware score: латиница/кириллица/греческая —
    /// good, CJK Ext-A и мусор — bad, CJK Basic — нейтрал (туда свопит ASCII,
    /// поэтому считается в ±0: не даёт ни очков, ни штрафа); победитель требует
    /// ≥4 букв из латиницы/кириллицы/греческой — отсекает альтернацию «a b c».
    /// Без этой ветки UTF-16LE/BE без BOM уходил в «Не удалось определить
    /// кодировку» либо декодировался в контрольный мусор (фикстура utf16.txt).
    /// </summary>
    private static Encoding? SniffUtf16NoBom(byte[] bytes)
    {
        if (bytes.Length < 8 || (bytes.Length & 1) != 0) return null;

        static bool PlausibleHigh(byte b) =>
            b is 0x00 or 0x03 or 0x04 or 0x05 or 0x06 or 0x20 or 0x21 or 0x22
              or 0x30 || (b >= 0x4E && b <= 0x9F);

        var pairs = bytes.Length / 2;
        var oddHi = 0; var evenHi = 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (PlausibleHigh(bytes[i + 1])) oddHi++;
            if (PlausibleHigh(bytes[i])) evenHi++;
        }
        var leCandidate = oddHi * 4 >= pairs;   // ≥25% high-байтов на нечётных
        var beCandidate = evenHi * 4 >= pairs;  // ≥25% на чётных
        if (!leCandidate && !beCandidate) return null;

        // score: good(латиница/кириллица/греческая/пробелы/цифры/пунктуация) − bad;
        // CJK Basic U+4E00–9FFF не считается ни в good, ни в bad (ASCII-своп),
        // CJK Ext-A U+3400–4DBF — в bad (туда свопит кириллица U+0400–04FF).
        static (double score, int strong) Score(string s)
        {
            var good = 0; var bad = 0; var strong = 0;
            foreach (var ch in s)
            {
                if (ch == '\uFFFD') { bad++; continue; }
                if (ch is >= '\u3400' and <= '\u4DBF') { bad++; continue; }  // Ext-A = своп кириллицы
                if (ch is >= '\u4E00' and <= '\u9FFF') continue;             // CJK Basic = нейтрал
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat is System.Globalization.UnicodeCategory.Control
                    && ch is not ('\r' or '\n' or '\t')) { bad++; continue; }
                if (cat is System.Globalization.UnicodeCategory.Surrogate
                      or System.Globalization.UnicodeCategory.PrivateUse
                      or System.Globalization.UnicodeCategory.OtherNotAssigned) { bad++; continue; }
                good++;
                if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                    or >= '\u0370' and <= '\u03FF'      // греческая
                    or >= '\u0400' and <= '\u052F') strong++; // кириллица + suppl.
            }
            var n = s.Length;
            return n == 0 ? (0, 0) : ((good - bad) / (double)n, strong);
        }

        var le = Encoding.Unicode.GetString(bytes);
        var be = Encoding.BigEndianUnicode.GetString(bytes);
        var (leScore, leStrong) = Score(le);
        var (beScore, beStrong) = Score(be);

        if (leCandidate && leScore >= 0.90 && leStrong >= 4 && leScore - beScore >= 0.05)
            return Encoding.Unicode;
        if (beCandidate && beScore >= 0.90 && beStrong >= 4 && beScore - leScore >= 0.05)
            return Encoding.BigEndianUnicode;
        return null;
    }

    private static string Decode(byte[] bytes, Encoding encoding) =>
        encoding.GetString(bytes);

    private static bool StartsWith(byte[] bytes, params byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);
}
