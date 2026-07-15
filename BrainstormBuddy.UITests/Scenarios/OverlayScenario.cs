using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrainstormBuddy.UITests.Infrastructure;
using BrainstormBuddy.UITests.Reporting;

namespace BrainstormBuddy.UITests.Scenarios;

/// <summary>
/// Проверка ГЛАВНОГО окна (оверлея): раньше тестер его вообще не скринил,
/// и визуальный развал (наложения панелей, обрезанные слайдеры) уходил в прод незамеченным.
/// Два уровня проверки:
///  1) СТРУКТУРНАЯ (без vision): BoundingRectangle ключевых зон не должны пересекаться —
///     ловит наложения панелей детерминированно, без галлюцинаций модели.
///  2) VISION: скриншот в отчёт + вердикт модели (кодировка/контраст).
/// Оверлей исключён из захвата экрана (WDA_EXCLUDEFROMCAPTURE) — перед скриншотом
/// шлём глобальный хоткей Ctrl+Shift+C (режим скриншота), после — возвращаем.
/// </summary>
public class OverlayScenario
{
    private const string OVERLAY_PROMPT =
        "Ты — QA-инженер, проверяешь скриншот компактного оверлея поверх экрана (тёмная тема). " +
        "Ожидаемые зоны сверху вниз: селектор пресета с индикаторами REC/LLM; панель с двумя " +
        "слайдерами ТИШИНА и ЧАНК МАКС и рядом кнопок-иконок; статус; история вопрос-ответ; " +
        "красный аудио-таймлайн с временной шкалой; строка токенов; поле ввода. Отвечай ТОЛЬКО на русском.\n" +
        "1) Если какая-то надпись нечитаема как русское/английское слово — 'ОШИБКА КОДИРОВКИ:' и ДОСЛОВНАЯ цитата.\n" +
        "2) Если элементы наложены друг на друга или текст обрезан — 'ОШИБКА КОМПОНОВКИ:' и укажи какие.\n" +
        "3) Если всё в порядке — начни со слова 'OK' и перечисли зоны, которые видишь.\n" +
        "Запрещено выдумывать проблемы и давать советы.";

    public async Task RunAsync(AppLauncher launcher, VisionClient vision, HtmlReportBuilder report)
    {
        Console.WriteLine("=== OVERLAY: looking for main window ===");
        Window overlay = null;
        for (int i = 0; i < 10 && overlay == null; i++)
        {
            overlay = launcher.GetAllTopLevelWindows()
                .FirstOrDefault(w => w.Title.Contains("Overlay", StringComparison.OrdinalIgnoreCase));
            if (overlay == null) Thread.Sleep(1000);
        }
        if (overlay == null) { report.AddFailure("Overlay", "Main overlay window not found"); return; }

        // --- 1) Структурная проверка: зоны не пересекаются ---
        var zones = new (string name, string automationId)[]
        {
            ("PresetSelector", "PresetSelector"),
            ("SilenceSlider", "SilenceSlider"),
            ("ChunkSlider", "ChunkSlider"),
            ("StatusText", "StatusText"),
            ("HistoryScroll", "HistoryScroll"),
            ("TimelineImage", "TimelineImage"),
            ("ChatInputBox", "ChatInputBox"),
        };
        var rects = new List<(string name, Rectangle rect)>();
        // Эти зоны видны только в Инженерном режиме, а TimelineImage ещё и внутри сворачиваемого
        // эквалайзера (по умолчанию свёрнут) — их отсутствие НЕ дефект компоновки.
        var engineerGated = new HashSet<string> { "SilenceSlider", "ChunkSlider", "TimelineImage" };
        foreach (var (name, id) in zones)
        {
            var el = overlay.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (el == null)
            {
                if (engineerGated.Contains(id))
                    report.AddStep($"Overlay: зона {name}", null, "SKIPPED: контрол скрыт (Инженерный режим / свёрнутый эквалайзер).");
                else
                    report.AddFailure($"Overlay: зона {name}", $"Элемент {id} не найден в дереве");
                continue;
            }
            var r = el.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) { report.AddFailure($"Overlay: зона {name}", $"Нулевой размер: {r}"); continue; }
            rects.Add((name, r));
        }

        var overlaps = new List<string>();
        for (int a = 0; a < rects.Count; a++)
            for (int b = a + 1; b < rects.Count; b++)
            {
                var ia = Rectangle.Intersect(rects[a].rect, rects[b].rect);
                // допускаем 2px соприкосновения на границах
                if (ia.Width > 2 && ia.Height > 2)
                    overlaps.Add($"{rects[a].name} пересекается с {rects[b].name} ({ia.Width}x{ia.Height}px)");
            }

        if (overlaps.Count > 0)
            report.AddFailure("Overlay: структура (наложения зон)", string.Join("\n", overlaps));
        else
            report.AddStep("Overlay: структура", null, $"OK — {rects.Count} зон найдено, наложений нет.");
        Console.WriteLine($"Overlay structure: {rects.Count} zones, {overlaps.Count} overlaps");

        // --- 2) Vision-проверка: показать в захвате, скрин, вернуть ---
        // Capture.Element снимает ОБЛАСТЬ ЭКРАНА: чужое окно поверх (напр. Диспетчер задач)
        // попадёт в кадр. Выводим окно на передний план перед скрином.
        try { overlay.Focus(); } catch { }
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_C);
        Thread.Sleep(900);
        try
        {
            var b64 = CaptureBase64(overlay);
            var fb = await vision.AskAboutImageAsync(b64, OVERLAY_PROMPT);
            report.AddStep("Overlay: главное окно", b64, fb);
        }
        finally
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_C);
            Thread.Sleep(300);
        }
    }

    private string CaptureBase64(Window window)
    {
        var image = FlaUI.Core.Capturing.Capture.Element(window);
        using var ms = new MemoryStream();
        image.Bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
}
