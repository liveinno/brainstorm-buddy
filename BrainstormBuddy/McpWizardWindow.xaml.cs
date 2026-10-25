using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Expander = System.Windows.Controls.Expander;
using Orientation = System.Windows.Controls.Orientation;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Mcp;

namespace BrainstormBuddy;

/// <summary>
/// Мастер «Подключить AI-агент к Buddy»: 4 шага из MCP-SPEC §4.1 —
/// приложение → доступ → подключение → проверка. Результат: запись
/// McpConnectionEntry в config.Mcp.Connections + токен-файл через
/// McpPermissionStore (DPAPI-стор). Технические параметры прячутся
/// в «Дополнительно»; «Назад» не теряет ввод.
/// </summary>
public partial class McpWizardWindow : Window
{
    private readonly AppConfig _config;
    private readonly McpPermissionStore _store;
    private readonly string _entryId = Guid.NewGuid().ToString("N")[..12];
    private int _step;

    // Шаг 1: приложение.
    private ComboBox? _clientCombo;
    private RadioButton? _hereRadio;
    private RadioButton? _cloudRadio;
    // Шаг 2: доступ.
    private ComboBox? _scopeCombo;
    private CheckBox? _readCheck;
    private CheckBox? _addFactCheck;
    private CheckBox? _changeCheck;
    private CheckBox? _deleteCheck;
    private CheckBox? _memoryCheck;
    private TextBlock? _rightsHint;
    // Шаг 3: подключение.
    private TextBox? _nameBox;
    private TextBlock? _configPreview;
    private Button? _autoBtn;
    private Button? _copyBtn;
    // Шаг 4: проверка.
    private TextBlock? _verifyText;

    private string? _issuedToken;
    private string? _generatedConfigJson;
    private string TokenPath => _store.TokenFilePath(_entryId);
    /// <summary>
    /// Путь хоста, попадающий в конфиг клиента: постоянный путь установленной
    /// копии (иначе — exe рядом с приложением), а не рабочая папка сборки.
    /// </summary>
    private static string HostExePath => McpClientConfigExporter.ResolveHostExePath();

    public McpWizardWindow(AppConfig config, bool startAtCloud)
    {
        InitializeComponent();
        _config = config;
        _store = new McpPermissionStore(App.Current.AppDataDir);
        _step = 0;
        _cloudPrefill = startAtCloud;
        // Высота ≤85% рабочей области (спека §7): SizeToContent ограничен MaxHeight.
        MaxHeight = Math.Min(760, SystemParameters.WorkArea.Height * 0.85);
        RenderStep();
    }

    private readonly bool _cloudPrefill;

    private static string T(string key, string fallback) =>
        Application.Current.TryFindResource(key) as string ?? fallback;

    private void RenderStep()
    {
        StepHost.Children.Clear();
        WizardStatus.Text = "";
        WizardStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        BackBtn.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        // «Отмена» на промежуточных шагах; на последнем «Готово» закрывает окно.
        CancelBtn.Visibility = _step <= 2 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Content = _step switch
        {
            0 or 1 => T("L_McpW_Continue", "Продолжить"),
            2 => T("L_McpW_Finish", "Подключить"),
            _ => T("L_McpW_Done", "Готово"),
        };
        switch (_step)
        {
            case 0: BuildAppStep(); break;
            case 1: BuildAccessStep(); break;
            case 2: BuildConnectStep(); break;
            case 3: BuildVerifyStep(); break;
        }
        // После build: радио _cloudRadio ещё не существовало до BuildAppStep —
        // обновлять видимость облачной карточки нужно ПОСЛЕ создания радио.
        UpdateCloudPanelVisibility();
    }

    /// <summary>Облачная карточка видна только на шаге 1 при выборе «В облаке».</summary>
    private void UpdateCloudPanelVisibility()
    {
        if (CloudStepPanel == null) return;
        CloudStepPanel.Visibility =
            _step == 0 && _cloudRadio?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshCloudPanel(); // состояние/кнопка облачного шага (McpWizardWindow.Cloud.cs)
    }

    /// <summary>Заголовок шага — named-стили типографики Foreground не задают
    /// (implicit-стиль TextBlock они затеняют), цвет — явно из темы.</summary>
    private TextBlock H(string text)
    {
        var h = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("SectionTitleText"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        h.SetResourceReference(TextBlock.ForegroundProperty, "TextBrightBrush");
        return h;
    }

    private void BuildAppStep()
    {
        WizardTitle.Text = T("L_McpW_T1", "Шаг 1 из 4 — Приложение");
        StepHost.Children.Add(H(T("L_McpW_App", "Какой агент подключаем?")));

        // ComboBoxItem, а не строки: implicit-стиль ComboBoxItem даёт тёмные пункты.
        _clientCombo = new ComboBox { Height = 36, Margin = new Thickness(0, 0, 0, 20) };
        _clientCombo.Items.Add(new ComboBoxItem { Content = T("L_McpW_ClientDesktop", "Claude Desktop (этот компьютер)") });
        _clientCombo.Items.Add(new ComboBoxItem { Content = T("L_McpW_ClientCloud", "Claude (облако)") });
        _clientCombo.Items.Add(new ComboBoxItem { Content = T("L_McpW_Other", "Другой агент") });
        _clientCombo.SelectedIndex = _cloudPrefill ? 1 : 0;
        StepHost.Children.Add(_clientCombo);

        _hereRadio = new RadioButton { Content = T("L_McpW_Here", "На этом компьютере"), IsChecked = !_cloudPrefill };
        _cloudRadio = new RadioButton { Content = T("L_McpW_Cloud", "В облаке"), IsChecked = _cloudPrefill };
        _hereRadio.Checked += (s, e) => UpdateCloudPanelVisibility();
        _hereRadio.Unchecked += (s, e) => UpdateCloudPanelVisibility();
        StepHost.Children.Add(_hereRadio);
        StepHost.Children.Add(_cloudRadio);
    }

    private void BuildAccessStep()
    {
        WizardTitle.Text = T("L_McpW_T2", "Шаг 2 из 4 — Доступ");
        StepHost.Children.Add(H(T("L_McpW_Scope", "Что видно агенту?")));

        _scopeCombo = new ComboBox { Height = 36, Margin = new Thickness(0, 0, 0, 20) };
        _scopeCombo.Items.Add(new ComboBoxItem { Content = T("L_McpW_ScopeAll", "Все проекты, включая новые") });
        _scopeCombo.Items.Add(new ComboBoxItem { Content = T("L_McpW_ScopeExisting", "Только существующие проекты") });
        _scopeCombo.Items.Add(new ComboBoxItem { Content = T("L_McpW_ScopeMeetings", "Выбранные встречи") });
        _scopeCombo.Items.Add(new ComboBoxItem { Content = T("L_McpW_ScopeEverything", "Всё (проекты + память)") });
        _scopeCombo.SelectedIndex = 0;
        StepHost.Children.Add(_scopeCombo);

        StepHost.Children.Add(H(T("L_McpW_Rights", "Что разрешено?")));
        _readCheck = new CheckBox { Content = T("L_McpW_Read", "Чтение данных"), IsChecked = true };
        _addFactCheck = new CheckBox { Content = T("L_McpW_Add", "Добавление фактов") };
        _changeCheck = new CheckBox { Content = T("L_McpW_Change", "Изменение данных") };
        _deleteCheck = new CheckBox { Content = T("L_McpW_Delete", "Удаление (всегда с подтверждением)") };
        _memoryCheck = new CheckBox { Content = T("L_McpW_Memory", "Включать память"), IsChecked = true };
        StepHost.Children.Add(_readCheck);
        StepHost.Children.Add(_addFactCheck);
        StepHost.Children.Add(_changeCheck);
        StepHost.Children.Add(_deleteCheck);
        StepHost.Children.Add(_memoryCheck);

        // Подсказка про пустой набор прав: показывается, когда владелец
        // снял все галочки (связь без полномочий — осознанный выбор).
        _rightsHint = new TextBlock
        {
            Style = (Style)FindResource("BodyText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _rightsHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        StepHost.Children.Add(_rightsHint);
        foreach (var cb in new[] { _readCheck, _addFactCheck, _changeCheck, _deleteCheck })
        {
            cb.Checked += (_, _) => UpdateRightsHint();
            cb.Unchecked += (_, _) => UpdateRightsHint();
        }
        UpdateRightsHint();
    }

    /// <summary>Подсказка шага 2: пустой набор прав сохраняется (без тихого
    /// добавления "read"), поэтому предупреждаем явно.</summary>
    private void UpdateRightsHint()
    {
        if (_rightsHint == null) return;
        var any = _readCheck?.IsChecked == true || _addFactCheck?.IsChecked == true
                  || _changeCheck?.IsChecked == true || _deleteCheck?.IsChecked == true;
        _rightsHint.Text = any ? "" : T("L_McpW_NoRights",
            "Ни одного права не выбрано: подключение сохранится без полномочий — "
            + "агент не сможет читать и изменять данные.");
        _rightsHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildConnectStep()
    {
        WizardTitle.Text = T("L_McpW_T3", "Шаг 3 из 4 — Подключение");
        StepHost.Children.Add(H(T("L_McpW_Name", "Название подключения")));
        _nameBox = new TextBox { Height = 36, Margin = new Thickness(0, 0, 0, 20), Text = DefaultName() };
        StepHost.Children.Add(_nameBox);

        // JSON-конфиг живёт в свёрнутом «Дополнительно»: раскрытие
        // прокручивает контент, не выталкивая кнопки (спека §7).
        var adv = new Expander { Header = T("L_McpW_Advanced", "Дополнительно"), Margin = new Thickness(0, 0, 0, 16) };
        _configPreview = new TextBlock { Style = (Style)FindResource("CodeText"), TextWrapping = TextWrapping.Wrap };
        _configPreview.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        adv.Content = new Border
        {
            Style = (Style)FindResource("JsonPanel"),
            Margin = new Thickness(0),
            Child = _configPreview,
        };
        StepHost.Children.Add(adv);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _autoBtn = new Button
        {
            Content = T("L_McpW_Auto", "Настроить автоматически"),
            Height = 36, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0),
        };
        _autoBtn.Click += OnAutoConfigure;
        btnRow.Children.Add(_autoBtn);

        _copyBtn = new Button
        {
            Content = T("L_McpW_Copy", "Скопировать конфиг"),
            Height = 36, Padding = new Thickness(12, 0, 12, 0),
        };
        _copyBtn.Click += (s, e) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(_generatedConfigJson ?? "");
                WizardStatus.Text = T("L_McpW_Copied", "Скопировано");
                WizardStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            }
            catch (Exception ex)
            {
                App.Current.Logger?.Warn($"MCP wizard: clipboard copy failed: {ex.Message}", "Mcp");
                SetWizardError(T("L_McpW_CopyFail", "Не удалось скопировать конфиг в буфер обмена."));
            }
        };
        btnRow.Children.Add(_copyBtn);
        StepHost.Children.Add(btnRow);

        GeneratePreview();
    }

    private void BuildVerifyStep()
    {
        WizardTitle.Text = T("L_McpW_T4", "Шаг 4 из 4 — Проверка");
        _verifyText = new TextBlock { Style = (Style)FindResource("BodyText"), TextWrapping = TextWrapping.Wrap };
        _verifyText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        StepHost.Children.Add(_verifyText);
        RunVerify();
    }

    private string DefaultName()
    {
        var idx = _clientCombo?.SelectedIndex ?? 0;
        // Пустое имя не показываем как технический «Agent» — локализованный дефолт.
        return idx switch
        {
            0 => "Claude Desktop",
            1 => "Claude Cloud",
            _ => T("L_McpW_DefaultAgent", "Мой агент"),
        };
    }

    private void GeneratePreview()
    {
        // Конфиг с путём к token-файлу — сам токен выпускается при «Подключить».
        _generatedConfigJson = McpClientConfigExporter.BuildConfigJson(HostExePath, TokenPath);
        if (_configPreview != null) _configPreview.Text = _generatedConfigJson;
    }

    private void OnAutoConfigure(object sender, RoutedEventArgs e)
    {
        WizardStatus.Text = T("L_McpW_Writing", "Настраиваю…");
        WizardStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        try
        {
            // Токен нужен сейчас — файл должен существовать до записи конфига клиента.
            EnsureTokenIssued();
            var res = McpClientConfigExporter.ApplyToClaudeDesktop(HostExePath, TokenPath);
            WizardStatus.Text = res;
        }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"MCP wizard: auto-configure failed: {ex}", "Mcp");
            SetWizardError(T("L_McpW_AutoFail",
                "Не удалось настроить автоматически — скопируйте конфиг вручную."));
        }
    }

    /// <summary>Ошибка в статус-строке: пользователь видит дружелюбный текст, детали — в логе.</summary>
    private void SetWizardError(string friendly)
    {
        WizardStatus.Text = friendly;
        WizardStatus.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 0) { _step--; RenderStep(); }
    }

    /// <summary>«Отмена»/крестик не создают подключение — entry в конфиг не писался.</summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0: case 1:
                _step++; RenderStep(); break;
            case 2:
                if (CreateConnection()) { _step = 3; RenderStep(); }
                break;
            default:
                DialogResult = true; break;
        }
    }

    /// <summary>Выпуск токена идемпотентен по connectionId: повтор = перевыпуск.</summary>
    private void EnsureTokenIssued()
    {
        if (_issuedToken != null) return;
        var entry = CollectEntry();
        _issuedToken = _store.Issue(entry);
    }

    private McpConnectionEntry CollectEntry()
    {
        var rights = new System.Collections.Generic.List<string>();
        if (_readCheck?.IsChecked == true) rights.Add("read");
        if (_addFactCheck?.IsChecked == true) rights.Add("add-fact");
        if (_changeCheck?.IsChecked == true) rights.Add("change");
        if (_deleteCheck?.IsChecked == true) rights.Add("delete");
        // Пустой набор прав сохраняется как есть: молча добавлять "read" —
        // скрытое расширение полномочий. Связь без прав бесполезна, но
        // безопасно, и пользователь видит это в подсказке шага 2.

        var scope = _scopeCombo?.SelectedIndex switch
        {
            1 => "existing-projects",
            2 => "selected-meetings",
            3 => "all",
            _ => "all-projects",
        };

        return new McpConnectionEntry
        {
            Id = _entryId,
            Name = string.IsNullOrWhiteSpace(_nameBox?.Text) ? DefaultName() : _nameBox.Text.Trim(),
            Trust = _cloudRadio?.IsChecked == true ? "trusted-remote" : "own",
            ClientKind = _clientCombo?.SelectedIndex == 0 ? "claude-desktop" : "other",
            Scope = scope,
            Rights = rights,
            MemoryIncluded = _memoryCheck?.IsChecked == true,
        };
    }

    private bool CreateConnection()
    {
        try
        {
            var entry = CollectEntry();
            if (entry.Rights.Count == 0)
            {
                App.Current.Logger?.Warn(
                    $"MCP wizard: connection '{entry.Name}' created without rights", "Mcp");
            }
            EnsureTokenIssued();
            _config.Mcp.Connections.RemoveAll(c => c.Id == entry.Id);
            _config.Mcp.Connections.Add(entry);
            if (_cloudRadio?.IsChecked == true) _config.Mcp.TunnelMode = "tailscale";
            return true;
        }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"MCP wizard: create connection failed: {ex}", "Mcp");
            SetWizardError(T("L_McpW_SaveFail",
                "Не удалось сохранить подключение — подробности в журнале."));
            return false;
        }
    }

    /// <summary>
    /// Проверка без живого клиента: токен-файл существует, грант резолвится
    /// в это же подключение — то, что McpHost сделает на первом коннекте.
    /// </summary>
    private void RunVerify()
    {
        if (_verifyText == null) return;
        try
        {
            var fileOk = File.Exists(TokenPath);
            var resolved = _issuedToken != null
                ? _store.Resolve(_issuedToken, _config.Mcp.Connections)
                : null;
            // Спека §6.1: «Подключено» — только после реального обращения
            // клиента (LastSeenUtc). Токен+грант — лишь готовая настройка.
            _verifyText.Text = fileOk && resolved?.Id == _entryId
                ? T("L_McpW_Ok", "Настройка готова. Ожидаем подключение агента.")
                : T("L_McpW_Warn", "Хост ещё не запущен — включится после сохранения настроек.");
        }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"MCP wizard: verify failed: {ex}", "Mcp");
            _verifyText.Text = T("L_McpW_VerifyFail",
                "Не удалось проверить настройку — подробности в журнале.");
            _verifyText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        }
    }
}
