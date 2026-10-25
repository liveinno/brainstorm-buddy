using System.Text;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Нормализация текста запроса для лексического поиска (memory recall и поиск
/// по встречам). Служебные маркеры канала («[secret]», «[screen]») — метки
/// источника, а не содержание вопроса: в MATCH/coverage они не участвуют.
/// Общие вопросительные/служебные слова отбрасываются до усечения списка
/// терминов, иначе длинный естественный вопрос вытесняет редкую сущность
/// («waterfall», «sensor supply») из кандидатов и ранжирования. Если после
/// фильтра не осталось ни одного термина, возвращается исходный набор —
/// запрос «что в памяти?» сохраняет прежнее поведение.
/// </summary>
internal static class QueryText
{
    private static readonly string[] ServiceMarkers = ["[secret]", "[screen]"];

    /// <summary>Вопросительные, служебные и поисковые глаголы, которые не
    /// различают факты/строки встреч и не должны влиять на покрытие.</summary>
    private static readonly HashSet<string> CommonStopWords = new(StringComparer.Ordinal)
    {
        // русские местоимения/предлоги/связки и вопросительные слова
        "а", "без", "бы", "был", "была", "были", "было", "быть", "в", "во",
        "вот", "все", "всё", "вы", "где", "да", "для", "до", "его", "ее", "её",
        "если", "есть", "еще", "ещё", "же", "за", "здесь", "и", "из", "или",
        "им", "их", "к", "как", "какая", "какие", "какой", "когда", "кто", "ли",
        "мне", "мной", "может", "можешь", "можете", "можно", "мы", "на", "над",
        "нам", "нас", "не", "нет", "но", "ну", "о", "об", "он", "она", "они",
        "оно", "от", "по", "под", "при", "про", "с", "со", "так", "там", "тебе",
        "тебя", "то", "тоже", "только", "тут", "ты", "у", "уже", "что", "чтобы",
        "эта", "эти", "это", "я",
        // поисковые глаголы/вежливость
        "дай", "дайте", "найди", "найдите", "найти", "покажи", "покажите",
        "показать", "поиск", "поискать", "поищи", "искать", "ищи", "подскажи",
        "подскажите", "пожалуйста", "расскажи", "расскажите", "рассказать",
        "скажи", "скажите", "сказать", "выведи", "вывести",
        // тип вопроса («что решили/обсудили») — категория, а не тема. Иначе
        // df=1 у шаблонного глагола даёт idf≈6 и вытесняет редкую тему (QA) из top.
        "решили", "решил", "решила", "решило", "обсудили", "обсудил", "обсудила",
        "договорились", "обговорили", "итог", "итоги", "decided", "discussed",
        "agreed",
        // английские служебные/поисковые
        "a", "about", "an", "and", "are", "can", "could", "did", "do", "does",
        "find", "for", "from", "how", "i", "in", "is", "me", "of", "on", "or",
        "please", "search", "show", "tell", "the", "to", "was", "we", "were",
        "what", "when", "where", "who", "with", "you",
    };

    /// <summary>Маркеры запроса «нужен ПOЛНЫЙ список», а не несколько фактов:
    /// «все задачи», «полный перечень», «перечисли всё», «all tasks», «every
    /// meeting». Такой вопрос оправдывает бюджет дополнительного поиска и снятие
    /// лимита по количеству фактов — иначе модель не может выполнить просьбу.
    /// Слова ищутся по началу токена: «все/всех/всё/всем» покрывают падежи.</summary>
    private static readonly string[] FullListMarkers =
    [
        "все", "всех", "всё", "всем", "полный", "полное", "полностью",
        "перечисли", "перечислить", "перечень", "список", "каждый", "каждую",
        "каждого", "all", "every", "each", "complete", "full", "list",
    ];

    /// <summary>true — запрос требует полного списка (см. <see cref="FullListMarkers"/>).</summary>
    public static bool WantsFullList(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var text = StripServiceMarkers(query).ToLowerInvariant();
        foreach (var raw in text.Split(
            new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')',
                    '«', '»', '"', '—', '–', '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim();
            foreach (var marker in FullListMarkers)
            {
                // Латиница — только точное слово («all» не должно ловить
                // «allow»/«install»); кириллица — по началу токена (падежи).
                var latin = marker.All(c => c < 128);
                if (latin ? w == marker : w.StartsWith(marker, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Снять ведущие маркеры канала. Маркер в середине текста не
    /// трогаем: там он может быть частью цитаты/факта.</summary>
    public static string StripServiceMarkers(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query ?? string.Empty;
        var s = query.TrimStart();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var marker in ServiceMarkers)
            {
                if (!s.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
                s = s[marker.Length..].TrimStart();
                changed = true;
            }
        }
        return s;
    }

    public static bool IsStopWord(string word) =>
        word.Length > 0 && CommonStopWords.Contains(word.ToLowerInvariant());

    /// <summary>Слово запроса покрыто токеном контента: общий префикс ≥2 для
    /// слов ≤2 букв, ≥3 для слов ≤4 букв и ≥4 для более длинных. Двухбуквенный
    /// термин («QA», «AI», «ML», «ID») — это тема, а не шум: ранее порог 3
    /// делал «qa» непокрываемым, и такие факты вытеснялись служебными стемами.
    /// «новые»↔«новости» и «ндс»↔«нас» по-прежнему не матчатся.</summary>
    internal static bool SharesStem(string a, string b)
    {
        var t = b.ToLowerInvariant();
        int n = Math.Min(a.Length, t.Length), lcp = 0;
        while (lcp < n && a[lcp] == t[lcp]) lcp++;
        int need = n <= 2 ? 2 : n <= 4 ? 3 : 4;
        return lcp >= need;
    }

    /// <summary>Опечатка служебного слова («поисктаь» ↔ «поискать»): слова
    /// короче 4 букв не проверяем — иначе «имя»/«наши» цепляются за «им»/«на».
    /// Только для слов, которых нет в корпусе (df=0): корректные формы
    /// («показатель») остаются темой.</summary>
    public static bool IsNearStopWord(string word, ISet<string>? domainStopWords = null)
    {
        if (word.Length < 4) return false;
        foreach (var sw in CommonStopWords)
            if (sw.Length >= 4 && SharesStem(word, sw)) return true;
        if (domainStopWords != null)
            foreach (var sw in domainStopWords)
                if (sw.Length >= 4 && SharesStem(word, sw)) return true;
        return false;
    }

    /// <summary>Убрать служебные слова, сохранив теги «#...». Если фильтр
    /// вычистил весь запрос, возвращаем исходные слова (деградация, не пустота),
    /// пока вызывающий не запретил fallback: поиску по дате нужен пустой набор
    /// терминов, иначе «что обсуждали вчера» ищет слово «обсудили» внутри
    /// транскрипта и перебивает листинг встреч периода.</summary>
    public static List<string> FilterScaffolding(
        IReadOnlyList<string> words, ISet<string>? domainStopWords = null,
        bool fallbackToAll = true)
    {
        var filtered = new List<string>(words.Count);
        foreach (var w in words)
            if (w.StartsWith('#') || !IsStopWord(w) && domainStopWords?.Contains(w.ToLowerInvariant()) != true)
                filtered.Add(w);
        return filtered.Count > 0 || !fallbackToAll ? filtered : words.ToList();
    }

    /// <summary>Соседние пары/тройки смысловых слов запроса — сигнал точной
    /// темы («sensor supply»), который не размывается остальными словами
    /// вопроса. Строится по отфильтрованному списку в исходном порядке.</summary>
    public static HashSet<string> BuildPhraseNgrams(IReadOnlyList<string> contentWords)
    {
        var ngrams = new HashSet<string>(StringComparer.Ordinal);
        for (var n = 2; n <= 3; n++)
            for (var i = 0; i + n <= contentWords.Count; i++)
                ngrams.Add(string.Join(' ', contentWords.Skip(i).Take(n)));
        return ngrams;
    }

    /// <summary>Lowercase + не-буквы/цифры → пробел, пробелы схлопнуты.</summary>
    public static string NormalizeForPhrase(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    public static int CountPhraseMatches(string normalizedContent, HashSet<string> ngrams)
    {
        if (ngrams.Count == 0 || normalizedContent.Length == 0) return 0;
        var padded = " " + normalizedContent + " ";
        var count = 0;
        foreach (var ngram in ngrams)
            if (padded.Contains(" " + ngram + " ", StringComparison.Ordinal))
                count++;
        return count;
    }
}
