using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Services;

/// <summary>
/// Собирает обезличенный архив для поддержки: логи + настройки без токенов + сведения
/// о системе + недавние ошибки. НАМЕРЕННО НЕ включает историю вопросов/ответов
/// (qa_history.txt) и транскрибации — это персональные данные, остаются на машине юзера.
/// </summary>
public static class DiagnosticsService
{
    /// <summary>
    /// Создаёт zip-бандл в <paramref name="destDir"/> и возвращает путь к нему.
    /// </summary>
    /// <param name="destDir">Куда положить архив (обычно Документы\BrainstormBuddy).</param>
    /// <param name="appDataDir">%APPDATA%\BrainstormBuddy — где лежат logs\ и config.json.</param>
    /// <param name="configPath">Путь к config.json.</param>
    /// <param name="extraSystemInfo">Доп. сведения от приложения (движки, версии, эндпоинты).</param>
    public static string CreateSupportBundle(string destDir, string appDataDir, string configPath, string? extraSystemInfo = null)
    {
        Directory.CreateDirectory(destDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var staging = Path.Combine(Path.GetTempPath(), $"bb_diag_{stamp}");
        Directory.CreateDirectory(staging);
        var manifest = new StringBuilder();
        manifest.AppendLine("BrainstormBuddy — архив для поддержки");
        manifest.AppendLine($"Собран: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        manifest.AppendLine();
        manifest.AppendLine("ЧТО ВНУТРИ (всё обезличено):");

        try
        {
            // 1) Сведения о системе
            try
            {
                File.WriteAllText(Path.Combine(staging, "system_info.txt"), BuildSystemInfo(extraSystemInfo), Encoding.UTF8);
                manifest.AppendLine("  • system_info.txt — ОС, .NET, CPU, активные движки, версии");
            }
            catch (Exception ex) { manifest.AppendLine($"  • system_info.txt — ПРОПУЩЕНО ({ex.Message})"); }

            // 2) Настройки без токенов/ключей
            try
            {
                if (File.Exists(configPath))
                {
                    var redacted = RedactConfigJson(File.ReadAllText(configPath));
                    File.WriteAllText(Path.Combine(staging, "config.redacted.json"), redacted, Encoding.UTF8);
                    manifest.AppendLine("  • config.redacted.json — настройки, ключи/токены заменены на ***");
                }
            }
            catch (Exception ex) { manifest.AppendLine($"  • config.redacted.json — ПРОПУЩЕНО ({ex.Message})"); }

            // 3) Лог-файлы (обезличенные) + сводка ошибок
            var errorLines = new List<string>();
            try
            {
                var logDir = Path.Combine(appDataDir, "logs");
                int copied = 0;
                if (Directory.Exists(logDir))
                {
                    foreach (var path in Directory.GetFiles(logDir, "app*.log"))
                    {
                        var raw = SafeRead(path);
                        var clean = RedactLogText(raw);
                        File.WriteAllText(Path.Combine(staging, Path.GetFileName(path)), clean, Encoding.UTF8);
                        copied++;
                        foreach (var l in clean.Split('\n'))
                            if (l.Contains("[Error]") || l.Contains("[Warn ]")) errorLines.Add(l.TrimEnd('\r'));
                    }
                }
                manifest.AppendLine($"  • app*.log — {copied} файл(ов) логов, речь/ответы вырезаны");

                if (errorLines.Count > 0)
                {
                    var tail = errorLines.Count > 400 ? errorLines.GetRange(errorLines.Count - 400, 400) : errorLines;
                    File.WriteAllText(Path.Combine(staging, "errors.txt"), string.Join(Environment.NewLine, tail), Encoding.UTF8);
                    manifest.AppendLine($"  • errors.txt — {tail.Count} строк(и) предупреждений/ошибок");
                }
            }
            catch (Exception ex) { manifest.AppendLine($"  • app*.log — ПРОПУЩЕНО ({ex.Message})"); }

            manifest.AppendLine();
            manifest.AppendLine("ЧЕГО ЗДЕСЬ НЕТ (остаётся только на вашем компьютере):");
            manifest.AppendLine("  • История вопросов/ответов (qa_history.txt)");
            manifest.AppendLine("  • Транскрибации файлов и записи звонков");
            manifest.AppendLine("  • API-ключи, токены доступа, пароли");
            File.WriteAllText(Path.Combine(staging, "README.txt"), manifest.ToString(), Encoding.UTF8);

            // 4) Упаковка
            var zipPath = Path.Combine(destDir, $"diagnostics_{stamp}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* временная папка — не критично */ }
        }
    }

    private static string SafeRead(string path)
    {
        try
        {
            // Файл может быть открыт логгером на запись — читаем с общим доступом.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch { return string.Empty; }
    }

    private static string BuildSystemInfo(string? extra)
    {
        var sb = new StringBuilder();
        var asm = typeof(DiagnosticsService).Assembly.GetName();
        sb.AppendLine($"Приложение: {asm.Name} {asm.Version}");
        sb.AppendLine($"ОС: {Environment.OSVersion} (64-bit: {Environment.Is64BitOperatingSystem})");
        sb.AppendLine($".NET (CLR): {Environment.Version}");
        sb.AppendLine($"Процесс 64-bit: {Environment.Is64BitProcess}");
        sb.AppendLine($"Ядер CPU: {Environment.ProcessorCount}");
        sb.AppendLine($"Культура: {CultureInfo.CurrentCulture.Name}");
        try
        {
            var gc = GC.GetGCMemoryInfo();
            sb.AppendLine($"Доступно памяти (прибл.): {gc.TotalAvailableMemoryBytes / (1024 * 1024)} МБ");
        }
        catch { /* не критично */ }
        // Намеренно НЕ включаем имя машины и имя пользователя Windows (персональные данные).
        if (!string.IsNullOrWhiteSpace(extra))
        {
            sb.AppendLine();
            sb.AppendLine("— Состояние приложения —");
            sb.AppendLine(RedactLogText(extra));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Убирает из текста логов персональные данные: распознанную речь и ответы ассистента,
    /// имя пользователя Windows, e-mail, а также любые случайно попавшие токены/ключи.
    /// </summary>
    public static string RedactLogText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Имя пользователя в путях C:\Users\<имя>\...
        text = Regex.Replace(text, @"([A-Za-z]:\\Users\\)[^\\\r\n""'<>|]+", "$1<user>");

        // E-mail
        text = Regex.Replace(text, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", "<email>");

        // Секреты
        text = Regex.Replace(text, @"glpat-[A-Za-z0-9_.\-]+", "glpat-***");
        text = Regex.Replace(text, @"\bsk-[A-Za-z0-9]{8,}", "sk-***");
        text = Regex.Replace(text, @"(?i)\bBearer\s+[A-Za-z0-9._\-]+", "Bearer ***");
        text = Regex.Replace(text, @"(?i)(PRIVATE-TOKEN|Authorization|api[_-]?key)(""?\s*[:=]\s*""?)[^\s""',}]+", "$1$2***");

        // Явные места, где приложение пишет распознанную речь / ответ модели
        text = Regex.Replace(text, @"(Ask done in [0-9.]+ms: )'[^']*'", "$1'<redacted>'");
        text = Regex.Replace(text, @"(expand answer for )'[^']*'", "$1'<redacted>'");
        text = Regex.Replace(text, @"(STT response received: )'[^']*'", "$1'<redacted>'");

        // Эвристика: длинная фраза в кавычках с пробелом — почти наверняка транскрипт/ответ.
        text = Regex.Replace(text, @"'([^'\r\n]{25,})'", m => m.Groups[1].Value.Contains(' ') ? "'<redacted>'" : m.Value);

        return text;
    }

    /// <summary>
    /// Возвращает config.json, где значения ключей/токенов заменены на «***»,
    /// а имена пользователей/почта в остальных строковых полях обезличены.
    /// </summary>
    public static string RedactConfigJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            RedactNode(node);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
        }
        catch
        {
            // Не распарсили — на всякий случай прогоняем как обычный текст.
            return RedactLogText(json);
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    obj[key] = LooksSecretKey(key)
                        ? (string.IsNullOrEmpty(s) ? s : "***")
                        : RedactLogText(s);
                }
                else RedactNode(child);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) RedactNode(item);
        }
    }

    private static bool LooksSecretKey(string key) =>
        Regex.IsMatch(key, @"(?i)(apikey|authvalue|token|secret|password)");
}
