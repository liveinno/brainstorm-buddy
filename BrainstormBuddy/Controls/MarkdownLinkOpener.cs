using System;
using System.Diagnostics;

namespace BrainstormBuddy;

/// <summary>Открытие ссылок из markdown-текста. Общий помощник для окон
/// (история встреч, «Спросить память», транскрибация файлов, оверлей):
/// открываем только безопасные схемы (http/https/mailto) и только по явному
/// клику пользователя — сам компонент никаких схем не исполняет.</summary>
internal static class MarkdownLinkOpener
{
    public static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* браузер или почтовый клиент не запустился — молча */ }
    }
}
