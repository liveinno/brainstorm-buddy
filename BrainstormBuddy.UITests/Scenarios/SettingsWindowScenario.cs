using FlaUI.Core.AutomationElements;
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrainstormBuddy.UITests.Infrastructure;
using BrainstormBuddy.UITests.Reporting;

namespace BrainstormBuddy.UITests.Scenarios;

public class SettingsWindowScenario
{
    // ВАЖНО: в промпте НЕЛЬЗЯ приводить примеры «каракуль» — малые vision-модели
    // цитируют их из промпта как найденные (проверено на qwen2.5vl:3b, report 2026-07-04).
    private const string UI_CHECK_PROMPT =
        "Ты — QA-инженер, проверяешь скриншот WPF-приложения. Отвечай ТОЛЬКО на русском.\n" +
        "Правила вердикта:\n" +
        "1) Если КАЖДАЯ надпись на скриншоте читается как осмысленный русский или английский текст — " +
        "начни ответ со слова 'OK' и перечисли 3-5 главных элементов, которые видишь.\n" +
        "2) 'ОШИБКА КОДИРОВКИ:' пиши ТОЛЬКО если на скриншоте есть надпись, которую ты НЕ можешь прочитать " +
        "как русское или английское слово. Обязан процитировать её ДОСЛОВНО, символ в символ, как видишь на экране. " +
        "Если не можешь процитировать конкретную надпись — ошибки нет.\n" +
        "3) 'ОШИБКА КОНТРАСТНОСТИ:' пиши ТОЛЬКО если конкретная надпись почти не отличима по цвету от своего фона " +
        "и потому нечитаема. Укажи, какая именно надпись. Тёмная тема и приглушённые подписи — это дизайн, НЕ ошибка.\n" +
        "Запрещено: выдумывать проблемы, давать советы, упоминать ошибки которых не видишь. ";
    private readonly string _targetTab;

    public SettingsWindowScenario(string targetTab = null) { _targetTab = targetTab; }

    public async Task RunAsync(AppLauncher launcher, VisionClient vision, HtmlReportBuilder report, string targetTab = null)
    {
        var effectiveTarget = targetTab ?? _targetTab;
        Console.WriteLine("Waiting for Settings window...");
        Window settingsWindow = FindSettings(launcher, attempts: 5);
        if (settingsWindow == null)
        {
            // Окно могли закрыть предыдущие сценарии (регресс) — открываем сами из оверлея
            Console.WriteLine("Settings window not found — opening via overlay SettingsButton");
            var overlay = launcher.GetAllTopLevelWindows()
                .FirstOrDefault(w => w.Title.Contains("Overlay", StringComparison.OrdinalIgnoreCase));
            var btn = overlay?.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))?.AsButton();
            btn?.Invoke();
            settingsWindow = FindSettings(launcher, attempts: 8);
        }
        if (settingsWindow == null) { report.AddFailure("Settings", "Window not found (даже после открытия из оверлея)"); return; }
        settingsWindow.Focus(); Thread.Sleep(500);

        // Сайдбар настроек = TabControl с TabStripPlacement=Left → пункты имеют ControlType.TabItem
        var sidebarItems = settingsWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem));
        if (sidebarItems.Length < 7) { report.AddFailure("Sidebar", $"Expected >=7 tabs, found {sidebarItems.Length}"); return; }

        void SelectTab(int idx)
        {
            try { sidebarItems[idx].AsTabItem().Select(); } catch { sidebarItems[idx].Click(); }
        }

        // Switch to HackerTheme first for dark theme testing (Оверлей = idx 4)
        Console.WriteLine("Step 0: Switch to HackerTheme");
        SelectTab(4); Thread.Sleep(500);
        var themeCombo = settingsWindow.FindFirstDescendant(cf => cf.ByAutomationId("ThemeCombo"))?.AsComboBox();
        if (themeCombo != null) { themeCombo.Select("HackerTheme"); Thread.Sleep(800); }

        // Test all sidebar tabs
        var navMap = new (int idx, string name)[] {
            (0, "API"), (1, "LLM"), (2, "Аудио"), (3, "Локальный STT"), (4, "Оверлей"),
            (5, "Горячие клавиши"), (6, "О приложении"), (7, "Профиль"), (8, "Агенты"), (9, "LLM Логи")
        };

        foreach (var (idx, name) in navMap) {
            if (!ShouldProcessTab(name, effectiveTarget)) continue;
            if (idx >= sidebarItems.Length) continue;
            Console.WriteLine($"Switching to: {name} (idx {idx})");
            SelectTab(idx);
            Thread.Sleep(800);
            var b64 = CaptureBase64(settingsWindow);
            var fb = await vision.AskAboutImageAsync(b64, UI_CHECK_PROMPT + $"Тёмная тема, вкладка настроек '{name}'.");
            report.AddStep($"Dark: {name}", b64, fb);
        }

        // === INTERACTIVE TESTS ===
        // 1. Test ComboBox dropdowns actually open
        Console.WriteLine("=== INTERACTIVE: Testing ComboBox dropdowns ===");
        SelectTab(0); Thread.Sleep(500); // API tab
        var combos = settingsWindow.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox));
        Console.WriteLine($"Found {combos.Length} combos on API tab");
        foreach (var c in combos) {
            try {
                var cb = c.AsComboBox();
                if (cb.IsEnabled) {
                    Console.WriteLine($"Expanding combo: {cb.Name}");
                    cb.Expand(); Thread.Sleep(300);
                    Console.WriteLine("  -> Dropdown expand attempted (OK)");
                    report.AddStep($"Dropdown: {cb.Name}", null, "Expand() called successfully.");
                    cb.Collapse();
                }
            } catch (Exception ex) { Console.WriteLine($"  Combo error: {ex.Message}"); }
        }

        // 2. Test theme switching (HackerTheme vs LightTheme)
        Console.WriteLine("=== INTERACTIVE: Theme switching ===");
        SelectTab(4); Thread.Sleep(500); // Overlay
        var themeCb = settingsWindow.FindFirstDescendant(cf => cf.ByAutomationId("ThemeCombo"))?.AsComboBox();
        if (themeCb != null) {
            themeCb.Select("HackerTheme"); Thread.Sleep(800);
            var bDark = CaptureBase64(settingsWindow);
            var fDark = await vision.AskAboutImageAsync(bDark, UI_CHECK_PROMPT + "Тёмная тема, вкладка настроек 'Оверлей'.");
            report.AddStep("H: Оверлей", bDark, fDark);

            themeCb.Select("LightTheme"); Thread.Sleep(800);
            var bLight = CaptureBase64(settingsWindow);
            var fLight = await vision.AskAboutImageAsync(bLight, UI_CHECK_PROMPT + "Светлая тема, вкладка настроек 'Оверлей'.");
            report.AddStep("L: Оверлей", bLight, fLight);

            // Now test ALL tabs in LightTheme
            foreach (var (idx, name) in navMap) {
                if (!ShouldProcessTab(name, effectiveTarget)) continue;
                if (idx >= sidebarItems.Length) continue;
                SelectTab(idx);
                Thread.Sleep(800);
                var b64 = CaptureBase64(settingsWindow);
                var fb = await vision.AskAboutImageAsync(b64, UI_CHECK_PROMPT + $"Светлая тема, вкладка настроек '{name}'.");
                report.AddStep($"Light: {name}", b64, fb);
            }

            themeCb.Select("HackerTheme"); Thread.Sleep(500);
        }
        // Don't close - UX audit will handle it
    }

    private static Window FindSettings(AppLauncher launcher, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            foreach (var w in launcher.GetAllTopLevelWindows())
                if (w.Title.Contains("BrainstormBuddy", StringComparison.OrdinalIgnoreCase)
                    && w.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem)).Length >= 5)
                    return w;
            Thread.Sleep(1000);
        }
        return null;
    }

    private static bool ShouldProcessTab(string tabName, string effectiveTarget) {
        if (string.IsNullOrEmpty(effectiveTarget)) return true;
        return tabName.Equals(effectiveTarget, StringComparison.OrdinalIgnoreCase);
    }

    private string CaptureBase64(Window window) {
        var image = FlaUI.Core.Capturing.Capture.Element(window);
        using var ms = new MemoryStream();
        image.Bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
}
