


namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Оценка качества и построчная склейка двух результатов распознавания одного изображения.
/// Используется при сравнении основного и повторного проходов Tesseract с разным
/// препроцессингом: каждая строка берётся из более качественного результата.
/// </summary>
/// <remarks>
/// ⚠️ Чистые функции над готовыми <see cref="RecognitionResult"/> — без платформенного UI,
/// экрана и сети. Это сделано намеренно: логика оценки покрывается юнит-тестами без
/// моделей Tesseract и без запуска реального движка.
///
/// <para><b>Почему одной уверенности недостаточно.</b> На зашумлённом или слишком мелком
/// кадре формально высокая уверенность может сопровождать мусор. Поэтому она дополняется
/// независимой оценкой доли осмысленных символов.</para>
///
/// <para><b>Метрика «доля осмысленных символов»</b> (<see cref="TextQuality"/>) — произведение
/// двух множителей:</para>
/// <list type="number">
/// <item>доля символов, которые вообще бывают в тексте: буквы, цифры, обычная пунктуация.
/// Движок, читающий чужую письменность, сыплет глифами вроде <c>¤ ¦ ‗ ▪</c> — они и режут
/// эту долю;</item>
/// <item>штраф за мусорные слова: одиночные символы, которые не бывают самостоятельным
/// словом («Ы», «ъ», «§»), и слова, где кириллица смешана с латиницей внутри одного слова
/// («Ореn» — классика чтения английского русским движком).</item>
/// </list>
///
/// <para><b>Чего метрика не умеет.</b> Если русский движок прочитал английское слово
/// целиком кириллицей и без мусора («Орен» вместо «Open»), обе строки получат близкое
/// качество — различить их без словаря нельзя. Поэтому при разнице в пределах
/// <see cref="QualityTolerance"/> выигрывает более полная строка (больше осмысленных
/// символов), а при равенстве — основной проход. Это сохраняет детерминированный результат
/// без выдуманной уверенности.</para>
/// </remarks>
public static class LineMerger
{
    /// <summary>
    /// Минимальное вертикальное перекрытие, при котором строки двух результатов считаются
    /// одной и той же строкой экрана. Один и тот же текст два прохода обводят почти
    /// одинаково, поэтому порог высокий: половина меньшей высоты.
    /// </summary>
    public const double MinVerticalOverlap = 0.5;

    /// <summary>
    /// Разница качества, ниже которой строки считаются равными по качеству. Метрика
    /// приблизительная, и «0.98 против 0.97» — шум, а не преимущество.
    /// </summary>
    public const double QualityTolerance = 0.05;

    /// <summary>Вес штрафа за мусорные слова: строка сплошь из мусора теряет половину качества.</summary>
    private const double JunkWordPenalty = 0.5;

    /// <summary>
    /// Пунктуация, которая встречается в экранном тексте. Намеренно БЕЗ <c>| ~ ^ `</c>:
    /// в реальном тексте они редки, а в шуме OCR часты, и их отсутствие в списке —
    /// полезный сигнал, а не недосмотр.
    /// </summary>
    private const string AllowedPunctuation = ".,:;!?…-–—()[]{}<>\"'«»„“”/\\+=*&%#@№$€₽_°§";

    /// <summary>Буквы, которые бывают самостоятельным словом: русские предлоги и союзы, английские «a» и «I».</summary>
    private const string AllowedSingleLetters = "аибвкосужяё" + "ai";

    /// <summary>Знаки, которые бывают самостоятельным «словом»: тире, дефис, номер, плюс-минус.</summary>
    private const string AllowedSingleSymbols = "-–—+±№§";

    /// <summary>
    /// Склеить два прохода построчно. Строки сопоставляются по геометрии, для каждой пары
    /// берётся лучшая; строки без пары сохраняются обе — их просто не увидел один из проходов.
    /// </summary>
    /// <param name="primary">Результат основного прохода. При равном качестве выигрывает он.</param>
    /// <param name="secondary">Результат повторного прохода.</param>
    /// <param name="engineName">Имя распознавателя для итога. По умолчанию — имя основного прохода.</param>
    /// <param name="elapsed">Время для итога. По умолчанию — сумма: проходы шли последовательно.</param>
    /// <param name="minVerticalOverlap">Порог вертикального перекрытия для сопоставления строк.</param>
    public static RecognitionResult Merge(
        RecognitionResult primary,
        RecognitionResult secondary,
        string? engineName = null,
        TimeSpan? elapsed = null,
        double minVerticalOverlap = MinVerticalOverlap)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);

        var secondaryLines = secondary.Lines;
        var taken = new bool[secondaryLines.Count];
        var merged = new List<RecognizedLine>(primary.Lines.Count + secondaryLines.Count);

        foreach (var line in primary.Lines)
        {
            if (line.IsEmpty) continue;

            var matchIndex = FindMatch(line, secondaryLines, taken, minVerticalOverlap);

            if (matchIndex < 0)
            {
                merged.Add(line);
                continue;
            }

            taken[matchIndex] = true;
            merged.Add(ChooseBetter(line, secondaryLines[matchIndex]));
        }

        for (var i = 0; i < secondaryLines.Count; i++)
        {
            if (taken[i] || secondaryLines[i].IsEmpty) continue;
            merged.Add(secondaryLines[i]);
        }

        // Порядок чтения: сверху вниз, при равной высоте — слева направо. OrderBy стабилен,
        // поэтому строки, совпавшие по обоим ключам, сохраняют исходный порядок.
        var ordered = merged
            .OrderBy(l => l.Bounds.Top)
            .ThenBy(l => l.Bounds.Left)
            .ToList();

        return new RecognitionResult(
            ordered,
            engineName ?? primary.EngineName,
            elapsed ?? primary.Elapsed + secondary.Elapsed);
    }

    /// <summary>
    /// Какая из двух строк лучше. Разница качества в пределах <see cref="QualityTolerance"/> —
    /// ничья, тогда берётся более полная строка, а при равной полноте — <paramref name="primary"/>.
    /// </summary>
    public static RecognizedLine ChooseBetter(RecognizedLine primary, RecognizedLine candidate)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(candidate);

        var primaryQuality = LineQuality(primary);
        var candidateQuality = LineQuality(candidate);

        if (candidateQuality - primaryQuality > QualityTolerance) return candidate;
        if (primaryQuality - candidateQuality > QualityTolerance) return primary;

        return MeaningfulCharCount(candidate.Text) > MeaningfulCharCount(primary.Text)
            ? candidate
            : primary;
    }

    /// <summary>Качество строки, 0..1. Пустая строка — 0.</summary>
    public static double LineQuality(RecognizedLine? line)
        => line is null ? 0 : TextQuality(line.Text);

    /// <summary>
    /// Доля осмысленных символов с поправкой на мусорные слова, 0..1.
    /// Отдельная функция от строки: пороги проверяются тестами напрямую, без геометрии.
    /// </summary>
    public static double TextQuality(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var visible = 0;
        var meaningful = 0;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;

            visible++;
            if (IsMeaningful(ch)) meaningful++;
        }

        if (visible == 0) return 0;

        var words = SplitWords(text);
        var junk = words.Count(IsJunkWord);
        var junkShare = words.Length == 0 ? 0 : (double)junk / words.Length;

        var score = (double)meaningful / visible * (1 - JunkWordPenalty * junkShare);

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>Сколько в тексте осмысленных символов — мера полноты строки при ничьей по качеству.</summary>
    public static int MeaningfulCharCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var count = 0;
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch) && IsMeaningful(ch)) count++;
        }

        return count;
    }

    private static int FindMatch(
        RecognizedLine line,
        IReadOnlyList<RecognizedLine> candidates,
        bool[] taken,
        double minVerticalOverlap)
    {
        var bestIndex = -1;
        var bestOverlap = 0.0;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (taken[i]) continue;

            var candidate = candidates[i];
            if (candidate.IsEmpty) continue;

            // Одна и та же строка экрана перекрывается и по вертикали, и по горизонтали.
            // Без проверки горизонтали две соседние колонки на одной высоте склеились бы в одну.
            if (!line.Bounds.IntersectsWith(candidate.Bounds)) continue;

            var overlap = line.Bounds.VerticalOverlapRatio(candidate.Bounds);
            if (overlap < minVerticalOverlap || overlap <= bestOverlap) continue;

            bestOverlap = overlap;
            bestIndex = i;
        }

        return bestIndex;
    }

    private static bool IsMeaningful(char ch)
        => char.IsLetterOrDigit(ch) || AllowedPunctuation.Contains(ch);

    private static string[] SplitWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsJunkWord(string word)
    {
        if (word.Length == 1)
        {
            var ch = word[0];

            if (char.IsDigit(ch)) return false;
            if (char.IsLetter(ch)) return !AllowedSingleLetters.Contains(char.ToLowerInvariant(ch));

            return !AllowedSingleSymbols.Contains(ch);
        }

        return HasMixedScript(word);
    }

    /// <summary>
    /// Кириллица и латиница внутри ОДНОГО слова — почти всегда ошибка движка,
    /// читавшего чужую письменность («Ореn», «Сtrl»). Настоящий смешанный текст
    /// смешивается на уровне слов, а не букв.
    /// </summary>
    /// <remarks>
    /// Диапазоны письменностей не дублируются: используется уже покрытый тестами
    /// <see cref="LanguageDetector"/>. Слово смешанное, если в нём есть и кириллица, и латиница.
    /// </remarks>
    private static bool HasMixedScript(string word)
        => LanguageDetector.CyrillicRatio(word) > 0 && LanguageDetector.LatinRatio(word) > 0;
}
