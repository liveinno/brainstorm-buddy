using System;
using System.IO;
using System.Threading.Tasks;
using BrainstormBuddy.UITests.Infrastructure;
using BrainstormBuddy.UITests.Reporting;
using BrainstormBuddy.UITests.Scenarios;

namespace BrainstormBuddy.UITests;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Starting BrainstormBuddy UI Tests...");

        string targetTab = null;
        string installSetup = null;
        string installKeepSetup = null;
        string uninstallDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tab" && i + 1 < args.Length)
            {
                targetTab = args[i + 1];
                i++;
            }
            else if (args[i] == "--install" && i + 1 < args.Length)
            {
                installSetup = args[i + 1];
                i++;
            }
            else if (args[i] == "--install-keep" && i + 1 < args.Length)
            {
                installKeepSetup = args[i + 1];
                i++;
            }
            else if (args[i] == "--uninstall-only" && i + 1 < args.Length)
            {
                uninstallDir = args[i + 1];
                i++;
            }
        }

        // ============================================================
        // INSTALL-KEEP MODE: установка со сбросом настроек, приложение
        // ОСТАЁТСЯ установленным (для передачи пользователю на тест).
        // ============================================================
        if (!string.IsNullOrEmpty(installKeepSetup))
        {
            if (!File.Exists(installKeepSetup)) { Console.Error.WriteLine($"Setup not found: {installKeepSetup}"); return 2; }
            var cfgK = File.Exists("config.test.json") ? TestConfig.Load("config.test.json") : new TestConfig();
            var repK = new HtmlReportBuilder();
            VisionClient visK = null;
            try { if (cfgK.VisionProviders is { Count: > 0 }) { var v = new VisionClient(cfgK); visK = await v.PingAllProvidersAsync() ? null : v; } }
            catch { }
            using var autoK = new FlaUI.UIA3.UIA3Automation();
            var instK = new BrainstormBuddy.UITests.Scenarios.InstallerScenario(installKeepSetup, autoK);
            bool okK = false;
            try { okK = await instK.InstallKeepAsync(visK, repK); }
            catch (Exception ex) { repK.AddFailure("Install-keep crashed", ex.Message + "\n" + ex.StackTrace); }
            repK.Save(Path.Combine(Environment.CurrentDirectory, "report_install_keep.html"));
            Console.WriteLine($"Install-keep report saved. ok = {okK}");
            return okK ? 0 : 1;
        }

        // ============================================================
        // UNINSTALL-ONLY MODE: гоняем только GUI-деинсталлятор уже
        // установленного приложения (для отладки удаления без переустановки).
        // ============================================================
        if (!string.IsNullOrEmpty(uninstallDir))
        {
            var cfgU = File.Exists("config.test.json") ? TestConfig.Load("config.test.json") : new TestConfig();
            var repU = new HtmlReportBuilder();
            using var autoU = new FlaUI.UIA3.UIA3Automation();
            var instU = new BrainstormBuddy.UITests.Scenarios.InstallerScenario(uninstallDir, autoU);
            bool okU = false;
            try { okU = await instU.UninstallAsync(uninstallDir, null, repU); }
            catch (Exception ex) { repU.AddFailure("Uninstall crashed", ex.Message + "\n" + ex.StackTrace); }
            repU.Save(Path.Combine(Environment.CurrentDirectory, "report_uninstall.html"));
            Console.WriteLine($"Uninstall report saved. exe gone = {okU}");
            return okU ? 0 : 1;
        }

        // ============================================================
        // INSTALL MODE: гоняем GUI-мастер инсталлятора через FlaUI.
        // Vision — best-effort (если Ollama жив, аннотирует скриншоты;
        // если нет — просто прикладывает скриншоты). НЕ выходим по exit 2.
        // ============================================================
        if (!string.IsNullOrEmpty(installSetup))
        {
            if (!File.Exists(installSetup))
            {
                Console.Error.WriteLine($"Setup not found: {installSetup}");
                return 2;
            }
            var cfg = File.Exists("config.test.json") ? TestConfig.Load("config.test.json") : new TestConfig();
            var rep = new HtmlReportBuilder();
            VisionClient vis = null;
            try
            {
                if (cfg.VisionProviders is { Count: > 0 })
                {
                    var v = new VisionClient(cfg);
                    var dead = await v.PingAllProvidersAsync();
                    vis = dead ? null : v;
                    Console.WriteLine(dead ? "Vision недоступен — скриншоты без аннотаций." : "Vision жив — аннотирую страницы мастера.");
                }
            }
            catch (Exception ex) { Console.WriteLine("Vision ping failed: " + ex.Message + " (продолжаю без vision)"); }

            using var automation = new FlaUI.UIA3.UIA3Automation();
            var installer = new BrainstormBuddy.UITests.Scenarios.InstallerScenario(installSetup, automation);
            bool okInstall = false;
            try { okInstall = await installer.RunAsync(vis, rep); }
            catch (Exception ex) { rep.AddFailure("Installer scenario crashed", ex.Message + "\n" + ex.StackTrace); }

            var installReport = Path.Combine(Environment.CurrentDirectory, "report_install.html");
            rep.Save(installReport);
            Console.WriteLine($"Installer report: {installReport}");
            return okInstall ? 0 : 1;
        }

        if (!string.IsNullOrEmpty(targetTab))
        {
            Console.WriteLine($"Targeting specific tab: {targetTab}");
        }

        var configPath = "config.test.json";
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config not found at {configPath}. Creating default.");
            var defaultConfig = new TestConfig
            {
                ExePath = "..\\BrainstormBuddy\\bin\\Release\\net8.0-windows\\BrainstormBuddy.exe",
                VisionProviders = new System.Collections.Generic.List<ProviderConfig>
                {
                    new ProviderConfig
                    {
                        ApiKey = "YOUR_API_KEY",
                        BaseUrl = "http://127.0.0.1:11434/v1/chat/completions",
                        VisionModel = "qwen2.5vl:7b"
                    }
                }
            };
            File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Please fill it and run again.");
            return 2;
        }

        var config = TestConfig.Load(configPath);
        var report = new HtmlReportBuilder();
        var vision = new VisionClient(config);

        // ============================================================
        // PRE-FLIGHT: ping every provider with a tiny text request
        // If ALL providers are down, abort BEFORE launching the app
        // ============================================================
        Console.WriteLine($"Pre-flight check: pinging {config.VisionProviders.Count} Vision API provider(s)...");
        var allDead = await vision.PingAllProvidersAsync();

        if (allDead)
        {
            var reason = vision.LastFailureReason;
            Console.Error.WriteLine();
            Console.Error.WriteLine("============================================================");
            Console.Error.WriteLine("  FATAL: All Vision API providers are unavailable!");
            Console.Error.WriteLine("============================================================");
            Console.Error.WriteLine();
            Console.Error.WriteLine("The UI tester cannot run without a working Vision API.");
            Console.Error.WriteLine("The UI automation will still run, but every screenshot");
            Console.Error.WriteLine("analysis will fail.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Reasons:");
            Console.Error.WriteLine(reason);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Action for developer / agent:");
            Console.Error.WriteLine("  1. Add a working provider to config.test.json");
            Console.Error.WriteLine("  2. Or wait for rate limits to reset");
            Console.Error.WriteLine("  3. Or set a valid API key in your account");
            Console.Error.WriteLine();

            report.MarkAllProvidersFailed(reason);
            report.AddFailure("Pre-flight: Vision API check", $"All {config.VisionProviders.Count} provider(s) failed. Test aborted before launching the app.\n\n{reason}");
            report.Save(Path.Combine(Environment.CurrentDirectory, "report.html"));
            return 2;
        }

        // ============================================================
        // MAIN: launch app and run scenarios
        // ============================================================
        Console.WriteLine($"Launching {config.ExePath}");
        bool hadUIFailure = false;
        try
        {
            using (var launcher = new AppLauncher(config.ExePath))
            {
                // Главное окно первым: структурная проверка наложений + vision-скрин
                var overlayScenario = new OverlayScenario();
                await overlayScenario.RunAsync(launcher, vision, report);

                // Регресс всех кнопок главного окна (детерминированный, vision не нужен)
                var regress = new OverlayRegressionScenario();
                await regress.RunAsync(launcher, vision, report);

                var settingsScenario = new SettingsWindowScenario(targetTab);
                await settingsScenario.RunAsync(launcher, vision, report);

                // Always run UX audit: hover buttons, check tooltips, both themes
                var uxAudit = new UiUxAuditScenario();
                await uxAudit.RunAsync(launcher, vision, report);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception during test execution: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            hadUIFailure = true;
        }

        // ============================================================
        // RUNTIME CHECK: if a provider died mid-test, mark the report
        // as needing manual review
        // ============================================================
        if (vision.LastCallAllFailed)
        {
            var reason = vision.LastFailureReason;
            Console.Error.WriteLine();
            Console.Error.WriteLine("============================================================");
            Console.Error.WriteLine("  WARNING: All Vision API providers failed during the test.");
            Console.Error.WriteLine("============================================================");
            Console.Error.WriteLine("The tester took screenshots and clicked through the UI,");
            Console.Error.WriteLine("but Vision analysis was not possible. Manual review needed.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Reasons:");
            Console.Error.WriteLine(reason);
            Console.Error.WriteLine();

            if (!report.HasCriticalFailure)
            {
                report.MarkAllProvidersFailed(reason);
                report.AddFailure("Runtime: Vision API", $"All providers failed mid-test. UI automation completed but visual analysis was not done.\n\n{reason}");
            }
            hadUIFailure = true;
        }

        var reportPath = Path.Combine(Environment.CurrentDirectory, "report.html");
        report.Save(reportPath);
        Console.WriteLine($"Done! Report saved to {reportPath}");

        return hadUIFailure ? 1 : 0;
    }
}

