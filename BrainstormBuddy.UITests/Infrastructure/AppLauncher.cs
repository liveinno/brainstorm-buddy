using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System;
using System.IO;

namespace BrainstormBuddy.UITests.Infrastructure;

public class AppLauncher : IDisposable
{
    private readonly Application _app;
    public UIA3Automation Automation { get; }
    
    public AppLauncher(string exePath)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Cannot find EXE at: {exePath}");

        // Приложение single-instance (Global\BrainstormBuddy_SingleInstance): уже запущенная
        // копия (например, установленная) заставит новый процесс показать MessageBox и выйти,
        // FlaUI не найдёт окно и все сценарии упадут. Убиваем экземпляры ДО запуска.
        KillAll();

        _app = Application.Launch(exePath);
        Automation = new UIA3Automation();
    }

    public bool HasExited => _app.HasExited;
    public int ExitCode => _app.ExitCode;

    private static void KillAll()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("BrainstormBuddy"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
    }

    public Window GetMainWindow()
    {
        return _app.GetMainWindow(Automation, TimeSpan.FromSeconds(5));
    }
    
    public Window[] GetAllTopLevelWindows()
    {
        return _app.GetAllTopLevelWindows(Automation);
    }

    public void Dispose()
    {
        try { _app?.Kill(); } catch {}
        try { _app?.Dispose(); } catch {}
        Thread.Sleep(500);
        try { Automation?.Dispose(); } catch {}

        KillAll();
    }
}