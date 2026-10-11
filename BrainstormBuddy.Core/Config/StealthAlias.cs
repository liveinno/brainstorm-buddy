namespace BrainstormBuddy.Config;

/// <summary>
/// Валидация/нормализация alias'а имени процесса для стелс-режима
/// (AppConfig.Ui.StealthProcessName). Alias — это БАЗОВОЕ имя exe без расширения:
/// «FocusCoach» → запускается копия stealth\FocusCoach.exe, и Диспетчер задач
/// показывает колонку «Имя» = FocusCoach (имя файла образа), а не BrainstormBuddy.
/// </summary>
public static class StealthAlias
{
    /// <summary>Макс. длина alias (имя файла без расширения). WIN32 MAX_PATH-бюджет для путей копии.</summary>
    public const int MaxLength = 60;

    // Имена устройств DOS — недопустимы как базовое имя файла (NtCreateFile отклонит).
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Быстрая проверка «поле пустое или содержит корректный alias» — для UI.</summary>
    public static bool IsValid(string? raw) => TryNormalize(raw, out _, out _);

    /// <summary>
    /// Нормализация ввода пользователя: trim, снятие необязательного суффикса «.exe»,
    /// проверка символов имени файла. Пустой ввод = стелс выключен (валидно, alias="").
    /// </summary>
    /// <returns>true, если ввод допустим; alias = нормализованное базовое имя ("" при выключенном режиме).</returns>
    public static bool TryNormalize(string? raw, out string alias, out string error)
    {
        alias = string.Empty;
        error = string.Empty;

        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0)
            return true; // стелс выключен — пустое значение легально

        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            s = s[..^4].Trim();

        if (s.Length == 0)
        {
            error = "Пустое имя процесса (одно «.exe» — не alias).";
            return false;
        }
        if (s.Length > MaxLength)
        {
            error = $"Имя слишком длинное (максимум {MaxLength} символов).";
            return false;
        }
        if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Имя содержит недопустимые для файла символы (\\/:*?\"<>| и служебные).";
            return false;
        }
        // Точки/пробелы на краях: Windows тихо обрезает их → фактическое имя exe
        // отличается от ожидаемого и guard по имени не сработает.
        if (s != s.TrimEnd('.', ' ') || s.StartsWith('.'))
        {
            error = "Имя не должно начинаться с точки и заканчиваться точкой/пробелом.";
            return false;
        }
        if (ReservedNames.Contains(s))
        {
            error = $"«{s}» — зарезервированное имя устройства Windows.";
            return false;
        }

        alias = s;
        return true;
    }
}
