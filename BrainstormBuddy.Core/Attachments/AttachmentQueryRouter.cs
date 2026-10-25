using System.Text.RegularExpressions;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>Диапазон страниц документа (1-based, включительно).</summary>
public readonly record struct PageInterval(int From, int To)
{
    public bool Contains(int page) => page >= From && page <= To;
    public override string ToString() => From == To ? From.ToString() : $"{From}-{To}";
}

/// <summary>Как отвечать на вопрос о вложениях.</summary>
public enum AttachmentQueryKind
{
    /// <summary>Обычная сборка контекста (chunk-бюджет, lexical+page скоринг).</summary>
    General,
    /// <summary>Вопрос адресован конкретным страницам одного документа.</summary>
    Pages,
    /// <summary>Вопрос ко всему документу — map-reduce по всем страницам,
    /// без подмены выборкой.</summary>
    WholeDocument,
    /// <summary>Неоднозначность документа — ответить коротким уточнением,
    /// не смешивая контексты.</summary>
    Clarify,
}

/// <summary>
/// Результат маршрутизации вопроса.
/// <paramref name="Document"/> — выбранный документ (для Pages/WholeDocument обязателен,
/// для General — подсказка приоритета для <c>AttachmentContextBuildOptions</c>).
/// <paramref name="Candidates"/> — имена-конкуренты для Clarify.
/// <paramref name="MissingPages"/> — запрошенные страницы за пределами документа
/// (честное «нет таких страниц», а не молчаливая выборка).
/// </summary>
public sealed record AttachmentQuery(
    AttachmentQueryKind Kind,
    StoredDocument? Document,
    IReadOnlyList<PageInterval> Pages,
    IReadOnlyList<StoredDocument> Candidates,
    IReadOnlyList<int> MissingPages)
{
    public static readonly AttachmentQuery General =
        new(AttachmentQueryKind.General, null, [], [], []);
}

/// <summary>
/// Маршрутизация вопроса о вложениях встречи: явное имя файла/выбор документа,
/// страничные адреса (только 1-based адреса PDF — «стр. 87», «5-8», «pages 3–4»),
/// вопрос «по всему документу», неоднозначность → Clarify вместо смешивания.
/// Работает на метаданных кандидатов (StoredDocument), без загрузки сегментов.
/// </summary>
public static class AttachmentQueryRouter
{
    /// <summary>Минимальная длина стема имени файла для совпадения в вопросе.</summary>
    private const int MinStemLength = 3;

    /// <summary>
    /// <paramref name="candidates"/> — документы встречи (могут включать
    /// удалённые/пустые — отфильтровываются). Пустой вопрос → General.
    /// </summary>
    public static AttachmentQuery Route(string? question, IReadOnlyList<StoredDocument> candidates)
    {
        if (string.IsNullOrWhiteSpace(question))
            return AttachmentQuery.General;

        var usable = candidates.Where(d => d is { Deleted: false, CharCount: > 0 }).ToList();
        if (usable.Count == 0)
            return AttachmentQuery.General;

        var matched = MatchDocuments(question, usable);
        if (matched.Count > 1)
            return new AttachmentQuery(AttachmentQueryKind.Clarify, null, [], matched, []);
        var doc = matched.Count == 1 ? matched[0] : null;

        // Страничный адрес — самый сильный сигнал скоупа; он опережает
        // whole-doc слова («перескажи страницу 5» — страничный вопрос).
        // «Конец документа»/«последние N страниц» — тот же адрес, но номер
        // вычисляется от реального числа страниц (§4: конец документа).
        var tailMatch = ParseTailSpec(question);
        // Имя файла может содержать число («Подготовка (2).pdf»): это не адрес
        // страницы. Маскируем точное имя до разбора адресов; длина сохраняется,
        // поэтому индексы tailMatch остаются валидными.
        var pageText = doc is null ? question : MaskDocumentName(question, doc);
        // Число внутри «последних N страниц» — не адрес страницы N: фразу
        // вырезаем до разбора адресов, иначе «последние 10» читалось бы как стр. 10.
        var pages = ParsePageSpec(tailMatch == null ? pageText
            : string.Concat(pageText.AsSpan(0, tailMatch.Index),
                            pageText.AsSpan(tailMatch.Index + tailMatch.Length)));
        if (tailMatch != null)
            doc ??= usable.Count == 1 ? usable[0] : null;
        if (tailMatch != null && doc != null)
        {
            // Граница — реальное число страниц файла (PageStats.Total), а не
            // число извлечённых текстовых якорей: у частичного документа
            // PageCount занижен, запрос «стр. 87» не должен врать «нет страницы».
            var totalPages = TotalPages(doc);
            if (totalPages > 0)
            {
                var n = Math.Min(Math.Max(TailCount(tailMatch), 1), totalPages);
                pages.Add(new PageInterval(totalPages - n + 1, totalPages));
                pages = Merge(pages); // хвост может пересекаться с явным диапазоном
            }
        }
        if (pages.Count > 0)
        {
            doc ??= usable.Count == 1 ? usable[0] : null;
            if (doc == null)
                return new AttachmentQuery(AttachmentQueryKind.Clarify, null, [], usable, []);

            var totalPages = TotalPages(doc);
            var missing = new List<int>();
            var ok = new List<PageInterval>();
            if (totalPages > 0)
            {
                foreach (var r in pages)
                {
                    if (r.From > totalPages) { for (var p = r.From; p <= r.To; p++) missing.Add(p); continue; }
                    if (r.To > totalPages)
                        for (var p = totalPages + 1; p <= r.To; p++) missing.Add(p);
                    ok.Add(new PageInterval(r.From, Math.Min(r.To, totalPages)));
                }
            }
            else ok.AddRange(pages);
            // дедуп пропущенных номеров для честной заметки
            missing = missing.Distinct().OrderBy(p => p).ToList();
            return new AttachmentQuery(AttachmentQueryKind.Pages, doc, ok, [], missing);
        }

        // «Последние страницы»/«конец» к нестраничному документу (нет метаданных
        // страниц) — адресовать некуда: вопрос явно про документ → whole-doc.
        if (tailMatch != null)
        {
            doc ??= usable.Count == 1 ? usable[0] : null;
            if (doc == null)
                return new AttachmentQuery(AttachmentQueryKind.Clarify, null, [], usable, []);
            return new AttachmentQuery(AttachmentQueryKind.WholeDocument, doc, [], [], []);
        }

        var genericDocRef = ReferencesDocument(question);
        if (IsWholeDocument(question) || (genericDocRef && doc == null))
        {
            doc ??= usable.Count == 1 ? usable[0] : null;
            if (doc == null)
                return new AttachmentQuery(AttachmentQueryKind.Clarify, null, [], usable, []);
            return new AttachmentQuery(AttachmentQueryKind.WholeDocument, doc, [], [], []);
        }
        if (genericDocRef && doc != null)
            return new AttachmentQuery(AttachmentQueryKind.WholeDocument, doc, [], [], []);

        // Явное имя без страничного/whole-doc намерения — приоритет в сборке.
        return new AttachmentQuery(AttachmentQueryKind.General, doc, [], [], []);
    }

    // ---------- имя документа ----------

    /// <summary>
    /// Заменяет вхождения имени файла и стема пробелами, сохраняя длину:
    /// числа внутри имени («(2)», «ICB 4_0») не должны читаться как адреса
    /// страниц. Регистр не важен.
    /// </summary>
    private static string MaskDocumentName(string question, StoredDocument doc)
    {
        var masked = Regex.Replace(question, Regex.Escape(doc.DisplayName),
            m => new string(' ', m.Length), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var stem = Path.GetFileNameWithoutExtension(doc.DisplayName);
        if (stem.Length >= MinStemLength)
            masked = Regex.Replace(masked, Regex.Escape(stem),
                m => new string(' ', m.Length), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return masked;
    }

    /// <summary>Явные ссылки на файлы: имя с расширением или стем (≥3 символов,
    /// с границей слова — «текст» не сматчит «контекст»).</summary>
    private static List<StoredDocument> MatchDocuments(string question, List<StoredDocument> usable)
    {
        var q = question.ToLowerInvariant();
        var qWords = Words(q);
        var hits = new List<StoredDocument>();
        var inflected = new List<StoredDocument>();
        foreach (var doc in usable)
        {
            var name = doc.DisplayName.ToLowerInvariant();
            if (name.Length >= MinStemLength && ContainsBounded(q, name))
            {
                hits.Add(doc);
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length >= MinStemLength && !string.Equals(stem, name, StringComparison.Ordinal)
                && ContainsBounded(q, stem))
            {
                hits.Add(doc);
                continue;
            }
            // Имена файлов часто стоят в вопросе в косвенном падеже:
            // «100 страницу стандарта» ↔ «Стандарт ICB 4_0». Точное вхождение
            // стема такое не ловит; сравниваем отдельные слова имени с
            // окончанием до 3 букв, но только по содержательным токенам.
            if (MatchesInflectedNameToken(stem, qWords))
                inflected.Add(doc);
        }
        // Точное имя сильнее падежного совпадения: если пользователь назвал
        // файл, не размываем выбор другим документом с общим словом.
        return hits.Count > 0 ? hits : inflected;
    }

    /// <summary>Слова названия без служебных/общих основ, которые встречаются
    /// почти в любом вопросе («проект», «управление», «контекст», «файл»).</summary>
    private static readonly string[] GenericNameStems =
    [
        "проект", "управлен", "план", "пример", "подготовк", "требован",
        "контекст", "таблиц", "документ", "файл", "текст",
    ];

    private static bool MatchesInflectedNameToken(string stem, IReadOnlyList<string> questionWords)
    {
        foreach (var token in Words(stem))
        {
            if (token.Length < MinStemLength) continue;
            if (GenericNameStems.Any(g => token.StartsWith(g, StringComparison.Ordinal)))
                continue;
            foreach (var qw in questionWords)
            {
                if (qw.Length < MinStemLength) continue;
                if (string.Equals(qw, token, StringComparison.Ordinal))
                    return true;
                // «стандарта» → «стандарт», «компетентности» → «компетентность».
                // Максимум 3 буквы окончания: «проектами» не должно тянуть
                // файл «проект», даже если бы общая основа не была отсеяна.
                if (qw.Length > token.Length && qw.StartsWith(token, StringComparison.Ordinal)
                    && qw.Length - token.Length <= 3)
                    return true;
            }
        }
        return false;
    }

    private static string[] Words(string text) =>
        Regex.Split(text, @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 0)
            .Select(w => w.ToLowerInvariant())
            .ToArray();

    private static bool ContainsBounded(string haystack, string needle)
    {
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            var before = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            var afterIdx = i + needle.Length;
            var after = afterIdx >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIdx]);
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>Родовое упоминание документа без имени («в документе», «the file»).</summary>
    private static bool ReferencesDocument(string question) =>
        Regex.IsMatch(question,
            @"\b(документ\w*|файл\w*|вложени\w*|приложени\w*|отч[её]т\w*|презентаци\w*|книг\w*|тексте|pdf|document\w*|file\w*|attachment\w*|report)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Намерение «по документу целиком»: пересказ/сводка/обзор.</summary>
    private static bool IsWholeDocument(string question) =>
        Regex.IsMatch(question,
            @"\b(целиком|полностью|краткое\s+содержание|содержание\s+документа|перескаж\w*|изложи\w*|основные\s+тезисы|summari[sz]e\w*|summary|whole\s+document|entire\s+document|overview|digest)\b|о\s+ч[её]м\s+(?:этот\s+)?(?:документ|файл|книга|отч[её]т)|что\s+в\s+(?:этом\s+)?(?:документе|файле|книге|отч[её]те)|весь\s+(?:документ|файл|текст|отч[её]т|книга)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---------- адреса страниц ----------

    /// <summary>
    /// Номера/диапазоны — адреса страниц PDF 1-based (конвенция
    /// <see cref="AttachmentContextBuilder"/>): голые числа — адреса,
    /// «5-8», «3 по 6», «pages 2–4» сливаются и дедуплицируются. Числа,
    /// встроенные в идентификаторы, соседние с «.»/«,» и цифрой (десятичные
    /// «2.31», разряды «1,000»), адресами НЕ считаются — цена: компактный список
    /// «5,7» без пробела тоже не читается (список с пробелом «5, 7» — читается).
    /// Число N внутри «последние N страниц»/«last N pages» не является адресом
    /// страницы N.
    /// </summary>
    public static List<PageInterval> ParsePageSpec(string? question)
    {
        var list = new List<PageInterval>();
        if (string.IsNullOrWhiteSpace(question)) return list;
        var tail = ParseTailSpec(question);
        foreach (Match m in Regex.Matches(question,
                     @"(?<![\p{L}\d])(?<!\d[.,])(?<start>\d{1,6})(?:\s*(?:[-–—]|\b(?:по|до|to)\b)\s*(?<end>\d{1,6}))?(?![\p{L}\d])(?![.,]\d)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            // В «последние N страниц» число N — размер хвоста, не адрес
            // страницы N. Пропускаем только совпадение внутри tail-фразы;
            // отдельные адреса в том же вопросе сохраняются.
            if (tail is not null && m.Index < tail.Index + tail.Length
                && m.Index + m.Length > tail.Index)
                continue;
            if (!int.TryParse(m.Groups["start"].Value, out var from) || from < 1)
                continue;
            var to = from;
            if (m.Groups["end"].Success && int.TryParse(m.Groups["end"].Value, out var parsed))
                to = parsed;
            if (to < 1) continue;
            list.Add(from <= to ? new PageInterval(from, to) : new PageInterval(to, from));
        }
        return Merge(list);
    }

    /// <summary>
    /// Адрес «с конца документа»: «последняя/последние [N] страниц(а/ы)»,
    /// «конец документа», «the end», «last [N] page(s)». Возвращает match
    /// фразы; число страниц извлекает <see cref="TailCount"/>.
    /// </summary>
    private static Match? ParseTailSpec(string question)
    {
        var m = Regex.Match(question,
            @"\bпоследн\w*\s*(?:\d{1,4}\s*)?страниц\w*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success) return m;
        var m2 = Regex.Match(question,
            @"\blast\s+(?:\d{1,4}\s+)?pages?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m2.Success) return m2;
        var m3 = Regex.Match(question,
            @"\bконц\w*\s+(?:документа|файла|книги|отч[её]та|текста)\b|\bthe\s+end\b|\bend\s+of\s+(?:the\s+)?(?:document|file|book|report)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m3.Success ? m3 : null;
    }

    /// <summary>
    /// Сколько страниц от конца просит фраза: явное N из «последние N страниц»;
    /// единственное число («последняя/последней странице», «last page») — 1;
    /// без числа («последние страницы», «конец документа») — 2
    /// (заключение редко умещается на одной странице).
    /// </summary>
    private static int TailCount(Match m)
    {
        var num = Regex.Match(m.Value, @"\d{1,4}");
        if (num.Success && int.TryParse(num.Value, out var n)) return n;
        var v = m.Value;
        var plural = v.Contains("последни", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(v, @"\blast\s+pages\b", RegexOptions.IgnoreCase);
        var endPhrase = v.Contains("конц", StringComparison.OrdinalIgnoreCase)
            || v.Contains("end", StringComparison.OrdinalIgnoreCase);
        return plural || endPhrase ? 2 : 1;
    }

    /// <summary>Число страниц для адресации: метрика PDF и PageCount не должны
    /// занижать друг друга у частичных/унаследованных записей.</summary>
    private static int TotalPages(StoredDocument doc)
    {
        var statsTotal = doc.PageStats is { Total: > 0 } stats ? stats.Total : 0;
        return Math.Max(statsTotal, doc.PageCount);
    }

    /// <summary>Пересекающиеся/соседние диапазоны сливаются в минимальный набор.</summary>
    internal static List<PageInterval> Merge(List<PageInterval> ranges)
    {
        if (ranges.Count < 2) return ranges;
        var sorted = ranges.OrderBy(r => r.From).ThenBy(r => r.To).ToList();
        var merged = new List<PageInterval> { sorted[0] };
        foreach (var r in sorted.Skip(1))
        {
            var last = merged[^1];
            if (r.From <= last.To + 1)
                merged[^1] = new PageInterval(last.From, Math.Max(last.To, r.To));
            else
                merged.Add(r);
        }
        return merged;
    }
}
