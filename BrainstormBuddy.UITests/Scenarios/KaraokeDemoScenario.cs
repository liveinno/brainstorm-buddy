using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using System;
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
/// Караоке-лента: «Предпросмотр» из вкладки настроек → ждём окно-полосу → скрин.
/// Регрессия 2.7.0 (фикс 2.7.1): лерп цвета к TextColor уходил в чёрное, плюс
/// в RebuildAgeBrushes терялось присваивание _ageBrushes — вся лента была TextColor.
/// Проверка двойная: детерминированная пиксельная (свежая строка = золото #FFE9A8,
/// старшие = белые/приглушённые белые по DimByAge) + вердикт vision-модели.
/// Демо-таймер не нужен: PreviewKaraoke пушит 3 статичные строки сразу
/// (question фильтруется по умолчанию → видно минимум 2 строки, age0 и age1).
/// </summary>
public class KaraokeDemoScenario
{
    private const string KARAOKE_PROMPT =
        "Ты — QA-инженер. На скриншоте — караоке-лента (узкая полоса с несколькими строками русского текста). " +
        "Отвечай ТОЛЬКО на русском.\n" +
        "Проверь цвета строк:\n" +
        "1) Последняя (нижняя, самая свежая) строка должна быть ЗОЛОТОГО/бледно-жёлтого цвета (#FFE9A8).\n" +
        "2) Строки выше неё должны быть БЕЛЫМИ или слегка приглушёнными серо-белыми (затухание по возрасту — это норма).\n" +
        "Если оба условия выполняются — начни ответ со слова 'OK' и перечисли строки, которые видишь.\n" +
        "Если свежая строка не золотая ИЛИ старшие строки тёмные/чёрные/нечитаемые — начни с 'ОШИБКА ЦВЕТА:' " +
        "и опиши, что видишь. Если ленты или текста на скриншоте нет вообще — 'ОШИБКА ЗАХВАТА:' и опиши кадр.";

    private AppLauncher _launcher = null!;
    private HtmlReportBuilder _report = null!;

    public async Task RunAsync(AppLauncher launcher, VisionClient vision, HtmlReportBuilder report)
    {
        _launcher = launcher;
        _report = report;
        Console.WriteLine("=== KARAOKE DEMO: preview + color check ===");

        var settings = FindSettings(launcher, attempts: 5);
        if (settings == null)
        {
            // Окно могли закрыть предыдущие сценарии — открываем из оверлея.
            var overlay = launcher.GetAllTopLevelWindows()
                .FirstOrDefault(w => w.Title.Contains("Overlay", StringComparison.OrdinalIgnoreCase));
            overlay?.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))?.AsButton()?.Invoke();
            settings = FindSettings(launcher, attempts: 8);
        }
        if (settings == null)
        {
            report.AddFailure("Karaoke", "Окно настроек не найдено (даже после открытия из оверлея)");
            return;
        }
        try { settings.Focus(); } catch { }
        Thread.Sleep(500);

        // Скрытое из захвата окно снимется сплошным чёрным — гасим флаг на время прогона.
        bool stealthWasOn = DisableStealthForCapture(settings);

        try
        {
            if (!SelectKaraokeTab(settings)) return;

            // Полупрозрачный фон просвечивает проводник/рабочий стол — на скрине
            // под текстом чужой контент, vision-вердикт мусорится. Поднимаем до 1.0.
            double? oldBgOpacity = RaiseBgOpacityForCapture(settings);
            try
            {
                // Градиент выключен → свежей золотой строки нет, все строки = TextColor.
                bool fadeOn = ReadFadeCheckbox(settings);
                if (!InvokePreview(settings)) return;

            // Настройки могут перекрыть полосу — сворачиваем на время съёмки.
            bool minimized = TryMinimize(settings);
            try
            {
                var karaoke = FindKaraoke(launcher, attempts: 10);
                if (karaoke == null)
                {
                    report.AddFailure("Karaoke: предпросмотр", "Окно «BrainstormBuddy Karaoke» не появилось после клика «Предпросмотр»");
                    return;
                }
                // Capture.Element снимает экранный прямоугольник — если поверх ленты
                // всплыло чужое topmost-окно, в кадр попадёт оно. Поднимаем ленту.
                try { karaoke.SetForeground(); } catch { try { karaoke.Focus(); } catch { } }
                Thread.Sleep(1000); // дожимаем slide-in/фейд-анимации строк

                int lineCount = karaoke.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Count(el => !string.IsNullOrWhiteSpace(el.Name));
                Console.WriteLine($"Karaoke window: {lineCount} text lines");

                string b64;
                Bitmap bmp;
                try { var shot = FlaUI.Core.Capturing.Capture.Element(karaoke); bmp = (Bitmap)shot.Bitmap; }
                catch (Exception ex)
                {
                    report.AddFailure("Karaoke: скриншот", $"Capture.Element упал: {ex.Message}");
                    return;
                }

                using (bmp)
                {
                    b64 = ToBase64(bmp);
                    ReportPixelVerdict(bmp, lineCount, fadeOn);
                }

                if (vision != null)
                {
                    var prompt = KARAOKE_PROMPT + (fadeOn ? "" :
                        "\nВАЖНО: градиент по возрасту отключён в настройках — все строки должны быть одного цвета, золотой строки не будет. Проверь только что текст вообще читаем.");
                    var fb = await vision.AskAboutImageAsync(b64, prompt);
                    report.AddStep("Karaoke: демо-лента (vision)", b64, fb ?? "Vision не ответил");
                    if (fb != null && fb.Contains("ОШИБКА ЦВЕТА"))
                        report.AddFailure("Karaoke: цвета (vision)", fb);
                }
                else
                {
                    report.AddStep("Karaoke: демо-лента", b64, "Vision недоступен — вердикт только пиксельный выше.");
                }
            }
            finally
            {
                if (minimized)
                    try { SetVisualState(settings, WindowVisualState.Normal); } catch { }
            }
            }
            finally
            {
                if (oldBgOpacity != null) RestoreBgOpacity(settings, oldBgOpacity.Value);
            }
        }
        finally
        {
            if (stealthWasOn) RestoreStealth(settings);
            // Возвращаем фокус настройкам — следующие сценарии ищут её элементы.
            try { if (GetVisualState(settings) == WindowVisualState.Minimized) SetVisualState(settings, WindowVisualState.Normal); } catch { }
        }
    }

    // ─── Шаги ─────────────────────────────────────────────────────────────
    /// <summary>Чекбокс «Градиент цвета по возрасту»: выключен → ждать золото нельзя
    /// (все строки = TextColor, юзер мог снять фейд). true при недоступности — дефолт ON.</summary>
    private static bool ReadFadeCheckbox(Window settings)
    {
        try
        {
            var cb = settings.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .FirstOrDefault(c =>
                    (c.Name ?? "").Contains("Градиент цвета", StringComparison.OrdinalIgnoreCase) ||
                    (c.Name ?? "").Contains("age", StringComparison.OrdinalIgnoreCase))
                ?.AsCheckBox();
            return cb?.IsChecked ?? true;
        }
        catch { return true; }
    }

    private bool SelectKaraokeTab(Window settings)
    {
        var tabs = settings.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem));
        // Имя берём из Header («Караоке» / "Karaoke"), индекс 6 — запасной (0-5: API…Оверлей).
        var tab = tabs.FirstOrDefault(t =>
                (t.Name ?? "").Contains("караок", StringComparison.OrdinalIgnoreCase) ||
                (t.Name ?? "").Contains("karaoke", StringComparison.OrdinalIgnoreCase))
            ?? tabs.ElementAtOrDefault(6);
        if (tab == null)
        {
            _report.AddFailure("Karaoke: вкладка", $"TabItem «Караоке» не найден (всего табов: {tabs.Length})");
            return false;
        }
        try { tab.AsTabItem().Select(); } catch { try { tab.Click(); } catch { } }
        Thread.Sleep(700);
        return true;
    }

    private bool InvokePreview(Window settings)
    {
        var btn = settings.FindFirstDescendant(cf => cf.ByAutomationId("KaraokePreviewButton"))?.AsButton();
        if (btn == null)
        {
            _report.AddFailure("Karaoke: предпросмотр", "Кнопка KaraokePreviewButton не найдена на вкладке");
            return false;
        }
        try
        {
            // Кнопка внизу ScrollViewer — Click() упал бы без ClickablePoint, Invoke безопасен.
            if (btn.Patterns.Invoke.IsSupported) btn.Patterns.Invoke.Pattern.Invoke();
            else btn.Invoke();
        }
        catch (Exception ex)
        {
            try { btn.Click(); }
            catch { _report.AddFailure("Karaoke: предпросмотр", $"Клик по «Предпросмотр» упал: {ex.Message}"); return false; }
        }
        Thread.Sleep(800);
        return true;
    }

    /// <summary>Снять галку «Невидим при захвате экрана» + Сохранить, чтобы
    /// ApplyLiveConfigChanges → KaraokeWindow.ApplyNativeModes убрал WDA-исключение.
    /// Возвращает true, если галка была включена и мы её сняли (нужно вернуть).</summary>
    private bool DisableStealthForCapture(Window settings)
    {
        try
        {
            if (!SelectKaraokeTabForEdit(settings)) return false;
            var cb = FindStealthCheckbox(settings);
            if (cb == null) { Console.WriteLine("stealth checkbox not found — skip"); return false; }
            if (cb.IsChecked != true) return false; // уже видимо в захвате

            cb.Toggle();
            Thread.Sleep(400);
            var save = settings.FindFirstDescendant(cf => cf.ByAutomationId("SaveBtn"))?.AsButton();
            if (save?.Patterns.Invoke.IsSupported == true) save.Patterns.Invoke.Pattern.Invoke();
            else save?.Invoke();
            Thread.Sleep(800);
            Console.WriteLine("stealth disabled for capture");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"stealth toggle failed (non-fatal): {ex.Message}");
            _report.AddStep("Karaoke: стелс-флаг", null, $"WARN — не удалось снять «Невидим при захвате»: {ex.Message}. " +
                "Если скрин выйдет чёрным — это WDA_EXCLUDEFROMCAPTURE, не цветовая регрессия.");
            return false;
        }
    }

    private void RestoreStealth(Window settings)
    {
        try
        {
            if (GetVisualState(settings) == WindowVisualState.Minimized)
                SetVisualState(settings, WindowVisualState.Normal);
            Thread.Sleep(400);
            if (!SelectKaraokeTabForEdit(settings)) return;
            var cb = FindStealthCheckbox(settings);
            if (cb == null || cb.IsChecked == true) return;
            cb.Toggle();
            Thread.Sleep(400);
            var save = settings.FindFirstDescendant(cf => cf.ByAutomationId("SaveBtn"))?.AsButton();
            if (save?.Patterns.Invoke.IsSupported == true) save.Patterns.Invoke.Pattern.Invoke();
            else save?.Invoke();
            Thread.Sleep(500);
            Console.WriteLine("stealth restored");
        }
        catch (Exception ex) { Console.WriteLine($"stealth restore failed: {ex.Message}"); }
    }

    /// <summary>Поднять «Непрозрачность фона полосы» до 1.0 + Сохранить, чтобы скрин
    /// не просвечивал окна за лентой (иначе vision видит проводник под текстом).
    /// Возвращает исходное значение или null, если трогать не пришлось.</summary>
    private double? RaiseBgOpacityForCapture(Window settings)
    {
        try
        {
            if (!SelectKaraokeTabForEdit(settings)) return null;
            var slider = FindBgOpacitySlider(settings);
            if (slider == null || !slider.Patterns.RangeValue.IsSupported) { Console.WriteLine("bg-opacity slider not found — skip"); return null; }
            var rv = slider.Patterns.RangeValue.Pattern;
            double old = rv.Value;
            if (old >= 0.99) return null; // уже непрозрачный
            rv.SetValue(1.0);
            Thread.Sleep(400);
            SaveSettings(settings);
            Console.WriteLine($"bg-opacity raised {old:F2} -> 1.00 for capture");
            return old;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"bg-opacity raise failed (non-fatal): {ex.Message}");
            _report.AddStep("Karaoke: фон", null, $"WARN — не удалось поднять непрозрачность фона: {ex.Message}. " +
                "Скрин может просвечивать окна позади ленты.");
            return null;
        }
    }

    private void RestoreBgOpacity(Window settings, double old)
    {
        try
        {
            if (GetVisualState(settings) == WindowVisualState.Minimized)
                SetVisualState(settings, WindowVisualState.Normal);
            Thread.Sleep(400);
            if (!SelectKaraokeTabForEdit(settings)) return;
            var slider = FindBgOpacitySlider(settings);
            if (slider == null || !slider.Patterns.RangeValue.IsSupported) return;
            slider.Patterns.RangeValue.Pattern.SetValue(old);
            Thread.Sleep(400);
            SaveSettings(settings);
            Console.WriteLine($"bg-opacity restored to {old:F2}");
        }
        catch (Exception ex) { Console.WriteLine($"bg-opacity restore failed: {ex.Message}"); }
    }

    private static Slider? FindBgOpacitySlider(Window settings) =>
        settings.FindAllDescendants(cf => cf.ByControlType(ControlType.Slider))
            .Select(s => s.AsSlider())
            .FirstOrDefault(s => Math.Abs(s.Minimum - 0.0) < 0.001 && Math.Abs(s.Maximum - 1.0) < 0.001);

    private static void SaveSettings(Window settings)
    {
        var save = settings.FindFirstDescendant(cf => cf.ByAutomationId("SaveBtn"))?.AsButton();
        if (save?.Patterns.Invoke.IsSupported == true) save.Patterns.Invoke.Pattern.Invoke();
        else save?.Invoke();
        Thread.Sleep(800);
    }

    private bool SelectKaraokeTabForEdit(Window settings)
    {
        var tabs = settings.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem));
        var tab = tabs.FirstOrDefault(t =>
                (t.Name ?? "").Contains("караок", StringComparison.OrdinalIgnoreCase) ||
                (t.Name ?? "").Contains("karaoke", StringComparison.OrdinalIgnoreCase))
            ?? tabs.ElementAtOrDefault(6);
        if (tab == null) return false;
        try { tab.AsTabItem().Select(); } catch { try { tab.Click(); } catch { } }
        Thread.Sleep(600);
        return true;
    }

    private CheckBox? FindStealthCheckbox(Window settings) =>
        settings.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
            .FirstOrDefault(c =>
                (c.Name ?? "").Contains("захвате", StringComparison.OrdinalIgnoreCase) ||
                (c.Name ?? "").Contains("capture", StringComparison.OrdinalIgnoreCase))
            ?.AsCheckBox();

    // ─── Поиск окон ───────────────────────────────────────────────────────

    private static Window FindSettings(AppLauncher launcher, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            foreach (var w in launcher.GetAllTopLevelWindows())
                if (w.Title.Contains("BrainstormBuddy", StringComparison.OrdinalIgnoreCase)
                    && !w.Title.Contains("Karaoke", StringComparison.OrdinalIgnoreCase)
                    && w.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).Length >= 5)
                    return w;
            Thread.Sleep(1000);
        }
        return null!;
    }

    private static Window? FindKaraoke(AppLauncher launcher, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            var w = launcher.GetAllTopLevelWindows()
                .FirstOrDefault(x => x.Title.Contains("Karaoke", StringComparison.OrdinalIgnoreCase));
            if (w != null) return w;
            Thread.Sleep(1000);
        }
        return null;
    }

    private static void SetVisualState(Window w, WindowVisualState state) =>
        w.Patterns.Window.PatternOrDefault?.SetWindowVisualState(state);

    private static WindowVisualState? GetVisualState(Window w) =>
        w.Patterns.Window.PatternOrDefault?.WindowVisualState.Value;

    private static bool TryMinimize(Window w)
    {
        try { SetVisualState(w, WindowVisualState.Minimized); return GetVisualState(w) == WindowVisualState.Minimized; }
        catch
        {
            try { w.Move(0, 0); return false; }
            catch { return false; }
        }
    }

    // ─── Пиксельная проверка цветов ────────────────────────────────────────

    /// <summary>
    /// Свежая строка (age0) — золото #FFE9A8; старшие — белые, приглушённые DimByAge
    /// (альфа 0.72/0.48/… на фоне #0A1622 даёт серо-голубоватые тона ~#BAB7C2 и ниже).
    /// Классификация: золото = тёплый (r заметно > b), белое = нейтральное
    /// (каналы близки, без тёплого сдвига). Чёрный кадр = окно под WDA/пустое.
    /// </summary>
    private void ReportPixelVerdict(Bitmap bmp, int lineCount, bool fadeOn)
    {
        int total = 0, black = 0, gold = 0, white = 0;
        for (int y = 0; y < bmp.Height; y += 2)
        for (int x = 0; x < bmp.Width; x += 2)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            int max = Math.Max(p.R, Math.Max(p.G, p.B));
            if (max < 12) { black++; continue; }
            if (p.R >= 200 && p.G >= 165 && p.R - p.B >= 55) { gold++; continue; }
            // Белый после альфа-бленда с фоном: ярче фона, каналы близкие (b может
            // слегка доминировать из-за синего фона), без золотого сдвига.
            if (max - Math.Min(p.R, Math.Min(p.G, p.B)) < 60 && max >= 120 && p.R - p.B < 40)
                white++;
        }

        double blackRatio = total > 0 ? (double)black / total : 1;
        Console.WriteLine($"pixels: total={total} black={blackRatio:P0} gold={gold} white={white} lines={lineCount}");

        if (blackRatio > 0.85)
        {
            _report.AddFailure("Karaoke: скриншот", $"Кадр почти чёрный ({blackRatio:P0}) — окно скрыто из захвата " +
                "(HiddenFromCapture не снялся) или лента пустая.");
            return;
        }
        if (lineCount < 1)
        {
            _report.AddStep("Karaoke: строки", null, $"WARN — в ленте нет текста ({lineCount} строк). " +
                "Возможная причина: Show*-фильтры конфига отфильтровали демо-строки предпросмотра.");
            return;
        }
        if (fadeOn)
        {
            if (gold > 0 && white > 0)
                _report.AddStep("Karaoke: цвета (пиксели)", null,
                    $"OK — золото найдено (свежая строка, {gold}px), белые старшие строки найдены ({white}px), строк: {lineCount}.");
            else
                _report.AddFailure("Karaoke: цвета (пиксели)",
                    $"Цветовая регрессия: золотых пикселей={gold}, белых={white} (строк: {lineCount}). " +
                    "Ожидалось: свежая строка золотая #FFE9A8, старшие белые/приглушённые.");
        }
        else if (gold > 0 || white > 0)
        {
            _report.AddStep("Karaoke: цвета (пиксели)", null,
                $"OK — градиент выключен (AgeColorFade=false), видимый текст есть (gold={gold}px, white={white}px, строк: {lineCount}).");
        }
        else
        {
            _report.AddStep("Karaoke: цвета (пиксели)", null,
                $"WARN — градиент выключен и светлого текста не видно (gold={gold}, white={white}, строк: {lineCount}). " +
                "Если строки есть, но тёмные — у юзера тёмный TextColor на тёмном фоне.");
        }
    }

    private static string ToBase64(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
}
