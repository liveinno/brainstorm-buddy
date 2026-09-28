using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using System.Drawing.Imaging;
using System.Text;
using BrainstormBuddy.UITests.Infrastructure;
using BrainstormBuddy.UITests.Reporting;
namespace BrainstormBuddy.UITests.Scenarios;

/// <summary>
/// Переключение LLM-провайдеров: выбор в комбо -> BaseUrl автозаполняется ->
/// «Проверить связь» с мусорным ключом -> ConnectionStatusText показывает
/// человекочитаемую ошибку (HumanizeApiError), а не сырое исключение.
/// </summary>
public class ProviderCheckScenario
{
    private readonly HtmlReportBuilder _report;

    public ProviderCheckScenario(HtmlReportBuilder report) => _report = report;

    // Провайдеры для показа: облачные дают 401/человекочитаемую ошибку на плохом ключе,
    // локальные — «connection refused»/нет сервера (тоже читаемо). Custom → открывается редактор.
    private static readonly string[] CloudProviders = { "OpenAI", "OpenRouter", "Groq" };
    private static readonly string[] LocalProviders = { "Ollama (local)", "LM Studio (local)", "LocalAI (local)" };
    private const string BadKey = "sk-uitest-invalid-0000000000";

    public async Task RunAsync(AppLauncher launcher, VisionClient vision, HtmlReportBuilder report)
    {
        var win = FindSettings(launcher, attempts: 5);
        if (win == null)
        {
            var overlay = launcher.GetAllTopLevelWindows()
                .FirstOrDefault(w => w.Title.Contains("Overlay", StringComparison.OrdinalIgnoreCase));
            overlay?.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))?.AsButton()?.Invoke();
            await Task.Delay(1500);
            win = FindSettings(launcher, attempts: 10);
        }
        if (win == null) { report.AddFailure("Providers", "Settings window not found"); return; }

        var tabs = win.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem));
        if (tabs.Length < 1) { report.AddFailure("Providers", "TabControl/пункты не найдены"); return; }
        try { tabs[0].AsTabItem().Select(); } catch { tabs[0].Click(); }
        await Task.Delay(900);

        var combo = win.FindFirstDescendant(cf => cf.ByAutomationId("LlmProviderCombo"))?.AsComboBox();
        if (combo == null) { report.AddFailure("Providers", "LlmProviderCombo не найден"); return; }

        // Снапшот исходного состояния: сценарий мусорит ключом и выбором провайдера,
        // а OnExit сохраняет конфиг → без восстановления отравим каждый след. запуск.
        var originalProvider = combo.SelectedItem?.Name ?? "";
        var keyBox0 = win.FindFirstDescendant(cf => cf.ByAutomationId("ApiKeyBox"))?.AsTextBox();
        var originalKey = keyBox0?.Text ?? "";
        var originalModel = "";
        try { originalModel = win.FindFirstDescendant(cf => cf.ByAutomationId("ChatModelCombo"))?.AsComboBox()?.EditableText ?? ""; }
        catch { /* кастомный стиль: Edit-подконтрол не всегда в UIA-дереве */ }

        var itemNames = combo.Items.Select(i => i.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        report.AddStep("Providers: список", null, $"Всего профилей: {itemNames.Count} — {string.Join(", ", itemNames)}");

        try
        {
            foreach (var name in CloudProviders.Concat(LocalProviders))
            {
                var idx = IndexOfProvider(combo, name);
                if (idx < 0) continue;
                await TestProvider(win, combo, name, BadKey, model: null, expectError: true, report);
            }

            // Реальные провайдеры: env BUDDY_TEST_LLM="ИмяПровайдера|ApiKey|Модель; Имя2|Ключ2|Модель2"
            // (ключи не хардкодим — секреты живут в env на машине запуска).
            var spec = Environment.GetEnvironmentVariable("BUDDY_TEST_LLM") ?? "";
            foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split('|');
                if (parts.Length < 2) continue;
                var model = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null;
                await TestProvider(win, combo, parts[0].Trim(), parts[1].Trim(), model, expectError: false, report);
            }

            // Редактор «Custom»: выбор открывает панель редактирования.
            if (itemNames.Any(n => n.Contains("Custom", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    SelectProvider(combo, itemNames.First(n => n.Contains("Custom", StringComparison.OrdinalIgnoreCase)));
                    await Task.Delay(700);
                    var panel = win.FindFirstDescendant(cf => cf.ByAutomationId("LlmProviderEditPanel"));
                    var visible = panel != null && !panel.Properties.IsOffscreen.ValueOrDefault;
                    report.AddStep("Providers: Custom редактор", Capture(win),
                        visible ? "OK — панель редактирования открылась" : "WARN — выбрали Custom, панель не видна");
                }
                catch (Exception ex) { report.AddFailure("Providers: Custom редактор", ex.Message); }
            }
        }
        finally
        {
            // Восстановление исходного провайдера/ключа/модели — иначе OnExit сохранит мусор.
            try
            {
                if (!string.IsNullOrEmpty(originalProvider))
                {
                    SelectProvider(combo, originalProvider);
                    await Task.Delay(600);
                }
                var kb = win.FindFirstDescendant(cf => cf.ByAutomationId("ApiKeyBox"))?.AsTextBox();
                if (kb != null) { kb.Focus(); kb.Text = originalKey; }
                var mc = win.FindFirstDescendant(cf => cf.ByAutomationId("ChatModelCombo"))?.AsComboBox();
                try
                {
                    if (mc != null && mc.EditableText != originalModel)
                    {
                        mc.EditableText = originalModel;
                        kb?.Focus(); // LostFocus закоммитит Text-биндинг
                    }
                }
                catch { /* кастомный стиль: Edit-подконтрол не всегда доступен — ок */ }
            }
            catch { /* best-effort восстановление */ }
        }
    }

    // Коммит выбора провайдера через SelectionItemPattern (FlaUI Select/Click на
    // привязанном SelectedValue-комбо не дёргают SelectionChanged WPF).
    private static void SelectProvider(ComboBox combo, string providerName)
    {
        combo.Expand();
        Thread.Sleep(400);
        var item = combo.Items.FirstOrDefault(i =>
            i.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));
        item?.Patterns.SelectionItem.PatternOrDefault?.Select();
        Thread.Sleep(200);
    }

    // Индекс item'а по имени — надёжнее Select(name), т.к. UIA-имя может отличаться от Name.
    private static int IndexOfProvider(ComboBox combo, string name)
    {
        for (int i = 0; i < combo.Items.Length; i++)
            if (combo.Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private async Task TestProvider(Window win, ComboBox combo, string providerName,
        string apiKey, string? model, bool expectError, HtmlReportBuilder report)
    {
        try
        {
            SelectProvider(combo, providerName);
            await Task.Delay(700);
            var baseUrl = ReadTextBoxWithUrl(win);
            var selName = combo.SelectedItem?.Name ?? "(null)";

            var keyBox = win.FindFirstDescendant(cf => cf.ByAutomationId("ApiKeyBox"))?.AsTextBox();
            if (keyBox != null) { keyBox.Focus(); keyBox.Text = apiKey; }

            // ChatModelCombo: Text-биндинг на LostFocus — после ввода уводим фокус.
            if (model != null)
            {
                var mc = win.FindFirstDescendant(cf => cf.ByAutomationId("ChatModelCombo"))?.AsComboBox();
                bool setOk = false;
                try { if (mc != null) { mc.Focus(); mc.EditableText = model; setOk = true; } }
                catch { /* кастомный стиль: Edit-подконтрол не всегда в UIA — идём в Edit-дитя */ }
                if (!setOk && mc != null)
                {
                    var edit = mc.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
                    if (edit != null) { edit.Focus(); edit.Text = model; }
                }
                keyBox?.Focus();
            }

            win.FindFirstDescendant(cf => cf.ByAutomationId("CheckConnectionBtn"))?.AsButton()?.Invoke();
            // OnCheckConnection неблокирующий: ждём смены статуса. Реальный ключ + Chat-шаг
            // может тянуть ~20с — 30с таймаут + добивка «Проверяю» ниже.
            string status = await WaitStatus(win, timeoutMs: 30000);
            var shot = Capture(win);

            // Кнопка неблокирующая: если «Проверяю…» не угасла — прошлый вызов ещё жив и
            // перезапишет статус следующего провайдера. Дожидаемся завершения.
            if (status.Contains("Проверяю"))
                status = await WaitStatus(win, timeoutMs: 20000);

            var empty = string.IsNullOrWhiteSpace(status) || status.Contains("Проверяю");
            var failed = status.Contains("✗") || status.Contains("Нет связи");
            string verdict;
            if (expectError)
                verdict = !empty && (failed || status.Contains("Шаг"))
                    ? $"OK — ошибка показана корректно: {status}"
                    : $"WARN — на мусорном ключе нет ошибки: '{status}'";
            else
                verdict = !empty && !failed
                    ? $"OK — связь установлена: {status}"
                    : $"WARN — реальный ключ не прошёл: '{status}'";
            report.AddStep($"Providers: {providerName}{(expectError ? " (бэд-ключ)" : " (реальный ключ)")}", shot,
                $"Выбран='{selName}'; BaseUrl='{baseUrl}'; {verdict}");
        }
        catch (Exception ex)
        {
            report.AddFailure($"Providers: {providerName}", $"Exception: {ex.Message}");
        }
    }

    // Активный BaseUrl — ActiveBaseUrlBox (биндинг _vm.BaseUrl). Fallback: первый Edit с «://».
    private static string ReadTextBoxWithUrl(Window win)
    {
        var named = win.FindFirstDescendant(cf => cf.ByAutomationId("ActiveBaseUrlBox"))?.AsTextBox();
        if (named != null) return named.Text;
        foreach (var e in win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
        {
            var t = e.AsTextBox()?.Text ?? "";
            if (t.Contains("://") || t.Contains("127.0.0.1") || t.Contains("localhost")) return t;
        }
        return "";
    }

    private static async Task<string> WaitStatus(Window win, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var t = win.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionStatusText"))?.Name ?? "";
            if (!string.IsNullOrWhiteSpace(t) && !t.Contains("Проверяю")) return t.ReplaceLineEndings(" | ");
            await Task.Delay(500);
        }
        return win.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionStatusText"))?.Name ?? "";
    }

    private static Window FindSettings(AppLauncher launcher, int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            var w = launcher.GetAllTopLevelWindows().FirstOrDefault(x =>
                x.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).Length >= 5);
            if (w != null) return w;
            Thread.Sleep(800);
        }
        return null!;
    }

    private static string Capture(Window w)
    {
        try
        {
            var bmp = w.Capture();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null!; }
    }
}
