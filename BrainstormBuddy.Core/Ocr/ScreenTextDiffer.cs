using System.Text;

namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Сравнение текста, снятого с экрана: нормализация и близость по токенам (Jaccard).
/// </summary>
/// <remarks>
/// <para>
/// Чистые функции без состояния и ввода-вывода — покрываются юнит-тестами без экрана
/// и без Tesseract. Нужны наблюдателю <see cref="ScreenTextWatcher"/>: OCR одного и того же
/// кадра шумит, поэтому «тот же текст» определяется не равенством строк, а долей общих слов.
/// </para>
/// <para>
/// Пороги (стабильность <c>0.85</c>, смена — из конфига) подобраны по поведению OCR на
/// неподвижном тексте: отдельные слова дрожат, но большая часть токенов совпадает.
/// </para>
/// </remarks>
public static class ScreenTextDiffer
{
    /// <summary>Порог «текст стабилен»: доля общих токенов между соседними опросами.</summary>
    public const double StabilityThreshold = 0.85;

    /// <summary>
    /// Нормализация: нижний регистр (инвариантный), пунктуация — разделитель,
    /// любые пробелы — в один. Результат не содержит краевых пробелов.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                // Пунктуация и пробелы — разделители: «привет,мир» → «привет мир»,
                // «конец.» и «конец» считаются одним словом.
                if (builder.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Доля общих токенов (Jaccard): пересечение / объединение множеств слов.
    /// Оба текста пусты — <c>1.0</c>; ровно один пуст — <c>0.0</c>.
    /// </summary>
    public static double TokenJaccard(string? a, string? b)
    {
        var left = Tokens(a);
        var right = Tokens(b);

        if (left.Count == 0 && right.Count == 0)
        {
            return 1.0;
        }

        if (left.Count == 0 || right.Count == 0)
        {
            return 0.0;
        }

        var intersection = 0;
        foreach (var token in left)
        {
            if (right.Contains(token))
            {
                intersection++;
            }
        }

        var union = left.Count + right.Count - intersection;
        return union == 0 ? 1.0 : (double)intersection / union;
    }

    /// <summary>
    /// Стабилен ли текст между двумя опросами (нормализованные строки):
    /// доля общих токенов не ниже <see cref="StabilityThreshold"/>.
    /// </summary>
    public static bool IsStable(string? previousNormalized, string? currentNormalized)
        => TokenJaccard(previousNormalized, currentNormalized) >= StabilityThreshold;

    /// <summary>
    /// Достаточно ли текст отличается от последнего отправленного, чтобы считаться
    /// новым: доля общих токенов НИЖЕ порога смены (чем выше порог, тем чаще срабатывает).
    /// </summary>
    public static bool IsSignificantChange(string? lastSentNormalized, string? currentNormalized, double changeThreshold)
        => TokenJaccard(lastSentNormalized, currentNormalized) < changeThreshold;

    /// <summary>
    /// Тот же гейт смены, но против ИСТОРИИ недавно отправленных текстов:
    /// <c>true</c> только если текущий текст отличается от КАЖДОЙ записи (доля общих
    /// токенов ниже порога со всеми). Пустая история — смена значима.
    /// </summary>
    /// <remarks>
    /// Нужен дедупу по истории в <see cref="ScreenTextWatcher"/>: сравнение только с
    /// последним отправленным пропускало повтор, когда пользователь уходил с вопроса
    /// на другой текст и возвращался — «открутил назад и вернулся».
    /// </remarks>
    public static bool IsSignificantChange(
        IReadOnlyCollection<string> sentHistory, string? currentNormalized, double changeThreshold)
    {
        ArgumentNullException.ThrowIfNull(sentHistory);

        foreach (var sent in sentHistory)
        {
            if (!IsSignificantChange(sent, currentNormalized, changeThreshold))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Похоже ли на вопрос/задание, которое стоит отправить LLM: есть «?», либо первое
    /// слово — вопросительное/императивное. Эвристика намеренно простая и без словарей:
    /// ложные срабатывания гасит порог смены текста, ложные пропуски — цена молчания.
    /// </summary>
    public static bool LooksLikeQuestion(string? text, int minLen)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length < Math.Max(0, minLen))
        {
            return false;
        }

        if (trimmed.Contains('?'))
        {
            return true;
        }

        var tokens = Normalize(trimmed).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        return QuestionStarters.Contains(tokens[0]) || ImperativeStarters.Contains(tokens[0]);
    }

    private static HashSet<string> Tokens(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return [];
        }

        return new HashSet<string>(normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    /// <summary>Вопросительные слова в начале фразы (ru+en).</summary>
    private static readonly HashSet<string> QuestionStarters = new(StringComparer.Ordinal)
    {
        // ru
        "что", "кто", "как", "какой", "какая", "какое", "какие", "каков", "который", "которая",
        "которое", "которые", "чем", "чём", "чей", "чья", "чьё", "чьи", "где", "когда", "куда",
        "откуда", "почему", "зачем", "сколько", "насколько", "разве", "неужели",
        // en
        "who", "what", "why", "how", "when", "where", "which", "whose", "whom", "is", "are",
        "was", "were", "do", "does", "did", "can", "could", "should", "would", "will", "shall",
        "may", "might", "must",
    };

    /// <summary>Императивы-задания в начале фразы (ru+en): «решите», «вычислите», «назовите».</summary>
    private static readonly HashSet<string> ImperativeStarters = new(StringComparer.Ordinal)
    {
        // ru
        "реши", "решите", "вычисли", "вычислите", "назови", "назовите", "найди", "найдите",
        "определи", "определите", "объясни", "объясните", "переведи", "переведите", "докажи",
        "докажите", "допиши", "допишите", "продолжи", "продолжите", "заполни", "заполните",
        "выбери", "выберите", "сравни", "сравните", "напиши", "напишите", "перечисли",
        "перечислите", "построй", "постройте", "упрости", "упростите", "запиши", "запишите",
        // en
        "solve", "compute", "calculate", "find", "explain", "define", "list", "describe",
        "determine", "evaluate", "simplify", "translate", "write", "name", "give", "prove",
        "complete", "fill", "choose", "select", "compare", "construct", "state", "provide",
    };
}
