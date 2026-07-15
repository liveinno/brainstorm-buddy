using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BrainstormBuddy.UITests.Infrastructure;
using BrainstormBuddy.UITests.Reporting;

namespace BrainstormBuddy.UITests.Scenarios;

public class UiUxAuditScenario
{
    private const string P = @"ВАЖНО: Ты профессиональный UI-аудитор. Твоя задача — найти ВСЕ проблемы контрастности на скриншоте.

ПРОВЕРЬ КАЖДЫЙ элемент:
1. ЛЕВОЕ МЕНЮ (сайдбар): читается ли КАЖДЫЙ пункт? Цвет шрифта vs цвет фона?
2. КНОПКИ ВНИЗУ (Открыть папку, Сохранить, Закрыть): виден ли текст?
3. ВСЕ ПОЛЯ ВВОДА (TextBox): виден ли текст внутри?
4. ВСЕ ВЫПАДАЮЩИЕ СПИСКИ (ComboBox): виден ли выбранный элемент?
5. ВСЕ ЗАГОЛОВКИ И ПОДПИСИ: читаются ли?
6. КНОПКА 'Проверить подключение' и другие кнопки в контенте.

Если хоть один элемент нечитаем — напиши 'ОШИБКА КОНТРАСТНОСТИ: [описание проблемы]'.
Если всё читается идеально — напиши 'КОНТРАСТНОСТЬ OK'.
Отвечай ТОЛЬКО на русском.";

    public async Task RunAsync(AppLauncher launcher, VisionClient vision, HtmlReportBuilder report)
    {
        Window settingsWindow = null;
        for (int i = 0; i < 15 && settingsWindow == null; i++) {
            foreach (var w in launcher.GetAllTopLevelWindows())
                if (w.Title.Contains("BrainstormBuddy") && w.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem)).Length >= 5)
                    { settingsWindow = w; break; }
            Thread.Sleep(1000);
        }
        if (settingsWindow == null) { report.AddFailure("UX", "No settings window"); return; }
        settingsWindow.Focus(); Thread.Sleep(500);

        foreach (var theme in new[] { "LightTheme", "HackerTheme" }) {
            Console.WriteLine($"`n=== THEME: {theme} ===");
            var tc = settingsWindow.FindFirstDescendant(cf => cf.ByAutomationId("ThemeCombo"))?.AsComboBox();
            if (tc != null) { tc.Select(theme); Thread.Sleep(1000); }

            // Сайдбар = TabControl → TabItem (idx 4 = Оверлей, там живёт ThemeCombo)
            var si = settingsWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem));
            if (si.Length >= 5) { try { si[4].AsTabItem().Select(); } catch { si[4].Click(); } Thread.Sleep(500); }

            var b64 = CaptureBase64(settingsWindow);
            var fb = await vision.AskAboutImageAsync(b64, P + $"Тема {theme}. Проверь контрастность ВСЕГО текста.");
            report.AddStep($"{theme}: контраст", b64, fb);

            // Hover buttons + tooltip check
            var btns = settingsWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button));
            Console.WriteLine($"Buttons: {btns.Length}");
            for (int j = 0; j < btns.Length; j++) {
                try {
                    var btn = btns[j].AsButton();
                    if (!btn.IsEnabled) continue;
                    var r = btn.BoundingRectangle;
                    Mouse.MoveTo((int)(r.X + r.Width/2), (int)(r.Y + r.Height/2));
                    if (j % 5 == 0) Thread.Sleep(1000); // slow down for tooltips
                    else Thread.Sleep(100);
                } catch {}
            }
            Thread.Sleep(800);
            var hb64 = CaptureBase64(settingsWindow);
            var hfb = await vision.AskAboutImageAsync(hb64, $"Тема {theme}. Видны ли всплывающие подсказки (tooltips) на кнопках? ВСЕ ли кнопки их показывают?");
            report.AddStep($"{theme}: tooltips", hb64, hfb);
        }

        // Check overlay for icons
        Window overlay = null;
        foreach (var w in launcher.GetAllTopLevelWindows())
            if (w.Title.Contains("Overlay", StringComparison.OrdinalIgnoreCase)) { overlay = w; break; }
        if (overlay != null) {
            overlay.Focus(); Thread.Sleep(500);
            var ob = CaptureBase64(overlay);
            var ofb = await vision.AskAboutImageAsync(ob, "Это ГЛАВНОЕ ОКНО (оверлей). Видны ли SVG-ИКОНКИ на кнопках тулбара (шестерёнка, пауза, корзина, скрепка, фотоаппарат, стрелки)? Или там emoji/текст? Напиши ЧТО ИМЕННО ты видишь на месте кнопок.");
            report.AddStep("Overlay: иконки", ob, ofb);
        } else report.AddFailure("Overlay", "Not found");

        var cb = settingsWindow.FindFirstDescendant(cf => cf.ByName("Закрыть"))?.AsButton();
        if (cb != null) { cb.Invoke(); }
    }

    private string CaptureBase64(Window w) {
        var img = FlaUI.Core.Capturing.Capture.Element(w);
        using var ms = new MemoryStream();
        img.Bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
}
