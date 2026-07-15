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

        _app = Application.Launch(exePath);
        Automation = new UIA3Automation();
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

        var leftover = System.Diagnostics.Process.GetProcessesByName("BrainstormBuddy");
        foreach (var p in leftover)
        {
            try { p.Kill(); p.WaitForExit(3000); } catch {}
        }
    }
}