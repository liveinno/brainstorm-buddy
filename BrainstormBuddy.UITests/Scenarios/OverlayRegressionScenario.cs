using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrainstormBuddy.UITests.Infrastructure;
using BrainstormBuddy.UITests.Reporting;

namespace BrainstormBuddy.UITests.Scenarios;

/// <summary>
/// РЕГРЕСС главного окна: прокликивает каждую кнопку оверлея, двигает слайдеры,
/// отправляет секретное сообщение, открывает контекст-меню истории.
/// После каждого действия — скриншот в отчёт + проверка, что приложение живо
/// (не зависло: окно отвечает на UIA-запросы). Ловит класс багов
/// «кнопка молча не работает» и «действие вешает приложение» (дедлок 2026-07-04).
/// Vision-модель тут почти не используется — вердикты детерминированные.
/// </summary>
public class OverlayRegressionScenario
{
    private Window _overlay = null!;
    private HtmlReportBuilder _report = null!;
    private AppLauncher _launcher = null!;

    public async Task RunAsync(AppLauncher launcher, VisionClient vision, HtmlReportBuilder report)
    {
        _launcher = launcher;
        _report = report;
        Console.WriteLine("=== OVERLAY REGRESSION: all buttons ===");

        _overlay = FindOverlay();
        if (_overlay == null) { report.AddFailure("Regress", "Overlay window not found"); return; }

        // Показать окно в захвате экрана на всё время регресса
        _overlay.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_C);
        Thread.Sleep(800);

        try
        {
            // --- Тогглы: клик → скрин → клик обратно ---
            // LLM-пауза убрана из шапки (по редизайну) — теперь только хоткей Ctrl+Shift+Y.
            Step("Пауза LLM (хоткей Ctrl+Shift+Y)", () =>
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_Y);
                Thread.Sleep(400);
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_Y);
            });
            ClickTwice("MicPauseButton", "Пауза аудио (тоггл, микрофон)");
            ClickTwice("AutoScrollButton", "Автоскролл (тоггл)");

            // --- Разовые действия ---
            ClickOnce("LiveSendBtn", "Отправить сейчас (молния)");
            ClickOnce("ExportTransBtn", "Экспорт транскрипта");
            ClickOnce("EraseButton", "Очистить историю");

            // --- Слайдеры: регресс на дедлок при drag (KRIT 2026-07-04) ---
            DragSlider("SilenceSlider", "Слайдер тишины");
            DragSlider("ChunkSlider", "Слайдер чанка");

            // --- Секретное сообщение: ввод + Enter, ждём появление пары в истории ---
            await SendSecretMessage();

            // --- Контекст-меню истории (правый клик) ---
            RightClickHistory();

            // --- Сворачивание эквалайзера ---
            ClickHeaderTwice();

            // --- Настройки: открыть и закрыть ---
            OpenCloseSettings();

            // --- Скрыть/показать через кнопку + хоткей ---
            HideAndRestore();

            // CloakButton намеренно НЕ кликаем: click-through делает окно недоступным
            // для UIA-кликов, обратного пути из автотеста нет. Проверяется вручную.
            _report.AddStep("Regress: CloakButton", null, "SKIPPED намеренно: click-through необратим из автотеста.");
        }
        finally
        {
            // Вернуть скрытие из захвата
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_C);
            Thread.Sleep(300);
        }
    }

    private Window FindOverlay()
    {
        for (int i = 0; i < 10; i++)
        {
            var w = _launcher.GetAllTopLevelWindows()
                .FirstOrDefault(x => x.Title.Contains("Overlay", StringComparison.OrdinalIgnoreCase));
            if (w != null) return w;
            Thread.Sleep(1000);
        }
        return null!;
    }

    /// <summary>Живо ли приложение: окно отвечает на UIA-запрос за 3 секунды.</summary>
    private bool AppResponsive()
    {
        var task = Task.Run(() => { var _ = _overlay.BoundingRectangle; return true; });
        return task.Wait(3000) && task.Result;
    }

    private void Step(string name, Action action)
    {
        try
        {
            action();
            Thread.Sleep(600);
            if (!AppResponsive())
            {
                _report.AddFailure($"Regress: {name}", "ПРИЛОЖЕНИЕ НЕ ОТВЕЧАЕТ после действия (зависание!)");
                return;
            }
            _report.AddStep($"Regress: {name}", CaptureBase64(), "OK — действие выполнено, приложение отвечает.");
            Console.WriteLine($"  [OK] {name}");
        }
        catch (Exception ex)
        {
            _report.AddFailure($"Regress: {name}", $"Exception: {ex.Message}");
            Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
        }
    }

    private Button? FindButton(string automationId) =>
        _overlay.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsButton();

    private void ClickOnce(string id, string name) =>
        Step(name, () =>
        {
            var b = FindButton(id) ?? throw new InvalidOperationException($"кнопка {id} не найдена");
            b.Invoke();
        });

    private void ClickTwice(string id, string name) =>
        Step(name, () =>
        {
            var b = FindButton(id) ?? throw new InvalidOperationException($"кнопка {id} не найдена");
            b.Invoke(); Thread.Sleep(400); b.Invoke();
        });

    private void DragSlider(string id, string name)
    {
        // Слайдеры паузы/чанка видны только в Инженерном режиме. Если он выключен — не FAIL, а SKIP.
        if (_overlay.FindFirstDescendant(cf => cf.ByAutomationId(id)) == null)
        {
            _report.AddStep($"Regress: {name}", null, "SKIPPED: контрол скрыт (Инженерный режим выключен).");
            Console.WriteLine($"  [SKIP] {name} (engineer mode off)");
            return;
        }
        Step(name, () =>
        {
            var s = _overlay.FindFirstDescendant(cf => cf.ByAutomationId(id))!.AsSlider();
            var original = s.Value;
            // серия быстрых изменений — имитация drag (регресс на дедлок)
            for (int i = 1; i <= 6; i++)
            {
                s.Value = s.Minimum + (s.Maximum - s.Minimum) * i / 7.0;
                Thread.Sleep(60);
            }
            s.Value = original;
        });
    }

    private async Task SendSecretMessage()
    {
        Step("Секретное сообщение: ввод и отправка", () =>
        {
            var box = _overlay.FindFirstDescendant(cf => cf.ByAutomationId("ChatInputBox"))?.AsTextBox()
                      ?? throw new InvalidOperationException("ChatInputBox не найден");
            box.Focus();
            box.Text = "регресс-тест: секретное сообщение";
            Keyboard.Type(VirtualKeyShort.ENTER);
        });
        // Пара должна появиться в истории (Sending) почти мгновенно
        await Task.Delay(2500);
        Step("Секретное сообщение: пара появилась в истории", () =>
        {
            var history = _overlay.FindFirstDescendant(cf => cf.ByAutomationId("HistoryList"))
                          ?? throw new InvalidOperationException("HistoryList не найден");
            var texts = history.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text));
            if (!texts.Any(t => (t.Name ?? "").Contains("регресс-тест")))
                throw new InvalidOperationException("отправленный текст не появился в истории");
        });
    }

    private void RightClickHistory()
    {
        Step("Контекст-меню истории (правый клик)", () =>
        {
            var history = _overlay.FindFirstDescendant(cf => cf.ByAutomationId("HistoryList"))
                          ?? throw new InvalidOperationException("HistoryList не найден");
            var item = history.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text));
            (item ?? history).RightClick();
            Thread.Sleep(700);
        });
        // скрин с открытым меню уже снят в Step; закрываем
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
    }

    private void ClickHeaderTwice()
    {
        // Эквалайзер виден только в Инженерном режиме. Выключен — SKIP, а не FAIL.
        var header = _overlay.FindFirstDescendant(cf => cf.ByAutomationId("TimelineHeaderText"));
        if (header == null)
        {
            _report.AddStep("Regress: Эквалайзер: свернуть/развернуть", null, "SKIPPED: эквалайзер скрыт (Инженерный режим выключен).");
            Console.WriteLine("  [SKIP] Эквалайзер (engineer mode off)");
            return;
        }
        Step("Эквалайзер: свернуть/развернуть", () =>
        {
            header.Click(); Thread.Sleep(500);
            header.Click();
        });
    }

    private void OpenCloseSettings()
    {
        Step("Настройки: открыть из оверлея", () =>
        {
            var b = FindButton("SettingsButton") ?? throw new InvalidOperationException("SettingsButton не найден");
            b.Invoke();
            Thread.Sleep(1200);
            var settings = _launcher.GetAllTopLevelWindows()
                .FirstOrDefault(w => w.Title.Contains("Настройки") || w.Title.Contains("Settings"));
            if (settings == null) throw new InvalidOperationException("окно настроек не открылось");
            settings.Close();
        });
    }

    private void HideAndRestore()
    {
        Step("Скрыть кнопкой + вернуть хоткеем Ctrl+Shift+H", () =>
        {
            var b = FindButton("HideButton") ?? throw new InvalidOperationException("HideButton не найден");
            b.Invoke();
            Thread.Sleep(700);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_H);
            Thread.Sleep(700);
            if (!AppResponsive()) throw new InvalidOperationException("оверлей не вернулся после хоткея");
        });
    }

    private string CaptureBase64()
    {
        var image = FlaUI.Core.Capturing.Capture.Element(_overlay);
        using var ms = new MemoryStream();
        image.Bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
}
