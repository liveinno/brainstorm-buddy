using System.Globalization;


namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Определение языка ПО РЕЗУЛЬТАТУ распознавания — по доле кириллицы и латиницы (ТЗ §2.2).
/// Чистая функция без состояния.
/// </summary>
public static class LanguageDetector
{
    /// <summary>
    /// Доля букв одной письменности, начиная с которой текст считается одноязычным.
    /// Ниже — «микс». 0.9 подобрано так, чтобы «Открыть file_name.txt через Ctrl+Shift+P»
    /// (пример из ТЗ §2.2) уверенно попадал в <see cref="TextLanguage.Mixed"/>.
    /// </summary>
    public const double DominanceThreshold = 0.9;

    /// <summary>
    /// Меньшинство короче этого числа букв не делает текст смешанным.
    /// Иначе «Нажмите OK» и «Поле ID» помечались бы как «микс», что для пользователя шум:
    /// одно вкраплённое слово — это всё ещё русский текст.
    /// </summary>
    public const int MinMinorityLetters = 3;

    /// <summary>Язык текста. Пусто или без букв — <see cref="TextLanguage.Unknown"/>.</summary>
    public static TextLanguage Detect(string? text)
    {
        var (cyrillic, latin) = CountLetters(text);
        return Classify(cyrillic, latin);
    }

    /// <summary>Язык результата распознавания целиком.</summary>
    public static TextLanguage Detect(RecognitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var cyrillic = 0;
        var latin = 0;
        foreach (var word in result.AllWords)
        {
            var (c, l) = CountLetters(word.Text);
            cyrillic += c;
            latin += l;
        }

        return Classify(cyrillic, latin);
    }

    /// <summary>
    /// Доля кириллицы среди букв (0..1). Букв нет — 0.
    /// Выделено отдельно: пороги проверяются тестами напрямую, без пересборки текста.
    /// </summary>
    public static double CyrillicRatio(string? text)
    {
        var (cyrillic, latin) = CountLetters(text);
        var total = cyrillic + latin;
        return total == 0 ? 0 : (double)cyrillic / total;
    }

    /// <summary>Доля латиницы среди букв (0..1). Букв нет — 0.</summary>
    public static double LatinRatio(string? text)
    {
        var (cyrillic, latin) = CountLetters(text);
        var total = cyrillic + latin;
        return total == 0 ? 0 : (double)latin / total;
    }

    /// <summary>Язык одного слова — для пометки <see cref="RecognizedWord.Language"/>.</summary>
    public static TextLanguage DetectWord(string? word) => Detect(word);

    private static TextLanguage Classify(int cyrillic, int latin)
    {
        var total = cyrillic + latin;
        if (total == 0) return TextLanguage.Unknown;

        var majority = Math.Max(cyrillic, latin);
        var minority = Math.Min(cyrillic, latin);
        var dominant = cyrillic >= latin ? TextLanguage.Russian : TextLanguage.English;

        if (minority < MinMinorityLetters) return dominant;

        return (double)majority / total >= DominanceThreshold ? dominant : TextLanguage.Mixed;
    }

    private static (int Cyrillic, int Latin) CountLetters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);

        var cyrillic = 0;
        var latin = 0;

        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;

            if (IsCyrillic(ch)) cyrillic++;
            else if (IsLatin(ch)) latin++;
            // Прочие письменности (греческая, армянская и т.п.) намеренно не считаем:
            // продукт заявлен для RU+EN, и раздувать классификацию нечем проверить.
        }

        return (cyrillic, latin);
    }

    /// <summary>Кириллица: основной блок U+0400–U+04FF плюс дополнение U+0500–U+052F.</summary>
    private static bool IsCyrillic(char ch) => ch is >= '\u0400' and <= '\u052F';

    /// <summary>
    /// Латиница: ASCII-буквы плюс Latin-1 Supplement и Latin Extended-A.
    /// Проверка через <see cref="CharUnicodeInfo"/> была бы медленнее на горячем пути
    /// (текст прогоняется целиком при каждом распознавании), а диапазоны здесь исчерпывающие.
    /// </summary>
    private static bool IsLatin(char ch)
        => ch is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '\u00C0' and <= '\u024F';
}
