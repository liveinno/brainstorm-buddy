using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BrainstormBuddy.UITests.Infrastructure;
using BrainstormBuddy.UITests.Reporting;

namespace BrainstormBuddy.UITests.Scenarios;

public class LogWindowScenario
{
    private const string UI_CHECK_PROMPT = "ВАЖНО: Отвечай ТОЛЬКО на русском языке! Внимательно проверь: 1) Кодировка — нет ли каракулей, знаков вопроса, странных символов (ОШИБКА КОДИРОВКИ). 2) Контрастность — хорошо ли читается текст на фоне? Нет ли серого на белом или белого на белом? Если контрастность плохая, напиши 'ОШИБКА КОНТРАСТНОСТИ' и опиши проблему. Если всё в порядке, опиши что видишь на экране. ";

    public async Task RunAsync(AppLauncher launcher, VisionClient vision, HtmlReportBuilder report)
    {
        Console.WriteLine("Opening LogWindow via Ctrl+Shift+L...");
        
        Keyboard.Press(VirtualKeyShort.CONTROL);
        Keyboard.Press(VirtualKeyShort.SHIFT);
        Keyboard.Type(VirtualKeyShort.KEY_L);
        Keyboard.Release(VirtualKeyShort.SHIFT);
        Keyboard.Release(VirtualKeyShort.CONTROL);
        
        Thread.Sleep(2000);

        Window logWindow = null;
        var windows = launcher.GetAllTopLevelWindows();
        foreach (var win in windows)
        {
            if (win.Title.Contains("BrainstormBuddy", StringComparison.OrdinalIgnoreCase) && win.Title.Contains("Логи", StringComparison.OrdinalIgnoreCase))
            {
                logWindow = win;
                break;
            }
        }

        if (logWindow == null)
        {
            Console.WriteLine("Could not find LogWindow!");
            report.AddFailure("Log Window", "Could not find LogWindow after pressing Ctrl+Shift+L.");
            return;
        }

        logWindow.Focus();
        Thread.Sleep(1000);

        var base64 = CaptureBase64(logWindow);
        Console.WriteLine("Sending LogWindow screenshot to Vision API...");
        var fb = await vision.AskAboutImageAsync(base64, UI_CHECK_PROMPT + "Это окно логов (LogWindow). Видны ли иконки на кнопках 'Очистить', 'Копировать', 'Открыть папку' внизу? Выглядят ли они ровно и отцентрированно? Как выглядят чекбоксы 'Автопрокрутка' и 'Пауза' с иконками сверху?");
        report.AddStep("LogWindow", base64, fb);

        Keyboard.Press(VirtualKeyShort.ALT);
        Keyboard.Type(VirtualKeyShort.F4);
        Keyboard.Release(VirtualKeyShort.ALT);
        Console.WriteLine("Closed LogWindow.");
    }

    private string CaptureBase64(Window window)
    {
        var image = FlaUI.Core.Capturing.Capture.Element(window);
        using var ms = new MemoryStream();
        image.Bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
}