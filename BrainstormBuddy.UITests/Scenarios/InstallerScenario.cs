using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrainstormBuddy.UITests.Infrastructure;
using BrainstormBuddy.UITests.Reporting;

namespace BrainstormBuddy.UITests.Scenarios;

/// <summary>
/// Гоняет РЕАЛЬНЫЙ GUI инсталлятора Inno Setup через FlaUI: кликает кнопки мастера
/// как живой пользователь (язык → welcome → лицензия → папка → сброс конфига →
/// компоненты → задачи → Install → Finish), снимает скриншот каждой страницы,
/// затем проверяет установленные файлы и так же через GUI удаляет приложение.
///
/// Никаких /VERYSILENT — установка идёт через видимый мастер, тестер сам жмёт
/// каждую кнопку, поэтому ни один диалог не «зависает» на экране без ответа.
/// Дев-конфиг (%APPDATA%\BrainstormBuddy\config.json) бэкапится до и восстанавливается
/// после, чтобы тест сброса не потёр реальные настройки разработчика.
/// </summary>
public class InstallerScenario
{
    private readonly string _setupPath;
    private readonly UIA3Automation _automation;

    // Заголовки кнопок мастера/диалогов (RU + EN, без ампersand-ускорителей).
    // ВАЖНО: «Установить только для меня» жмём в приоритете; «для всех пользователей»
    // НИКОГДА не жмём — это UAC-эскалация, после неё FlaUI не управляет поднятым окном.
    private static readonly string[] ForMeCaptions = { "только для меня", "for me", "just for me" };
    private static readonly string[] AllUsersCaptions = { "для всех", "all users", "anyone using" };
    private static readonly string[] FinishCaptions = { "завершить", "finish" };
    private static readonly string[] InstallCaptions = { "установить", "install" };
    private static readonly string[] NextCaptions = { "далее", "next" };
    private static readonly string[] OkCaptions = { "ок", "ok" };
    private static readonly string[] YesCaptions = { "да", "yes" };
    private static readonly string[] CancelCaptions = { "отмена", "cancel", "нет", "no", "назад", "back" };

    public InstallerScenario(string setupPath, UIA3Automation automation)
    {
        _setupPath = setupPath;
        _automation = automation;
    }

    public async Task<bool> RunAsync(VisionClient? vision, HtmlReportBuilder report)
    {
        bool ok = true;
        var variant = Path.GetFileName(_setupPath);
        report.AddStep($"Инсталлятор: {variant}", null,
            $"Файл: {_setupPath}\nРазмер: {SizeH(_setupPath)}\nРежим: установка через видимый GUI-мастер (FlaUI кликает кнопки).");

        // 0) Бэкап дев-конфига — сброс на странице мастера уносит его в Документы и удаляет.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var devCfg = Path.Combine(appData, "BrainstormBuddy", "config.json");
        var devCfgBak = Path.Combine(Path.GetTempPath(), "bb-uitest-devcfg.json");
        bool hadDevCfg = File.Exists(devCfg);
        if (hadDevCfg) { File.Copy(devCfg, devCfgBak, true); Log($"Дев-конфиг сохранён: {devCfg} → {devCfgBak}"); }
        var docsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BrainstormBuddy");
        int backupsBefore = CountBackups(docsDir);

        // 1) Запуск мастера и прокликивание страниц.
        Log($"Запускаю мастер: {_setupPath}");
        Process setup;
        try { setup = Process.Start(new ProcessStartInfo(_setupPath) { UseShellExecute = true })!; }
        catch (Exception ex) { report.AddFailure("Запуск инсталлятора", ex.Message); return false; }

        try
        {
            await DriveWizardAsync(setup, vision, report, isUninstall: false);
        }
        catch (Exception ex)
        {
            report.AddFailure("Проведение мастера установки", ex.Message + "\n" + ex.StackTrace);
            ok = false;
        }

        // Ждём завершения процесса установки (распаковка ~1 ГБ моделей).
        WaitProcessExit(setup, TimeSpan.FromMinutes(4));

        // 2) Проверка установленных файлов.
        var installDir = FindInstallDir();
        if (installDir == null)
        {
            report.AddFailure("Файлы после установки", "Не найден каталог установки (искал в LocalAppData\\Programs и Program Files).");
            ok = false;
        }
        else
        {
            Log($"Каталог установки: {installDir}");
            var required = new (string rel, string label)[]
            {
                ("BrainstormBuddy.exe", "приложение"),
                ("models/v2_ctc.onnx", "модель GigaAM (ONNX)"),
                ("models/labels.json", "лейблы GigaAM"),
                ("THIRD-PARTY-NOTICES.txt", "лицензии зависимостей"),
                ("unins000.exe", "деинсталлятор"),
            };
            var lines = new List<string>();
            foreach (var (rel, label) in required)
            {
                var p = Path.Combine(installDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) lines.Add($"✓ {rel,-34} {SizeH(p)}  ({label})");
                else { lines.Add($"✗ НЕТ: {rel}  ({label})"); ok = false; }
            }
            // Whisper — только в Full-варианте.
            var whisper = Path.Combine(installDir, "models", "ggml-large-v3-turbo-q5_0.bin");
            bool isFull = variant.Contains("Full", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(whisper)) lines.Add($"✓ {"models/ggml-large-v3-turbo-q5_0.bin",-34} {SizeH(whisper)}  (Whisper, Full)");
            else if (isFull) { lines.Add("✗ НЕТ: models/ggml-large-v3-turbo-q5_0.bin (ожидался в Full!)"); ok = false; }
            else lines.Add("· Whisper отсутствует — ожидаемо для Lite");

            report.AddStep("Проверка файлов установки", CaptureDir(installDir), string.Join("\n", lines));
        }

        // 3) Проверка сброса конфига (по умолчанию выбран пункт 0 — сброс с бэкапом в Документы).
        int backupsAfter = CountBackups(docsDir);
        var resetLines = new List<string>
        {
            $"Бэкапов конфига в «Документы\\BrainstormBuddy»: было {backupsBefore}, стало {backupsAfter}",
        };
        if (hadDevCfg)
        {
            if (backupsAfter > backupsBefore) resetLines.Add("✓ Сброс сработал: старый конфиг сохранён в резервную копию.");
            else resetLines.Add("✗ Ожидался новый бэкап конфига после сброса — не появился.");
            if (!File.Exists(devCfg)) resetLines.Add("✓ Старый config.json удалён (приложение создаст свежие дефолты).");
            else resetLines.Add("· config.json на месте (мог быть пересоздан приложением — проверьте).");
        }
        else resetLines.Add("· Дев-конфига до установки не было — сброс проверять не на чем.");
        report.AddStep("Проверка сброса конфига (бэкап в Документы)", null, string.Join("\n", resetLines));

        // 4) Удаление через GUI деинсталлятора.
        if (installDir != null && !await UninstallAsync(installDir, vision, report)) ok = false;

        // 5) Подчистка: убить возможный запущенный экземпляр и восстановить дев-конфиг.
        foreach (var p in Process.GetProcessesByName("BrainstormBuddy")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        if (hadDevCfg && File.Exists(devCfgBak))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(devCfg)!);
            File.Copy(devCfgBak, devCfg, true);
            report.AddStep("Восстановление дев-конфига", null, $"✓ {devCfg} восстановлен из резервной копии.");
        }

        report.AddStep($"Итог по {variant}", null, ok ? "✓ Установка → проверка → удаление прошли чисто." : "✗ Есть замечания — см. шаги выше.");
        return ok;
    }

    /// <summary>Установка со СБРОСОМ всех настроек, ОСТАВЛЯЕТ приложение установленным.
    /// Дев-конфиг НЕ восстанавливается — сброс должен «прилипнуть» (старый конфиг installer
    /// сам бэкапит в «Документы»). Не удаляет и не запускает приложение.</summary>
    public async Task<bool> InstallKeepAsync(VisionClient? vision, HtmlReportBuilder report)
    {
        bool ok = true;
        var variant = Path.GetFileName(_setupPath);
        report.AddStep($"Установка со сбросом (оставить): {variant}", null,
            $"Файл: {_setupPath}\nРазмер: {SizeH(_setupPath)}\nРежим: GUI-мастер, сброс всех настроек, приложение остаётся установленным.");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var devCfg = Path.Combine(appData, "BrainstormBuddy", "config.json");
        var docsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BrainstormBuddy");
        int backupsBefore = CountBackups(docsDir);
        bool hadDevCfg = File.Exists(devCfg);

        // Закрыть работающий экземпляр, иначе апгрейд не заменит файлы.
        foreach (var p in Process.GetProcessesByName("BrainstormBuddy")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        foreach (var p in Process.GetProcessesByName("_unins")) { try { p.Kill(); } catch { } }

        // Убрать ОСИРОТЕВШИЙ каталог прошлых прерванных прогонов (файлы есть, но ARP-ключа нет):
        // иначе Inno на странице папки спросит модальное «Папка уже существует, ставить в неё?»,
        // которое ломает автопрокликивание. Чистая папка → мастер идёт без этого диалога.
        var stale = FindInstallDir();
        if (stale != null)
        {
            try { Directory.Delete(stale, true); Log($"Убран осиротевший каталог установки: {stale}"); }
            catch (Exception ex) { Log($"Не смог убрать {stale}: {ex.Message}"); }
        }

        Log($"Запускаю мастер (install-keep): {_setupPath}");
        Process setup;
        try { setup = Process.Start(new ProcessStartInfo(_setupPath) { UseShellExecute = true })!; }
        catch (Exception ex) { report.AddFailure("Запуск инсталлятора", ex.Message); return false; }

        try { await DriveWizardAsync(setup, vision, report, isUninstall: false); }
        catch (Exception ex) { report.AddFailure("Проведение мастера установки", ex.Message + "\n" + ex.StackTrace); ok = false; }

        WaitProcessExit(setup, TimeSpan.FromMinutes(4));

        var installDir = FindInstallDir();
        if (installDir == null)
        {
            report.AddFailure("Файлы после установки", "Каталог установки не найден.");
            return false;
        }
        Log($"Каталог установки: {installDir}");
        var required = new (string rel, string label)[]
        {
            ("BrainstormBuddy.exe", "приложение"),
            ("models/v2_ctc.onnx", "модель GigaAM"),
            ("models/labels.json", "лейблы GigaAM"),
            ("THIRD-PARTY-NOTICES.txt", "лицензии"),
            ("unins000.exe", "деинсталлятор"),
        };
        var lines = new List<string>();
        foreach (var (rel, label) in required)
        {
            var p = Path.Combine(installDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) lines.Add($"✓ {rel,-34} {SizeH(p)}  ({label})");
            else { lines.Add($"✗ НЕТ: {rel}  ({label})"); ok = false; }
        }
        var whisper = Path.Combine(installDir, "models", "ggml-large-v3-turbo-q5_0.bin");
        if (File.Exists(whisper)) lines.Add($"✓ Whisper {SizeH(whisper)}");
        else if (variant.Contains("Full", StringComparison.OrdinalIgnoreCase)) { lines.Add("✗ НЕТ Whisper (ожидался в Full!)"); ok = false; }
        report.AddStep("Проверка файлов установки", null, string.Join("\n", lines));

        int backupsAfter = CountBackups(docsDir);
        var resetLines = new List<string> { $"Бэкапов конфига в «Документы\\BrainstormBuddy»: было {backupsBefore}, стало {backupsAfter}" };
        if (hadDevCfg)
        {
            resetLines.Add(backupsAfter > backupsBefore
                ? "✓ Сброс: старый конфиг сохранён в резервную копию (Документы)."
                : "· бэкап конфига не появился (возможно, не выбрано «Сбросить»).");
            resetLines.Add(!File.Exists(devCfg)
                ? "✓ Старый config.json удалён — приложение создаст свежие дефолты (мульти-агент ВЫКЛ, схема v3)."
                : "· config.json на месте (сброс не применился?).");
        }
        else resetLines.Add("· Дев-конфига до установки не было — сброс проверять не на чем.");
        report.AddStep("Сброс всех настроек", null, string.Join("\n", resetLines));

        report.AddStep($"Итог: {variant}", null, ok
            ? "✓ Установлено со СБРОСОМ настроек. Приложение НЕ удалялось, дев-конфиг НЕ восстанавливался — сброс сохранён."
            : "✗ Есть замечания — см. шаги выше.");
        return ok;
    }

    /// <summary>Удаление уже установленного приложения через GUI деинсталлятора (можно вызвать отдельно).</summary>
    public async Task<bool> UninstallAsync(string installDir, VisionClient? vision, HtmlReportBuilder report)
    {
        var exe = Path.Combine(installDir, "BrainstormBuddy.exe");
        var unins = Path.Combine(installDir, "unins000.exe");
        if (!File.Exists(unins)) { report.AddFailure("Удаление", "unins000.exe не найден — нечем удалять."); return false; }

        // Экземпляр приложения не должен держать файлы.
        foreach (var p in Process.GetProcessesByName("BrainstormBuddy")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }

        Log("Запускаю деинсталлятор через GUI.");
        Process? un;
        try { un = Process.Start(new ProcessStartInfo(unins) { UseShellExecute = true }); }
        catch (Exception ex) { report.AddFailure("Запуск деинсталлятора", ex.Message); return false; }
        if (un == null) { report.AddFailure("Запуск деинсталлятора", "процесс не стартовал"); return false; }

        try { await DriveWizardAsync(un, vision, report, isUninstall: true, doneWhen: () => !File.Exists(exe)); }
        catch (Exception ex) { report.AddFailure("Проведение деинсталлятора", ex.Message); }

        // Ждём фактического исчезновения exe (temp-копия деинсталлятора завершает работу асинхронно).
        for (int i = 0; i < 40 && File.Exists(exe); i++) Thread.Sleep(500);
        // Подчистка возможной зависшей temp-копии деинсталлятора, чтобы окно не осталось.
        foreach (var p in Process.GetProcessesByName("_unins")) { try { p.Kill(); } catch { } }

        bool gone = !File.Exists(exe);
        bool dirGone = !Directory.Exists(installDir) || (Directory.GetFiles(installDir).Length == 0);
        report.AddStep("Проверка удаления", null,
            gone ? $"✓ BrainstormBuddy.exe удалён после деинсталляции.{(dirGone ? " Каталог пуст/удалён." : " (в каталоге остались файлы — проверьте)")}"
                 : "✗ BrainstormBuddy.exe остался после удаления.");
        return gone;
    }

    // ── Прокликивание страниц мастера (общий для установки и удаления). ──
    // doneWhen: признак завершения этапа, проверяется когда окон нет (для удаления — исчезновение exe:
    // деинсталлятор Inno копирует себя в %TEMP% и перезапускается, исходный процесс сразу выходит,
    // поэтому по proc.HasExited судить нельзя).
    private async Task DriveWizardAsync(Process proc, VisionClient? vision, HtmlReportBuilder report, bool isUninstall,
        Func<bool>? doneWhen = null)
    {
        var desktop = _automation.GetDesktop();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(6);
        string lastSig = "";
        int guard = 0, noWin = 0;

        while (DateTime.UtcNow < deadline && guard++ < 120)
        {
            var win = FindWizardWindow(desktop, proc);
            if (win == null)
            {
                // Окон нет: сначала проверяем признак завершения (напр. exe удалён).
                if (doneWhen != null && doneWhen()) { Log("Этап завершён (по признаку)."); return; }
                noWin++;
                // Возврат только после устойчивого отсутствия окон (temp-перезапуск деинсталлятора
                // даёт паузу без окна на 1-2с — не бросаем этап рано).
                if (noWin > 12 && (doneWhen == null || proc.HasExited))
                { Log("Окон нет устойчиво — этап завершён."); return; }
                Thread.Sleep(700);
                continue;
            }
            noWin = 0;

            // Диалоги-сообщения (#32770): «Выбор режима установки» (ForMe!), подтверждения (Да/OK).
            var cls = SafeClass(win);
            if (cls == "#32770")
            {
                var db = FindPrimary(win);
                if (db != null) { Log($"Диалог: '{Short(win.Name)}' → жму '{db.Name}'"); ClickElement(db); }
                else if (ClickCaption(win, ForMeCaptions) || ClickCaption(win, YesCaptions) || ClickCaption(win, OkCaptions))
                    Log($"Диалог: '{Short(win.Name)}' → клик по тексту (фолбэк).");
                else Log($"Диалог: '{Short(win.Name)}' → кнопка не найдена (пропуск).");
                Thread.Sleep(700);
                continue;
            }

            // Страница мастера: снять «Запустить приложение», принять лицензию.
            UncheckLaunch(win);
            SelectAccept(win);

            // Скриншот один раз на новую страницу.
            var sig = PageSignature(win);
            if (sig != lastSig)
            {
                lastSig = sig;
                var b64 = TryCapture(win);
                string fb = "(vision отключён — скриншот приложен)";
                if (vision != null && b64 != null)
                {
                    try
                    {
                        fb = await vision.AskAboutImageAsync(b64,
                            "Ты QA-инженер. На скриншоте — страница мастера установки Windows (Inno Setup). " +
                            "Отвечай на русском. Начни с 'OK' если все надписи читаемы (нет каракуль/битой кодировки), " +
                            "затем 1-2 фразой опиши, что это за страница мастера и какие видны кнопки. " +
                            "Если видишь нечитаемый текст — процитируй его дословно после 'ОШИБКА КОДИРОВКИ:'.");
                    }
                    catch (Exception ex) { fb = "(vision ошибка: " + ex.Message + ")"; }
                }
                report.AddStep($"{(isUninstall ? "Удаление" : "Установка")}: {Short(sig)}", b64, fb);
            }

            // Приоритет: «только для меня» → Finish → Install → Next → OK → Yes.
            var btn = FindPrimary(win);
            if (btn == null)
            {
                // Кнопки нет вовсе (страница прогресса — активна только Cancel). Ждём.
                Thread.Sleep(800);
                continue;
            }

            var caption = (btn.Name ?? "").ToLowerInvariant();
            Log($"Страница '{Short(sig)}' → жму '{btn.Name}'");
            ClickElement(btn);

            if (FinishCaptions.Any(c => caption.Contains(c)))
            {
                Log("Нажат Finish — мастер завершён.");
                Thread.Sleep(800);
                return;
            }
            Thread.Sleep(900);
        }
        Log("Достигнут лимит шагов мастера.");
    }

    // ── Поиск окна мастера среди верхнеуровневых окон рабочего стола. ──
    private AutomationElement? FindWizardWindow(AutomationElement desktop, Process proc)
    {
        AutomationElement[] tops;
        try { tops = desktop.FindAllChildren(); } catch { return null; }
        // Имя-стемы (в нижнем регистре) любого окна инсталлятора: мастер, диалог языка,
        // «Выбор режима установки», деинсталлятор, подтверждения.
        string[] stems = { "brainstormbuddy", "setup", "устан", "режим", "язык", "language", "удален", "uninstall" };
        AutomationElement? candidate = null;
        foreach (var w in tops)
        {
            string name, cls;
            try { name = (w.Name ?? "").ToLowerInvariant(); cls = SafeClass(w); } catch { continue; }
            if (cls == "#32770") // диалог подтверждения/сообщения
            {
                if (stems.Any(s => name.Contains(s)) || FindButton(w, YesCaptions) != null || FindButton(w, OkCaptions) != null)
                    return w;
                continue;
            }
            // Любое окно (VCL-форма ИЛИ диалог загрузчика) с узнаваемым именем инсталлятора.
            // Класс НЕ фильтруем: окно выбора режима рисует загрузчик Inno, а не форма мастера.
            if (stems.Any(s => name.Contains(s))) candidate ??= w;
        }
        return candidate;
    }

    private AutomationElement? FindButton(AutomationElement win, string[] captions)
    {
        AutomationElement[] btns;
        try { btns = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)); } catch { return null; }
        foreach (var b in btns)
        {
            string n;
            try { if (!b.IsEnabled) continue; n = (b.Name ?? "").Replace("&", "").Trim().ToLowerInvariant(); } catch { continue; }
            if (string.IsNullOrEmpty(n)) continue;
            if (CancelCaptions.Any(c => n == c)) continue;
            if (AllUsersCaptions.Any(c => n.Contains(c))) continue; // никогда не жмём «для всех» (UAC)
            if (captions.Any(c => n.Contains(c))) return b;
        }
        return null;
    }

    // Основная кнопка окна по приоритету: только-для-меня → Finish → Install → Next → OK → Yes.
    private AutomationElement? FindPrimary(AutomationElement win) =>
        FindButton(win, ForMeCaptions) ?? FindButton(win, FinishCaptions) ?? FindButton(win, InstallCaptions)
        ?? FindButton(win, NextCaptions) ?? FindButton(win, OkCaptions) ?? FindButton(win, YesCaptions);

    private static void ClickElement(AutomationElement e)
    {
        try { e.AsButton().Invoke(); return; } catch { }
        try { e.Patterns.Invoke.Pattern.Invoke(); return; } catch { }
        try { e.Click(); } catch { }   // фолбэк: физический клик по центру
    }

    // Фолбэк: клик по ЛЮБОМУ элементу (не только Button), чьё имя содержит подпись — через координаты.
    private bool ClickCaption(AutomationElement win, string[] captions)
    {
        AutomationElement[] all;
        try { all = win.FindAllDescendants(); } catch { return false; }
        foreach (var el in all)
        {
            string n;
            try { if (!el.IsEnabled) continue; n = (el.Name ?? "").Replace("&", "").Trim().ToLowerInvariant(); } catch { continue; }
            if (string.IsNullOrEmpty(n)) continue;
            if (CancelCaptions.Any(c => n == c) || AllUsersCaptions.Any(c => n.Contains(c))) continue;
            if (!captions.Any(c => n.Contains(c))) continue;
            ClickElement(el);
            return true;
        }
        return false;
    }

    private void SelectAccept(AutomationElement win)
    {
        try
        {
            var radios = win.FindAllDescendants(cf => cf.ByControlType(ControlType.RadioButton));
            foreach (var r in radios)
            {
                var n = (r.Name ?? "").ToLowerInvariant();
                if (n.Contains("принимаю") || n.Contains("i accept"))
                {
                    var rb = r.AsRadioButton();
                    if (rb != null && rb.IsChecked != true) { rb.IsChecked = true; Log("Лицензия: выбрано «принимаю»."); }
                    return;
                }
            }
        }
        catch { }
    }

    private void UncheckLaunch(AutomationElement win)
    {
        try
        {
            var checks = win.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
            foreach (var c in checks)
            {
                var n = (c.Name ?? "").ToLowerInvariant();
                if (n.Contains("запустить") || n.Contains("launch"))
                {
                    var cb = c.AsCheckBox();
                    if (cb != null && cb.IsChecked == true) { cb.IsChecked = false; Log("Снят флажок «Запустить приложение»."); }
                }
            }
        }
        catch { }
    }

    // ── Поиск каталога установки (PrivilegesRequired=lowest → LocalAppData\Programs). ──
    private static string? FindInstallDir()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BrainstormBuddy"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BrainstormBuddy"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BrainstormBuddy"),
        };
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "BrainstormBuddy.exe")));
    }

    // ── Утилиты ──
    private static string PageSignature(AutomationElement win)
    {
        // Сигнатура страницы = заголовок окна + первый заметный текстовый заголовок страницы.
        try
        {
            var texts = win.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            var head = texts.Select(t => (t.Name ?? "").Trim())
                            .FirstOrDefault(s => s.Length > 6 && s.Length < 80);
            return ((win.Name ?? "") + "|" + (head ?? "")).Trim();
        }
        catch { return win.Name ?? ""; }
    }

    private static string SafeClass(AutomationElement e) { try { return e.ClassName ?? ""; } catch { return ""; } }

    private string? TryCapture(AutomationElement win)
    {
        try
        {
            var img = Capture.Element(win);
            using var ms = new MemoryStream();
            img.Bitmap.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    private static string? CaptureDir(string dir)
    {
        // Для отчёта каталог не скриним — возвращаем null, текст со списком файлов идёт в feedback.
        return null;
    }

    private static void WaitProcessExit(Process p, TimeSpan t)
    {
        try { if (!p.WaitForExit((int)t.TotalMilliseconds)) Log("Процесс не завершился в срок (продолжаю)."); } catch { }
    }

    private static int CountBackups(string docsDir)
    {
        try { return Directory.Exists(docsDir) ? Directory.GetFiles(docsDir, "config.backup-*.json").Length : 0; }
        catch { return 0; }
    }

    private static string SizeH(string path)
    {
        try
        {
            long b = new FileInfo(path).Length;
            string[] u = { "B", "KB", "MB", "GB" };
            double s = b; int i = 0;
            while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
            return $"{s:0.#} {u[i]}";
        }
        catch { return "?"; }
    }

    private static string Short(string s) => s.Length <= 60 ? s : s.Substring(0, 57) + "...";
    private static void Log(string m) => Console.WriteLine("[installer] " + m);
}
