using System.Text;

namespace BrainstormBuddy.Audio;

/// <summary>
/// Текстовая склейка незаконченных мыслей («семантический» эндпойнтинг на уровне ТЕКСТА).
///
/// Почему на тексте, а не на аудио: дефолтный STT (GigaAM) не выдаёт пунктуации и работает на
/// слабом CPU, а LLM-классификатор перед нарезкой аудио не влезает в бюджет латентности и крадёт
/// слот у ответа (см. ENDPOINTING_PLAN §3). Поэтому аудио режет адаптивный порог (подход 1), а
/// эта надстройка на уже распознанном lowercase-тексте решает: реплика похожа на законченную
/// мысль → отдать агентам; оборвана на союзе/предлоге/частице → придержать и склеить со следующим
/// фрагментом. Полностью эвристика, без LLM, деградирует в «отдать как есть».
/// </summary>
public sealed class TurnAggregator
{
    private readonly int _maxHold;       // максимум фрагментов в удержании (защита от залипания)
    private readonly int _maxHoldChars;  // и по длине — не копим бесконечно
    private readonly StringBuilder _pending = new();
    private int _heldCount;

    public TurnAggregator(int maxHold = 3, int maxHoldChars = 400)
    {
        _maxHold = Math.Max(1, maxHold);
        _maxHoldChars = Math.Max(40, maxHoldChars);
    }

    /// <summary>
    /// Подать фрагмент. Возвращает текст, который пора отдать агентам, либо null — если фрагмент
    /// придержан (мысль не закончена) и ждёт продолжения.
    /// </summary>
    public string? Push(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return Flush();

        if (_pending.Length > 0) _pending.Append(' ');
        _pending.Append(fragment.Trim());
        _heldCount++;

        string combined = _pending.ToString();
        bool forceOut = _heldCount >= _maxHold || combined.Length >= _maxHoldChars;

        if (!forceOut && SentenceCompleteness.LooksIncomplete(combined))
            return null; // мысль оборвана — ждём продолжения

        Reset();
        return combined;
    }

    /// <summary>Отдать накопленное принудительно (напр. пауза LLM снята / стоп).</summary>
    public string? Flush()
    {
        if (_pending.Length == 0) return null;
        var s = _pending.ToString();
        Reset();
        return s;
    }

    public int HeldCount => _heldCount;
    public bool HasPending => _pending.Length > 0;

    private void Reset() { _pending.Clear(); _heldCount = 0; }
}

/// <summary>
/// Эвристики завершённости русской фразы для lowercase-текста БЕЗ пунктуации (выход GigaAM).
/// Вопрос определяется по лид-словам в НАЧАЛЕ (в русском вопросительные слова стоят спереди),
/// незавершённость — по «висящему» служебному слову в конце (союз/предлог/частица).
/// </summary>
public static class SentenceCompleteness
{
    // Слова, на которых фраза обрываться НЕ должна (значит мысль продолжается).
    private static readonly HashSet<string> DanglingTail = new(StringComparer.OrdinalIgnoreCase)
    {
        // сочинительные/подчинительные союзы. «да» здесь НЕТ: как союз («хлеб да соль») — редкость,
        // а как подтверждение — постоянно; удержание «да» глотало ответы юзера (баг живого теста).
        "и", "а", "но", "или", "либо", "что", "чтобы", "как", "чем", "если", "когда",
        "потому", "поэтому", "хотя", "пока", "чтоб", "будто", "словно", "тоже", "также", "причём",
        "которая", "который", "которое", "которые", "которых", "которым", "котором",
        // предлоги
        "в", "во", "на", "с", "со", "к", "ко", "по", "для", "из", "изо", "от", "ото", "до", "при",
        "про", "под", "над", "за", "о", "об", "обо", "у", "без", "через", "между", "перед", "около",
        // частицы, на которых мысль обычно не заканчивается
        "не", "ни", "же", "бы", "ли", "то", "вот", "уж", "аж",
        // вводные-обрывки
        "это", "так", "типа", "мол", "ну",
    };

    // Лид-слова вопроса (в начале клаузы).
    private static readonly HashSet<string> QuestionLead = new(StringComparer.OrdinalIgnoreCase)
    {
        "что", "кто", "как", "какой", "какая", "какое", "какие", "каких", "каком", "какому",
        "почему", "зачем", "отчего", "где", "когда", "сколько", "чем", "куда", "откуда", "чему",
        "кому", "кого", "чей", "чья", "чьё", "разве", "неужели", "ли",
        // императивы-просьбы (тоже требуют ответа)
        "расскажи", "расскажите", "объясни", "объясните", "поясни", "поясните",
        "перечисли", "перечислите", "назови", "назовите", "опиши", "опишите",
    };

    /// <summary>Похоже, что мысль оборвана (заканчивается служебным словом) → стоит подождать.</summary>
    public static bool LooksIncomplete(string text)
    {
        var last = LastWord(text);
        if (last.Length == 0) return false;
        // Очень короткий обрывок (1-2 слова) без явного финала тоже считаем незаконченным.
        return DanglingTail.Contains(last);
    }

    /// <summary>Похоже на вопрос: начинается с вопросительного/побудительного слова.</summary>
    public static bool LooksLikeQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var first = FirstWord(text);
        return first.Length > 0 && QuestionLead.Contains(first);
    }

    private static string FirstWord(string text)
    {
        foreach (var w in Words(text)) return w;
        return "";
    }

    private static string LastWord(string text)
    {
        string last = "";
        foreach (var w in Words(text)) last = w;
        return last;
    }

    private static IEnumerable<string> Words(string text)
    {
        foreach (var raw in text.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '!', '?', ';', ':', '"', '«', '»', '(', ')', '-', '—' },
                                       StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim().ToLowerInvariant();
            if (w.Length > 0) yield return w;
        }
    }
}
