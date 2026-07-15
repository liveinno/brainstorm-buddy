using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BrainstormBuddy.UITests.Reporting;

public class HtmlReportBuilder
{
    private readonly List<string> _steps = new();
    private bool _allProvidersFailed = false;
    private string _allProvidersFailedReason = "";

    public void MarkAllProvidersFailed(string reason)
    {
        _allProvidersFailed = true;
        _allProvidersFailedReason = reason;
    }

    public bool HasCriticalFailure => _allProvidersFailed;

    // «ОШИБКА X не обнаружено / нет / not detected» — это ВЕРДИКТ ОБ ОТСУТСТВИИ ошибки,
    // а не ошибка. Наивный Contains красил такие шаги как FAILED (см. report 2026-07-04).
    private static bool MentionsRealError(string feedback, string marker)
    {
        var idx = feedback.IndexOf(marker, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var tail = feedback.Substring(idx + marker.Length, Math.Min(160, feedback.Length - idx - marker.Length));
            var tailLower = tail.ToLowerInvariant();
            var negated = tailLower.TrimStart(':', ' ', '-').StartsWith("не ") ||
                          tailLower.Contains("не обнаруж") || tailLower.Contains("нет") ||
                          tailLower.Contains("not detected") || tailLower.Contains("отсутству");
            if (!negated && marker == "ОШИБКА КОДИРОВКИ" && !QuoteLooksBroken(tail))
                negated = true; // модель процитировала НОРМАЛЬНЫЙ текст → галлюцинация 3b, не ошибка
            if (!negated) return true;
            idx = feedback.IndexOf(marker, idx + marker.Length, StringComparison.Ordinal);
        }
        return false;
    }

    // Промпт требует ДОСЛОВНУЮ цитату битого текста. Проверяем цитату кодом:
    // если она состоит из обычной кириллицы/латиницы/цифр/пунктуации — каракулей нет,
    // и «ошибка кодировки» выдумана (qwen2.5vl:3b хронически цитирует нормальные строки).
    private static bool QuoteLooksBroken(string tail)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tail, "[\"'«`“‘]([^\"'»`”’]{1,120})[\"'»`”’]");
        var quote = m.Success ? m.Groups[1].Value : tail.TrimStart(':', ' ').Split('\n')[0];
        if (string.IsNullOrWhiteSpace(quote)) return false;
        int weird = 0, total = 0;
        foreach (var ch in quote)
        {
            if (char.IsWhiteSpace(ch)) continue;
            total++;
            bool normal = (ch >= 'а' && ch <= 'я') || (ch >= 'А' && ch <= 'Я') || ch == 'ё' || ch == 'Ё' ||
                          (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || char.IsDigit(ch) ||
                          char.IsPunctuation(ch) || char.IsSymbol(ch) && "+-=/\\*%№<>".IndexOf(ch) >= 0;
            if (!normal) weird++;
        }
        return total > 0 && weird * 100 / total >= 20; // ≥20% странных символов = похоже на каракули
    }

    public void AddStep(string title, string base64Image, string aiFeedback)
    {
        var mojibake = MentionsRealError(aiFeedback, "ОШИБКА КОДИРОВКИ");
        var contrast = MentionsRealError(aiFeedback, "ОШИБКА КОНТРАСТНОСТИ");
        var hasError = aiFeedback.StartsWith("All providers failed.") ||
                       aiFeedback.StartsWith("Exception:") ||
                       mojibake || contrast;

        var encodedFeedback = aiFeedback.Replace("<", "&lt;").Replace(">", "&gt;");

        var borderColor = hasError ? "#F87171" : "#4ADE80";
        var badgeText = hasError ? "FAILED" : "OK";
        if (contrast) badgeText = "FAILED / CONTRAST ISSUE";
        else if (mojibake) badgeText = "FAILED / MOJIBAKE FOUND";
        var statusBadge = $"<span style='background:{(hasError ? "#F87171" : "#4ADE80")};color:#000;padding:2px 8px;border-radius:3px;font-size:11px;margin-left:10px;'>{badgeText}</span>";

        _steps.Add($@"
        <div class='step' style='border-left: 4px solid {borderColor};'>
            <h3>{title} {statusBadge}</h3>
            <img src='data:image/png;base64,{base64Image}' alt='Screenshot' style='max-width:800px; border: 1px solid #ccc;'/>
            <div class='feedback'>
                <strong>Vision AI:</strong><br/>
                <pre>{encodedFeedback}</pre>
            </div>
        </div>");
    }

    public void AddFailure(string title, string reason)
    {
        _steps.Add($@"
        <div class='step' style='border-left: 4px solid #F87171;'>
            <h3>{title} <span style='background:#F87171;color:#000;padding:2px 8px;border-radius:3px;font-size:11px;margin-left:10px;'>FAILED</span></h3>
            <div class='feedback'>
                <strong>Reason:</strong><br/>
                <pre>{reason.Replace("<", "&lt;").Replace(">", "&gt;")}</pre>
            </div>
        </div>");
    }

    public void Save(string path)
    {
        var headerBanner = _allProvidersFailed
            ? $@"<div style='background:#7F1D1D;color:#FEE2E2;padding:20px;border-radius:8px;margin-bottom:20px;border:2px solid #F87171;'>
                <h2 style='margin-top:0;'>VISION API UNAVAILABLE</h2>
                <p>All configured Vision API providers failed. The test runner took screenshots and ran UI interactions, but could not analyze the results. <strong>Manual review is required.</strong></p>
                <pre style='background:#000;color:#FCA5A5;padding:10px;border-radius:4px;overflow:auto;'>{_allProvidersFailedReason}</pre>
                <p style='margin-bottom:0;'>Fix: add a working provider to <code>config.test.json</code> or wait for rate limits to reset.</p>
              </div>"
            : "";

        var html = $@"
        <html>
        <head>
            <meta charset='utf-8'/>
            <title>BrainstormBuddy UI Test Report</title>
            <style>
                body {{ font-family: sans-serif; background: #121212; color: #E0E0E0; padding: 20px; }}
                .step {{ background: #1E1E1E; padding: 15px; margin-bottom: 20px; border-radius: 8px; box-shadow: 0 4px 6px rgba(0,0,0,0.3); }}
                .feedback {{ background: #2D2D2D; padding: 10px; margin-top: 10px; border-radius: 4px; border-left: 4px solid #4ADE80; }}
                pre {{ white-space: pre-wrap; font-family: inherit; margin: 0; padding: 5px 0; }}
                code {{ background: #333; padding: 2px 6px; border-radius: 3px; font-family: 'Cascadia Code', monospace; }}
            </style>
        </head>
        <body>
            <h1>BrainstormBuddy UI Test Report</h1>
            {headerBanner}
            {string.Join("\n", _steps)}
        </body>
        </html>";

        File.WriteAllText(path, html, Encoding.UTF8);
    }
}
