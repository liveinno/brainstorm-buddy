using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using AutomationElement = FlaUI.Core.AutomationElements.AutomationElement;
using VirtualKeyShort = FlaUI.Core.WindowsAPI.VirtualKeyShort;
using ControlType = FlaUI.Core.Definitions.ControlType;

static class N
{
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint PW_RENDERFULLCONTENT = 2;
}

class Shots
{
    static string OutDir = "";
    static string ExePath = "";
    static string InstallerPath = "";
    static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrainstormBuddy", "config.json");
    static readonly string ConfigBackup = ConfigPath + ".manualshots.bak";
    static Process? AppProc;
    static bool WeLaunchedApp;
    static int AppPid = -1;

    static readonly (string File, string Header)[] Tabs = new[]
    {
        ("03-tab-api.png", "API"),
        ("04-tab-llm.png", "LLM"),
        ("05-tab-audio.png", "Аудио"),
        ("06-tab-file.png", "Транскрибация файла"),
        ("07-tab-stt.png", "Распознавание речи"),
        ("08-tab-overlay.png", "Оверлей"),
        ("09-tab-hotkeys.png", "Горячие клавиши"),
        ("10-tab-about.png", "О приложении"),
        ("11-tab-summary.png", "Резюме"),
        ("12-tab-agents.png", "Агенты"),
        ("13-tab-diag.png", "Диагностика"),
    };

    [STAThread]
    static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) OutDir = Path.GetFullPath(args[++i]);
            if (args[i] == "--exe" && i + 1 < args.Length) ExePath = args[++i];
            if (args[i] == "--installer" && i + 1 < args.Length) InstallerPath = args[++i];
            if (args[i] == "--only" && i + 1 < args.Length) Only = args[++i];
        }
        if (OutDir == "") OutDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "manuals", "screenshots", "bb"));
        Directory.CreateDirectory(OutDir);
        if (ExePath == "") ExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BrainstormBuddy", "BrainstormBuddy.exe");
        N.SetProcessDpiAwarenessContext(new IntPtr(-4));

        try { RunAppShots(); } catch (Exception ex) { Console.WriteLine("APP-SHOTS FAILED: " + ex); }
        try { if (File.Exists(ConfigBackup)) File.Move(ConfigBackup, ConfigPath, true); } catch { }
        if (Want("installer") && InstallerPath != "" && File.Exists(InstallerPath))
        {
            try { RunInstallerShots(); } catch (Exception ex) { Console.WriteLine("INSTALLER-SHOTS FAILED: " + ex); }
        }
        Console.WriteLine("DONE");
        return 0;
    }

    static string? Only;
    static bool Want(string name) => Only == null || Only.Split(',').Contains(name);

    static void RunAppShots()
    {
        var existing = Process.GetProcessesByName("BrainstormBuddy");
        if (existing.Length > 0)
        {
            AppProc = existing[0];
            AppPid = AppProc.Id;
            Console.WriteLine("attached pid=" + AppPid);
        }
        else
        {
            if (!File.Exists(ExePath)) { Console.WriteLine("exe missing: " + ExePath); return; }
            if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigBackup, true);
            var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false };
            psi.Environment["BUDDY_CAPTURE_VISIBLE"] = "1";
            AppProc = Process.Start(psi)!;
            AppPid = AppProc.Id;
            WeLaunchedApp = true;
            Console.WriteLine("launched pid=" + AppPid);
            Thread.Sleep(8000);
        }

        using var uia = new UIA3Automation();
        // оверлей мог быть скрыт хоткеем в прошлом прогоне — сначала пробуем найти, иначе тоглим видимость
        var overlay = WaitAppWindow(uia, "Overlay", 15) ?? WaitAppWindow(uia, "BrainstormBuddy", 8);
        if (overlay == null)
        {
            SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_H);
            Thread.Sleep(1500);
            overlay = WaitAppWindow(uia, "Overlay", 20) ?? WaitAppWindow(uia, "BrainstormBuddy", 10);
        }
        if (overlay == null) { Console.WriteLine("overlay window not found"); return; }
        Thread.Sleep(1500);
        if (Want("overlay")) Shot(overlay, "01-overlay-idle.png", 48);

        if (Want("answer")) TryOverlayAnswer(uia, overlay);

        // прячем оверлей, чтобы не мешал
        SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_H);
        Thread.Sleep(700);

        if (Want("tabs"))
        {
            SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_S);
            var settings = WaitAppWindow(uia, "Настройки", 15);
            if (settings != null)
            {
                Thread.Sleep(1500);
                var tabItems = settings.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem));
                Console.WriteLine("tabitems: " + tabItems.Length);
                foreach (var t in Tabs)
                {
                    try
                    {
                        var item = tabItems.FirstOrDefault(x => SafeName(x).Contains(t.Header));
                        if (item == null) { Console.WriteLine("no tab " + t.Header); continue; }
                        var sel = item.Patterns.SelectionItem.PatternOrDefault;
                        if (sel != null) sel.Select(); else item.Click();
                        Thread.Sleep(800);
                        Shot(settings, t.File, 24);
                        if (t.File == "06-tab-file.png") TryFileTranscription(uia, settings);
                    }
                    catch (Exception e) { Console.WriteLine("tab " + t.Header + ": " + e.Message); }
                }
                CloseWindow(settings);
                Thread.Sleep(600);
            }
            else Console.WriteLine("settings window not found");
        }

        if (Want("log"))
        {
            SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_L);
            var log = WaitAppWindow(uia, "Логи", 10);
            if (log != null)
            {
                Thread.Sleep(1200);
                Shot(log, "15-log-window.png", 24);
                CloseWindow(log);
            }
            else Console.WriteLine("log window not found");
        }

        if (Want("tray")) TryTrayMenu(uia);
        if (WeLaunchedApp) GracefulExitViaTray(uia);
    }

    static void TryOverlayAnswer(UIA3Automation uia, AutomationElement overlay)
    {
        try
        {
            var edit = overlay.FindFirstDescendant(cf => cf.ByAutomationId("ChatInputBox"))
                       ?? overlay.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).FirstOrDefault();
            if (edit == null) { Console.WriteLine("no overlay edit"); return; }
            const string q = "Привет! Ответь одним коротким предложением.";
            try { edit.Patterns.Value.PatternOrDefault?.SetValue(q); }
            catch { }
            var v = edit.Patterns.Value.PatternOrDefault?.Value ?? "";
            if (!v.Contains("Привет"))
            {
                try { edit.Focus(); } catch { }
                Thread.Sleep(300);
                Keyboard.Type(q);
            }
            Thread.Sleep(400);
            var send = overlay.FindFirstDescendant(cf => cf.ByAutomationId("ChatSendBtn"))
                       ?? overlay.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                                 .FirstOrDefault(b => SafeName(b).Contains("Отправ") || (b.AutomationId ?? "").Contains("Send"));
            bool invoked = false;
            if (send != null)
            {
                try { send.Patterns.Invoke.PatternOrDefault?.Invoke(); invoked = true; }
                catch { }
                if (!invoked)
                    try { N.PostMessage(new IntPtr(send.Properties.NativeWindowHandle.Value), 0x00F5, IntPtr.Zero, IntPtr.Zero); invoked = true; } catch { }
            }
            if (!invoked)
            {
                try { edit.Focus(); } catch { }
                Keyboard.Press(VirtualKeyShort.RETURN);
            }
            Console.WriteLine("answer shot: waiting up to 45s for LLM reply...");
            Thread.Sleep(45000);
            Shot(overlay, "02-overlay-answer.png", 48);
        }
        catch (Exception e) { Console.WriteLine("answer: " + e.Message); }
    }

    static string SafeName(AutomationElement el) { try { return el.Name ?? ""; } catch { return ""; } }
    static string SafeAutoId(AutomationElement el) { try { return el.AutomationId ?? ""; } catch { return ""; } }

    static AutomationElement? WaitAppWindow(UIA3Automation uia, string titlePart, int timeoutSec)
    {
        var desktop = uia.GetDesktop();
        var end = DateTime.Now.AddSeconds(timeoutSec);
        while (DateTime.Now < end)
        {
            AutomationElement[] wins;
            try { wins = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)); }
            catch { Thread.Sleep(400); continue; }
            foreach (var w in wins)
            {
                try
                {
                    if (w.Properties.ProcessId.Value != AppPid) continue;
                    if (SafeName(w).Contains(titlePart)) return w;
                }
                catch { }
            }
            Thread.Sleep(400);
        }
        return null;
    }

    static void TryFileTranscription(UIA3Automation uia, AutomationElement settings)
    {
        try
        {
            var btns = settings.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            foreach (var b in btns)
            {
                if (SafeName(b).Contains("ранскрибац"))
                {
                    try { b.Patterns.Invoke.PatternOrDefault?.Invoke(); } catch { b.Click(); }
                    Thread.Sleep(2500);
                    var w = WaitAppWindow(uia, "Транскрибация", 8);
                    if (w != null)
                    {
                        Thread.Sleep(700);
                        Shot(w, "14-file-transcription.png", 24);
                        CloseWindow(w);
                    }
                    else Console.WriteLine("file transcription window not found");
                    return;
                }
            }
            Console.WriteLine("no transcription button in settings");
        }
        catch (Exception e) { Console.WriteLine("filetranscription: " + e.Message); }
    }

    static void TryTrayMenu(UIA3Automation uia)
    {
        AutomationElement? menu = null;
        try
        {
            var icon = FindTrayIcon(uia);
            if (icon == null) { Console.WriteLine("tray icon not found"); return; }
            var rc = icon.BoundingRectangle;
            Mouse.RightClick(new Point((int)(rc.Left + rc.Width / 2), (int)(rc.Top + rc.Height / 2)));
            Thread.Sleep(1500);
            var desktop = uia.GetDesktop();
            menu = desktop.FindFirstDescendant(cf => cf.ByControlType(ControlType.Menu));
            if (menu != null) Shot(menu, "16-tray-menu.png", 16);
            else Console.WriteLine("tray menu not found");
        }
        catch (Exception e) { Console.WriteLine("tray: " + e.Message); }
        finally
        {
            try { Keyboard.Press(VirtualKeyShort.ESCAPE); } catch { }
        }
        _ = menu;
    }

    static void GracefulExitViaTray(UIA3Automation uia)
    {
        try
        {
            if (AppProc == null || AppProc.HasExited) return;
            var icon = FindTrayIcon(uia);
            if (icon == null) { Console.WriteLine("exit: no tray icon, leaving app running"); return; }
            var rc = icon.BoundingRectangle;
            Mouse.RightClick(new Point((int)(rc.Left + rc.Width / 2), (int)(rc.Top + rc.Height / 2)));
            Thread.Sleep(1200);
            var desktop = uia.GetDesktop();
            var exit = desktop.FindFirstDescendant(cf => cf.ByName("Выход"));
            if (exit != null)
            {
                exit.Click();
                AppProc.WaitForExit(8000);
                Console.WriteLine("app exited via tray: " + AppProc.HasExited);
            }
            else { Console.WriteLine("exit item not found; leaving app running"); Keyboard.Press(VirtualKeyShort.ESCAPE); }
        }
        catch (Exception e) { Console.WriteLine("exit: " + e.Message); }
    }

    static AutomationElement? FindTrayIcon(UIA3Automation uia)
    {
        var desktop = uia.GetDesktop();
        var hosts = new List<AutomationElement>();
        try
        {
            foreach (var w in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            {
                try
                {
                    var cls = w.ClassName;
                    if (cls is "Shell_TrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
                        or "Windows.UI.Core.CoreWindow") hosts.Add(w);
                }
                catch { }
            }
        }
        catch { }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            foreach (var host in hosts)
            {
                try
                {
                    foreach (var el in host.FindAllDescendants())
                        if (SafeName(el).Contains("Brainstorm") || SafeName(el).Contains("Buddy")) return el;
                }
                catch { }
            }
            if (attempt < 2)
            {
                try
                {
                    var chev = desktop.FindFirstDescendant(cf => cf.ByAutomationId("1502"))
                               ?? desktop.FindFirstDescendant(cf => cf.ByName("Показать скрытые значки"))
                               ?? desktop.FindFirstDescendant(cf => cf.ByName("Show hidden icons"));
                    if (chev != null) { try { chev.Click(); } catch { } Thread.Sleep(1200); }
                }
                catch { }
            }
        }
        return null;
    }

    static void RunInstallerShots()
    {
        Process.Start(new ProcessStartInfo(InstallerPath) { UseShellExecute = true });
        using var uia = new UIA3Automation();

        // кастомные MsgBox из [Code] (.iss): reinstall/upgrade — окно «Установка», кнопки Pane id 6/7
        var dlg = WaitAnyWindow(uia, "Установка", 15)
                  ?? WaitAnyWindow(uia, "язык", 8)
                  ?? WaitAnyWindow(uia, "Language", 8);
        for (int step = 0; step < 2 && dlg != null; step++)
        {
            Thread.Sleep(700);
            Shot(dlg, step == 0 ? "20-installer-lang.png" : "20-installer-prompt.png", 24);
            var btns = dlg.FindAllDescendants(cf => cf.ByControlType(ControlType.Pane));
            var yes = btns.FirstOrDefault(b => SafeAutoId(b) == "6")
                      ?? btns.FirstOrDefault(b => SafeName(b).Trim() is "OK" or "ОК" or "Да");
            if (yes != null)
                N.PostMessage(new IntPtr(yes.Properties.NativeWindowHandle.Value), 0x00F5, IntPtr.Zero, IntPtr.Zero);
            else
            {
                var ok = dlg.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                            .FirstOrDefault(b => SafeName(b).Trim() is "OK" or "ОК" or "Да");
                if (ok != null) N.PostMessage(new IntPtr(ok.Properties.NativeWindowHandle.Value), 0x00F5, IntPtr.Zero, IntPtr.Zero);
            }
            Thread.Sleep(2000);
            dlg = WaitAnyWindow(uia, "Установка", 4) ?? WaitAnyWindow(uia, "язык", 2);
        }
        var wiz = WaitAnyWindow(uia, "BrainstormBuddy", 12);
        if (wiz != null)
        {
            Thread.Sleep(700);
            Shot(wiz, "21-installer-welcome.png", 24);
            // отмена установки
            var cancel = wiz.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                            .FirstOrDefault(b => SafeName(b).Contains("Отмена") || SafeName(b).Contains("Cancel"));
            if (cancel != null)
            {
                try { cancel.Patterns.Invoke.PatternOrDefault?.Invoke(); }
                catch { N.PostMessage(new IntPtr(cancel.Properties.NativeWindowHandle.Value), 0x00F5, IntPtr.Zero, IntPtr.Zero); }
                Thread.Sleep(800);
                var confirm = WaitAnyWindow(uia, "тмен", 5);
                if (confirm != null)
                {
                    var yes = confirm.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                                     .FirstOrDefault(b => SafeName(b).StartsWith("Д") || SafeName(b).StartsWith("Y"));
                    if (yes != null)
                        N.PostMessage(new IntPtr(yes.Properties.NativeWindowHandle.Value), 0x00F5, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        else Console.WriteLine("welcome wizard not found");
    }

    static AutomationElement? WaitAnyWindow(UIA3Automation uia, string titlePart, int timeoutSec)
    {
        var desktop = uia.GetDesktop();
        var end = DateTime.Now.AddSeconds(timeoutSec);
        while (DateTime.Now < end)
        {
            AutomationElement[] wins;
            try { wins = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)); }
            catch { Thread.Sleep(400); continue; }
            foreach (var w in wins)
            {
                try { if (SafeName(w).Contains(titlePart)) return w; } catch { }
            }
            Thread.Sleep(400);
        }
        return null;
    }

    static void SendHotkey(params VirtualKeyShort[] keys)
    {
        foreach (var k in keys.Take(keys.Length - 1)) Keyboard.Press(k);
        Keyboard.Press(keys[^1]);
        foreach (var k in keys.Take(keys.Length - 1).Reverse()) Keyboard.Release(k);
        Thread.Sleep(400);
    }

    static void CloseWindow(AutomationElement w)
    {
        try
        {
            var p = w.Patterns.Window.PatternOrDefault;
            if (p != null) { p.Close(); return; }
        }
        catch { }
        try { N.PostMessage(new IntPtr(w.Properties.NativeWindowHandle.Value), 0x0010, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    static void Shot(AutomationElement el, string file, int margin)
    {
        try
        {
            var hwnd = new IntPtr(el.Properties.NativeWindowHandle.Value);
            N.GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 10 || h < 10) { Console.WriteLine("bad rect " + file); return; }
            using var win = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(win))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = N.PrintWindow(hwnd, hdc, N.PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!ok) { Console.WriteLine("PrintWindow failed " + file); }
            }
            // белый холст с полями — «окно на белом фоне»
            using var canvas = new Bitmap(w + margin * 2, h + margin * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White);
                g.DrawImage(win, margin, margin, w, h);
            }
            canvas.Save(Path.Combine(OutDir, file), ImageFormat.Png);
            Console.WriteLine("shot " + file + $" {w + margin * 2}x{h + margin * 2}");
        }
        catch (Exception e) { Console.WriteLine("shot failed " + file + ": " + e.Message); }
    }
}
