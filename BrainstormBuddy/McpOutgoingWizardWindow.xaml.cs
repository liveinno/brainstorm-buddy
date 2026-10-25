using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Mcp.Outgoing;

namespace BrainstormBuddy;

/// <summary>
/// Мастер «Добавить инструменты в чат»: один экран — имя, транспорт
/// (stdio/HTTP), доверие, токен (HTTP). Кнопка «Проверить» — реальное
/// соединение через общий <see cref="McpClientManager"/> приложения (тот же,
/// что у чата): введённый, ещё не сохранённый токен подставляется через
/// SetSecretOverride, поэтому проверка и рабочий клиент авторизуются
/// одинаково. «Добавить»/«Сохранить» пишет <see cref="McpOutgoingServer"/>
/// в конфиг, а токен — в защищённое хранилище (в конфиг уходит только
/// ссылка SecretRef).
/// </summary>
public partial class McpOutgoingWizardWindow : Window
{
    /// <summary>Таймаут проверки/перечитывания инструментов, с.</summary>
    private const int ProbeTimeoutSeconds = 15;

    private readonly AppConfig _config;
    /// <summary>Правка существующего подключения (null — новое).</summary>
    private readonly McpOutgoingServer? _existing;
    private readonly Func<McpOutgoingServer, string?>? _resolveSecret;
    private readonly Func<McpOutgoingServer, Task>? _onSaved;
    /// <summary>Id сервера: у правки — прежний, у нового — сгенерированный.</summary>
    private readonly string _serverId;
    /// <summary>Ссылка на секрет в защищённом хранилище (в конфиг — только она).</summary>
    private readonly string _secretRef;
    /// <summary>Защита от повторного клика: проверка/сохранение не параллелятся.</summary>
    private bool _busy;

    public McpOutgoingWizardWindow(AppConfig config, McpOutgoingServer? existing = null,
        Func<McpOutgoingServer, string?>? resolveSecret = null,
        Func<McpOutgoingServer, Task>? onSaved = null)
    {
        InitializeComponent();
        _config = config;
        _existing = existing;
        _resolveSecret = resolveSecret;
        _onSaved = onSaved;
        _serverId = existing?.Id ?? Guid.NewGuid().ToString("N")[..12];
        _secretRef = !string.IsNullOrEmpty(existing?.SecretRef)
            ? existing!.SecretRef
            : "out-" + Guid.NewGuid().ToString("N")[..12];

        if (existing != null)
        {
            // Режим правки: подставляем поля, токен НЕ показываем — в поле только новый ввод.
            TitleText.Text = T("L_McpO_EditTitle", "Правка подключения");
            SaveBtn.Content = T("L_McpO_Save", "Сохранить");
            NameBox.Text = existing.Name;
            TransportCombo.SelectedIndex = string.Equals(existing.Transport, "http", StringComparison.OrdinalIgnoreCase)
                ? 1 : 0;
            CommandBox.Text = existing.Command;
            ArgsBox.Text = existing.Args;
            UrlBox.Text = existing.Url;
            TrustCombo.SelectedIndex = TrustIndex(existing.Trust);
            EnabledCheck.IsChecked = existing.Enabled;
            // Старый токен остаётся в хранилище — сообщаем об этом явно.
            if (!string.IsNullOrEmpty(SafeReveal(existing)))
                TokenSavedText.Visibility = Visibility.Visible;
        }
        else
        {
            TitleText.Text = T("L_McpO_Title", "Добавить инструменты в чат");
        }

        // Высота ≤85% рабочей области (спека §7).
        MaxHeight = Math.Min(700, SystemParameters.WorkArea.Height * 0.85);
    }

    private static string T(string key, string fallback) =>
        Application.Current.TryFindResource(key) as string ?? fallback;

    private void OnTransportChanged(object sender, SelectionChangedEventArgs e)
    {
        // ComboBox с SelectedIndex в XAML стреляет SelectionChanged ещё до того,
        // как привязаны StdioPanel/HttpPanel — без этого guard'а окно падает в ctor.
        if (!IsInitialized)
            return;
        var stdio = (TransportCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "http";
        StdioPanel.Visibility = stdio ? Visibility.Visible : Visibility.Collapsed;
        HttpPanel.Visibility = stdio ? Visibility.Collapsed : Visibility.Visible;
    }

    private static int TrustIndex(string trust) => trust switch
    {
        "own" => 0,
        "trusted" => 1,
        _ => 2,
    };

    /// <summary>Собирает сервер из полей формы (в конфиг на этом шаге ничего не пишем).</summary>
    private McpOutgoingServer Collect()
    {
        var transport = (TransportCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "stdio";
        return new McpOutgoingServer
        {
            Id = _serverId,
            Name = NameBox.Text.Trim(),
            Transport = transport,
            Command = CommandBox.Text.Trim(),
            Args = ArgsBox.Text.Trim(),
            Url = UrlBox.Text.Trim(),
            Trust = (TrustCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "untrusted",
            Enabled = EnabledCheck.IsChecked == true,
            SecretRef = _secretRef,
        };
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Статус: обычный текст — приглушённый, ошибка — ErrorBrush темы.</summary>
    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "ErrorBrush" : "TextMutedBrush");
    }

    /// <summary>
    /// Валидация полей до подключения: дружелюбное сообщение вместо
    /// сырого ex.Message от транспорта («Command cannot be null or empty» и т.п.).
    /// </summary>
    private bool Validate(out McpOutgoingServer srv)
    {
        srv = Collect();
        if (string.IsNullOrWhiteSpace(srv.Name))
        {
            SetStatus(T("L_McpO_NoName", "Введите имя сервера"), isError: true);
            return false;
        }
        if (srv.Transport == "http")
        {
            if (!Uri.TryCreate(srv.Url, UriKind.Absolute, out _))
            {
                SetStatus(T("L_McpO_BadUrl", "Укажите корректный URL сервера (https://…)"), isError: true);
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(srv.Command))
        {
            SetStatus(T("L_McpO_NoCommand", "Укажите команду запуска MCP-сервера"), isError: true);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Токен, с которым пойдёт проверка: введённый в мастере, а если поле пусто
    /// и это правка — ранее сохранённый (из защищённого хранилища).
    /// </summary>
    private string ResolveToken()
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0 && _existing != null)
            token = SafeReveal(_existing) ?? string.Empty;
        return token;
    }

    /// <summary>Раскрытие секрета не должно ронять мастер (хранилище может быть недоступно).</summary>
    private string? SafeReveal(McpOutgoingServer srv)
    {
        if (_resolveSecret == null) return null;
        try { return _resolveSecret(srv); }
        catch (Exception ex)
        {
            App.Current.Logger?.Warn($"MCP outgoing: secret reveal failed: {ex.Message}", "Mcp");
            return null;
        }
    }

    /// <summary>
    /// «Проверить»: подключение через ОБЩИЙ менеджер приложения — тот же,
    /// что работает в чате. Токен подставляется через SetSecretOverride,
    /// поэтому проверка проверяет ровно тот же запрос авторизации.
    /// </summary>
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!Validate(out var srv)) return;
        if (!_config.Mcp.Enabled)
        {
            SetStatus(T("L_McpO_Disabled", "MCP выключен — включите на странице."), isError: true);
            return;
        }
        _busy = true;
        TestBtn.IsEnabled = false;
        SaveBtn.IsEnabled = false;

        var token = ResolveToken();
        var tokenEmpty = token.Length == 0;
        SetStatus(T("L_McpO_Testing", "Подключаюсь…"));

        var shared = App.Current.McpOutgoing;
        McpClientManager? temporary = null;
        try
        {
            if (shared == null)
            {
                // Общего менеджера нет — временный клиент с тем же токеном.
                temporary = new McpClientManager(_ => token, App.Current.Logger);
                shared = temporary;
            }
            else if (!tokenEmpty)
            {
                shared.SetSecretOverride(srv.Id, token);
            }
            else
            {
                shared.ClearSecretOverride(srv.Id);
            }

            // Кеш соединения сбрасываем: проверка всегда идёт с текущим токеном.
            await shared.DisconnectAsync(srv);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            var tools = await shared.ListToolsAsync(srv, cts.Token);
            var names = tools.Select(t => t.OriginalName).ToList();
            SetStatus(string.Format(T("L_McpO_ToolsOk", "Подключено. Инструментов: {0} — {1}"),
                names.Count,
                names.Count > 5 ? string.Join(", ", names.Take(5)) + "…" : string.Join(", ", names)));
        }
        catch (Exception ex)
        {
            // Деталь исключения — в журнале (McpClientManager уже пишет свой Warn),
            // пользователю — локализованная причина.
            App.Current.Logger?.Warn($"MCP test «{srv.Name}» failed: {ex.Message}", "Mcp");
            SetStatus(DescribeFailure(ex, tokenEmpty), isError: true);
        }
        finally
        {
            if (temporary != null)
            {
                try { await temporary.DisposeAsync(); } catch { /* клиент уже не нужен */ }
            }
            else
            {
                shared?.ClearSecretOverride(srv.Id);
            }
            _busy = false;
            TestBtn.IsEnabled = true;
            SaveBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Точная причина сбоя: 401 / 403 / сервер недоступен / таймаут —
    /// по классификации исходящих MCP-ошибок.
    /// </summary>
    private string DescribeFailure(Exception ex, bool tokenEmpty)
    {
        string text;
        // Спец-случаи транспорта stdio и некорректного URL — они не про HTTP-коды.
        if (ex is UriFormatException or ArgumentNullException)
        {
            text = T("L_McpO_BadUrl", "Укажите корректный URL сервера (https://…)");
        }
        else if (ex is System.ComponentModel.Win32Exception or FileNotFoundException
            or DirectoryNotFoundException)
        {
            text = T("L_McpO_CmdNotFound",
                "Не удалось запустить команду — проверьте путь и аргументы сервера.");
        }
        else
        {
            var info = McpOutgoingErrorInfo.Classify(ex, ProbeTimeoutSeconds);
            text = info.Kind switch
            {
                McpOutgoingErrorKind.Unauthorized => T("L_McpO_Unauthorized",
                    "Токен отклонён сервером (401) — проверьте токен"),
                McpOutgoingErrorKind.Forbidden => T("L_McpO_Forbidden", "Доступ запрещён (403)"),
                McpOutgoingErrorKind.Unreachable => string.Format(
                    T("L_McpO_Unreachable", "Сервер недоступен: {0}"), info.Detail),
                McpOutgoingErrorKind.Timeout => string.Format(
                    T("L_McpO_TimeoutMsg", "Сервер не ответил за {0} с"), ProbeTimeoutSeconds),
                // Прочие HTTP-статусы (404/500 и т.п.) — с кодом, чтобы владелец
                // видел, что ответ сервера получен, но не тот.
                McpOutgoingErrorKind.Protocol => info.HttpStatus is int httpStatus
                    ? T("L_McpO_ConnFail",
                        "Не удалось подключиться к серверу — подробности в журнале.") + $" (HTTP {httpStatus})"
                    : T("L_McpO_ConnFail",
                        "Не удалось подключиться к серверу — подробности в журнале."),
                _ => MentionsAuth(ex)
                    ? T("L_McpO_Unauthorized", "Токен отклонён сервером (401) — проверьте токен")
                    : T("L_McpO_ConnFail",
                        "Не удалось подключиться к серверу — подробности в журнале."),
            };
        }
        if (tokenEmpty)
            text += " " + T("L_McpO_TokenEmpty", "Токен не задан — сервер может ответить 401");
        return text;
    }

    /// <summary>
    /// Некоторые транспорты прячут HTTP-код в тексте исключения — ищем
    /// упоминание 401/403 по цепочке inner-исключений.
    /// </summary>
    private static bool MentionsAuth(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var msg = e.Message;
            if (string.IsNullOrEmpty(msg)) continue;
            if (msg.Contains("401", StringComparison.Ordinal)
                || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("403", StringComparison.Ordinal)
                || msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// «Добавить»/«Сохранить»: секрет — в защищённое хранилище, сервер — в
    /// конфиг (правка сохраняет прежний Id), соединение сбрасывается,
    /// инструменты перечитываются уже с сохранённым секретом.
    /// </summary>
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!Validate(out var srv)) return;
        _busy = true;
        TestBtn.IsEnabled = false;
        SaveBtn.IsEnabled = false;
        try
        {
            await SaveAsync(srv);
        }
        catch (Exception ex)
        {
            App.Current.Logger?.Error("MCP outgoing save failed", ex, "Mcp");
            SetStatus(T("L_McpW_SaveFail",
                "Не удалось сохранить подключение — подробности в журнале."), isError: true);
        }
        finally
        {
            _busy = false;
            TestBtn.IsEnabled = true;
            SaveBtn.IsEnabled = true;
        }
    }

    /// <summary>Сохранение подключения: секрет — в хранилище, сервер — в конфиг.</summary>
    private async Task SaveAsync(McpOutgoingServer srv)
    {
        var token = TokenBox.Password.Trim();

        // 1. Секрет — в DPAPI-хранилище; в конфиг уходит только ссылка SecretRef.
        if (token.Length > 0)
        {
            try
            {
                var store = App.Current.McpPermissions
                    ?? throw new InvalidOperationException("MCP secret store недоступен");
                store.StoreSecret(srv.SecretRef, token);
            }
            catch (Exception ex)
            {
                App.Current.Logger?.Error("MCP outgoing: secret store failed", ex, "Mcp");
                SetStatus(T("L_McpW_SaveFail",
                    "Не удалось сохранить подключение — подробности в журнале."), isError: true);
                return;
            }
        }

        // 2. Конфиг: правка существующей записи (тот же Id) или добавление новой.
        if (_existing != null)
        {
            ApplyTo(_existing, srv);
            srv = _existing;
        }
        else if (!_config.Mcp.OutgoingServers.Any(s => s.Id == srv.Id))
        {
            _config.Mcp.OutgoingServers.Add(srv);
        }
        App.Current.SaveConfigSafe();

        // 3. Старое соединение сбрасываем, инструменты перечитываем.
        var failed = false;
        var manager = App.Current.McpOutgoing;
        if (manager != null)
        {
            try
            {
                await manager.DisconnectAsync(srv);
                var count = await ReloadToolsAsync(manager, srv, token);
                SetStatus(string.Format(T("L_McpO_SavedOk", "Сохранено. Инструментов: {0}"), count));
            }
            catch (Exception ex)
            {
                App.Current.Logger?.Warn($"MCP outgoing save «{srv.Name}»: {ex.Message}", "Mcp");
                SetStatus(DescribeFailure(ex, token.Length == 0), isError: true);
                failed = true;
            }
        }

        // 4. Колбэк вызывающего окна (сброс соединения/обновление списка).
        if (_onSaved != null)
        {
            try { await _onSaved(srv); }
            catch (Exception ex)
            {
                App.Current.Logger?.Warn($"MCP outgoing onSaved failed: {ex.Message}", "Mcp");
            }
        }

        if (failed)
        {
            // Окно остаётся открытым: токен/URL можно поправить и сохранить снова.
            return;
        }
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Перечитывает инструменты после сохранения. Сначала штатный путь менеджера
    /// (тот же, что у чата); если менеджер не нашёл свежесохранённый секрет —
    /// повтор с явно переданным токеном.
    /// </summary>
    private static async Task<int> ReloadToolsAsync(
        McpClientManager manager, McpOutgoingServer srv, string token)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            var tools = await manager.ListToolsAsync(srv, cts.Token);
            return tools.Count;
        }
        catch when (token.Length > 0)
        {
            manager.SetSecretOverride(srv.Id, token);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
                var tools = await manager.ListToolsAsync(srv, cts.Token);
                return tools.Count;
            }
            finally { manager.ClearSecretOverride(srv.Id); }
        }
    }

    /// <summary>Переносит правку полей в существующую запись конфига (прочие поля не трогаем).</summary>
    private static void ApplyTo(McpOutgoingServer target, McpOutgoingServer edited)
    {
        target.Name = edited.Name;
        target.Transport = edited.Transport;
        target.Command = edited.Command;
        target.Args = edited.Args;
        target.Url = edited.Url;
        target.Trust = edited.Trust;
        target.Enabled = edited.Enabled;
        target.SecretRef = edited.SecretRef;
    }
}
