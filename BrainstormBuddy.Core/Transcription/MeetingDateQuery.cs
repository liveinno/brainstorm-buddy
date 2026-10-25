using System.Globalization;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Transcription;

/// <summary>
/// Разбор даты из вопроса/аргумента инструмента поиска встреч. Вопросы вида
/// «что обсуждали вчера» или «22 октября» должны становиться ФИЛЬТРОМ по дате
/// встречи, а не поиском слова «вчера» внутри транскрипта.
/// </summary>
public static class MeetingDateQuery
{
    private static readonly string[] RelativeToday = { "сегодня", "today" };
    private static readonly string[] RelativeYesterday = { "вчера", "yesterday" };
    private static readonly string[] RelativeDayBefore = { "позавчера" };

    /// <summary>Русские месяцы во всех падежных формах. Список, а не префикс:
    /// префикс «ма» превращал «машина» в май.</summary>
    private static readonly Dictionary<string, int> RussianMonths = new(StringComparer.Ordinal)
    {
        ["январь"] = 1, ["января"] = 1, ["январе"] = 1, ["январ"] = 1,
        ["февраль"] = 2, ["февраля"] = 2, ["феврале"] = 2, ["феврал"] = 2,
        ["март"] = 3, ["марта"] = 3, ["марте"] = 3,
        ["апрель"] = 4, ["апреля"] = 4, ["апреле"] = 4, ["апрел"] = 4,
        ["май"] = 5, ["мая"] = 5, ["мае"] = 5,
        ["июнь"] = 6, ["июня"] = 6, ["июне"] = 6, ["июн"] = 6,
        ["июль"] = 7, ["июля"] = 7, ["июле"] = 7, ["июл"] = 7,
        ["август"] = 8, ["августа"] = 8, ["августе"] = 8,
        ["сентябрь"] = 9, ["сентября"] = 9, ["сентябре"] = 9, ["сентябр"] = 9,
        ["октябрь"] = 10, ["октября"] = 10, ["октябре"] = 10, ["октябр"] = 10,
        ["ноябрь"] = 11, ["ноября"] = 11, ["ноябре"] = 11, ["ноябр"] = 11,
        ["декабрь"] = 12, ["декабря"] = 12, ["декабре"] = 12, ["декабр"] = 12,
    };

    /// <summary>
    /// Диапазон дат в свободном вопросе: «20-22.10.2026», «с 20 по 22 октября»,
    /// «20 октября - 22 октября 2025». Месяц словом допускается с любой
    /// стороны; разделитель «по/до/и» — только со своими пробелами, иначе
    /// «или» съедается как «и».
    /// </summary>
    private const string RangePattern =
        @"(?<![\p{L}\d])(?:с\s+)?(?<d1>\d{1,2})(?:[./](?<m1>\d{1,2}))?(?:[./](?<y1>\d{2,4}))?" +
        @"(?:\s+(?<mon1>(?!(?:по|до|и)\b)[\p{L}]+))?" +
        @"(?:\s+(?:по|до|и)\s*|\s*[-–—]\s*)" +
        @"(?<d2>\d{1,2})(?:[./](?<m2>\d{1,2}))?(?:[./](?<y2>\d{2,4}))?" +
        @"(?:\s+(?<mon2>(?!(?:по|до|и)\b)[\p{L}]+))?" +
        @"(?:\s+(?<y>\d{4}))?(?:\s+(?:года|году|г\.))?(?![\p{L}\d])";

    /// <summary>Разобрать дату: ISO, dd.MM[.yyyy], «22 октября [2026]»,
    /// «сегодня/вчера/позавчера». Непонятная строка → false.</summary>
    public static bool TryParse(string? raw, DateOnly today, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().ToLowerInvariant();

        if (RelativeToday.Contains(s)) { date = today; return true; }
        if (RelativeYesterday.Contains(s)) { date = today.AddDays(-1); return true; }
        if (RelativeDayBefore.Contains(s)) { date = today.AddDays(-2); return true; }

        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
            return true;

        // dd.MM без года разбираем сами: DateTime.TryParseExact подставляет
        // год 1, и 29.02 падает ещё до пересчёта на текущий год.
        var dotParts = s.Split('.');
        if (dotParts.Length == 2 &&
            int.TryParse(dotParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var shortDay) &&
            int.TryParse(dotParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var shortMonth) &&
            shortMonth is >= 1 and <= 12 && shortDay is >= 1 and <= 31)
        {
            try
            {
                date = new DateOnly(today.Year, shortMonth, shortDay);
                if (date > today.AddDays(7)) date = date.AddYears(-1);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        if (DateTime.TryParseExact(s,
                new[] { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var numeric))
        {
            date = DateOnly.FromDateTime(numeric);
            return true;
        }

        var monthMatch = Regex.Match(s, @"(?<!\d)(\d{1,2})\s+([\p{L}]+)(?:\s+(\d{4}))?(?!\d)",
            RegexOptions.CultureInvariant);
        if (monthMatch.Success &&
            int.TryParse(monthMatch.Groups[1].Value, out var day) &&
            TryRussianMonth(monthMatch.Groups[2].Value, out var month))
        {
            var year = monthMatch.Groups[3].Success
                ? int.Parse(monthMatch.Groups[3].Value, CultureInfo.InvariantCulture)
                : today.Year;
            if (!monthMatch.Groups[3].Success)
            {
                try
                {
                    var candidate = new DateOnly(year, month, day);
                    if (candidate > today.AddDays(7)) candidate = candidate.AddYears(-1);
                    date = candidate;
                    return true;
                }
                catch (ArgumentOutOfRangeException) { return false; }
            }
            try
            {
                date = new DateOnly(year, month, day);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        // Английские месяцы (модель иногда переводит дату): 22 October 2026.
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("ru-RU"), CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParse(s, culture, DateTimeStyles.None, out var parsed))
            {
                date = DateOnly.FromDateTime(parsed);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Найти дату или диапазон в свободном вопросе. Понимает «22 октября»,
    /// «вчера», «с 20 по 22 октября», «20–22.10.2026». Диапазон становится
    /// фильтром DateFrom/DateTo, а не словом внутри транскрипта.
    /// </summary>
    public static bool TryExtractPeriod(string query, DateOnly today,
        out DateOnly from, out DateOnly to, out string cleanedQuery)
    {
        from = to = default;
        cleanedQuery = query;
        if (string.IsNullOrWhiteSpace(query)) return false;

        var range = Regex.Match(query, RangePattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (range.Success && TryResolveRange(range.Groups, today, out from, out to))
        {
            if (from > to) (from, to) = (to, from);
            cleanedQuery = (query[..range.Index] + " " +
                            query[(range.Index + range.Length)..]).Trim();
            return true;
        }

        if (TryExtract(query, today, out var single, out var cleaned))
        {
            from = to = single;
            cleanedQuery = cleaned;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Диапазон из вопроса. Обе стороны делят общий контекст: месяц и год
    /// одной стороны наследует другая («20-22.10.2026» = 20..22 октября 2026,
    /// «20.10-22.10.2025» = 20..22 октября 2025). Без явного года берётся
    /// ближайший к сегодня вариант — с тем же правилом «сильно будущая дата —
    /// прошлый год», что у одиночной даты.
    /// </summary>
    private static bool TryResolveRange(GroupCollection g, DateOnly today,
        out DateOnly from, out DateOnly to)
    {
        from = to = default;
        if (!int.TryParse(g["d1"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day1) ||
            !int.TryParse(g["d2"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day2))
            return false;
        if (day1 is < 1 or > 31 || day2 is < 1 or > 31) return false;

        // Месяц: свой числовой → свой словом → числовой другой стороны → словом.
        var num1 = ParseMonth(g["m1"].Value);
        var num2 = ParseMonth(g["m2"].Value);
        var word1 = ParseMonthWord(g["mon1"].Value);
        var word2 = ParseMonthWord(g["mon2"].Value);
        var mon1 = num1 ?? word1 ?? num2 ?? word2;
        var mon2 = num2 ?? word2 ?? num1 ?? word1;
        if (mon1 is null || mon2 is null) return false;

        var ex1 = ParseYear(g["y1"].Value);
        var ex2 = ParseYear(g["y2"].Value);
        var common = ParseYear(g["y"].Value);

        if (ex1 is null && ex2 is null && common is null)
        {
            var best = int.MaxValue;
            foreach (var y in new[] { today.Year - 1, today.Year, today.Year + 1 })
            {
                if (!TryCompose(day1, mon1.Value, y, out var f)) continue;
                if (!TryCompose(day2, mon2.Value, mon2.Value < mon1.Value ? y + 1 : y, out var t))
                    continue;
                var dist = DistanceDays(today, f, t);
                if (dist >= best) continue;
                best = dist;
                from = f;
                to = t;
            }
            if (best == int.MaxValue) return false;
            if (from > today.AddDays(7))
            {
                from = from.AddYears(-1);
                to = to.AddYears(-1);
            }
            return true;
        }

        int y1, y2;
        if (ex1 is not null && ex2 is not null)
        {
            y1 = ex1.Value;
            y2 = ex2.Value;
        }
        else if (common is not null)
        {
            y1 = common.Value;
            y2 = common.Value;
            if (mon2.Value < mon1.Value) y2 = y1 + 1;
        }
        else if (ex1 is not null)
        {
            y1 = ex1.Value;
            y2 = y1;
            if (mon2.Value < mon1.Value) y2 = y1 + 1;
        }
        else
        {
            // Год указан один раз - у конца диапазона; начало наследует его,
            // а при переходе через новый год откатывается на год назад
            // («28.12-05.01.2026» = 28.12.2025..05.01.2026 - прошлые встречи).
            y2 = ex2!.Value;
            y1 = y2;
            if (mon2.Value < mon1.Value) y1 = y2 - 1;
        }
        if (!TryCompose(day1, mon1.Value, y1, out from)) return false;
        if (!TryCompose(day2, mon2.Value, y2, out to)) return false;
        return true;
    }

    private static bool TryCompose(int day, int month, int year, out DateOnly date)
    {
        date = default;
        if (month is < 1 or > 12 || year is < 1900 or > 2200 || day < 1) return false;
        // «31 февраля» — человек ошибся; берём последний день месяца.
        var max = DateTime.DaysInMonth(year, month);
        date = new DateOnly(year, month, Math.Min(day, max));
        return true;
    }

    private static int DistanceDays(DateOnly today, DateOnly from, DateOnly to)
    {
        if (today >= from && today <= to) return 0;
        var toStart = from.DayNumber - today.DayNumber;
        var toEnd = today.DayNumber - to.DayNumber;
        return Math.Abs(Math.Min(toStart, toEnd));
    }

    private static int? ParseMonth(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return null;
        return m is >= 1 and <= 12 ? m : null;
    }

    private static int? ParseMonthWord(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return TryRussianMonth(raw, out var m) ? m : null;
    }

    private static int? ParseYear(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var y)) return null;
        if (raw.Length == 4) return y is >= 1900 and <= 2200 ? y : null;
        if (raw.Length == 2) return y < 50 ? 2000 + y : 1900 + y;
        return null;
    }

    /// <summary>Найти дату в свободном вопросе. <paramref name="cleanedQuery"/>
    /// не содержит найденного выражения — по нему ищутся смысловые термины,
    /// а дата применяется отдельным фильтром.</summary>
    public static bool TryExtract(string query, DateOnly today, out DateOnly date,
        out string cleanedQuery)
    {
        date = default;
        cleanedQuery = query;
        if (string.IsNullOrWhiteSpace(query)) return false;

        var patterns = new[]
        {
            @"(?<![\p{L}\d])(сегодня|вчера|позавчера|today|yesterday)(?![\p{L}\d])",
            @"(?<!\d)\d{4}-\d{2}-\d{2}(?!\d)",
            @"(?<!\d)\d{1,2}[./]\d{1,2}(?:[./]\d{2,4})?(?!\d)",
            @"(?<!\d)\d{1,2}\s+[\p{L}]+(?:\s+\d{4})?(?!\d)",
        };
        foreach (var pattern in patterns)
        {
            var m = Regex.Match(query, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success) continue;
            if (!TryParse(m.Value, today, out date)) continue;
            cleanedQuery = (query[..m.Index] + " " + query[(m.Index + m.Length)..]).Trim();
            return true;
        }
        return false;
    }

    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryRussianMonth(string word, out int month)
        => RussianMonths.TryGetValue(word.ToLowerInvariant(), out month);
}
