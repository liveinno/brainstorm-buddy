using System.Windows;
using System.Windows.Controls;

namespace BrainstormBuddy;

/// <summary>
/// Модальный диалог в стиле приложения (тёмная тема через DynamicResource),
/// заменяющий системный MessageBox (бэклог №59). Создаётся только через
/// <see cref="AppMessageBox.Show"/> — конструкторы недоступны снаружи.
/// Кнопки локализованы ключами L_Msg_Ok/Yes/No/Cancel с фолбэком на английский.
/// </summary>
public partial class StyledMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    /// <summary>Выбранная кнопка (читается AppMessageBox.Show после ShowDialog).</summary>
    internal MessageBoxResult Result => _result;

    internal StyledMessageBox(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        Title = caption;
        MessageText.Text = text;
        ApplyIcon(icon);
        BuildButtons(buttons);

        // Результат закрытия крестиком без нажатия кнопок: как у MessageBox —
        // OK-диалог → OK, всё остальное → Cancel.
        _result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
    }

    private void ApplyIcon(MessageBoxImage icon)
    {
        // Алиасы enum (Hand==Error, Exclamation==Warning, Asterisk==Information)
        // сводятся к каноническим именам — поэтому или-слияние по константам.
        var (glyph, brushKey) = icon switch
        {
            MessageBoxImage.Error => ("✕", "ErrorBrush"),
            MessageBoxImage.Warning => ("⚠", "WarnBrush"),
            MessageBoxImage.Question => ("?", "AccentBrush"),
            MessageBoxImage.Information => ("ℹ", "AccentBrush"),
            _ => (null, null)
        };
        if (glyph == null) return;
        IconGlyph.Text = glyph;
        if (brushKey != null)
            IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        IconGlyph.Visibility = Visibility.Visible;
    }

    private void BuildButtons(MessageBoxButton buttons)
    {
        // Порядок и результат — как у MessageBox: Yes/No[/Cancel] и OK[/Cancel].
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("L_Msg_Ok", "OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("L_Msg_Ok", "OK", MessageBoxResult.OK, isDefault: true);
                AddButton("L_Msg_Cancel", "Cancel", MessageBoxResult.Cancel, isCancel: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("L_Msg_Yes", "Yes", MessageBoxResult.Yes, isDefault: true);
                AddButton("L_Msg_No", "No", MessageBoxResult.No);
                break;
            default: // YesNoCancel
                AddButton("L_Msg_Yes", "Yes", MessageBoxResult.Yes, isDefault: true);
                AddButton("L_Msg_No", "No", MessageBoxResult.No);
                AddButton("L_Msg_Cancel", "Cancel", MessageBoxResult.Cancel, isCancel: true);
                break;
        }
    }

    private void AddButton(string resourceKey, string fallback, MessageBoxResult result,
                           bool isDefault = false, bool isCancel = false)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = System.Windows.Application.Current?.TryFindResource(resourceKey) as string ?? fallback,
            MinWidth = 76,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Margin = new Thickness(ButtonsPanel.Children.Count > 0 ? 8 : 0, 0, 0, 0)
        };
        if (isDefault && TryFindResource("PrimaryActionButton") is Style style)
            btn.Style = style;
        btn.Click += (_, _) =>
        {
            _result = result;
            DialogResult = true;
        };
        ButtonsPanel.Children.Add(btn);
    }
}

/// <summary>
/// Статический API по образцу <see cref="System.Windows.MessageBox"/>:
/// диалог в теме приложения вместо системного окна (бэклог №59).
/// </summary>
public static class AppMessageBox
{
    /// <summary>Показать модальный диалог с текстом, заголовком, набором кнопок и иконкой.</summary>
    public static MessageBoxResult Show(Window? owner, string text, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        var dlg = new StyledMessageBox(text, caption, buttons, icon);
        if (owner != null)
        {
            dlg.Owner = owner;
            // Диалог над Topmost-владельцем (оверлей встречи) — owned-окно и так выше
            // владельца, но без Topmost может уйти под чужие topmost-окна.
            dlg.Topmost = owner.Topmost;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // Без владельца, но где-то висит Topmost-оверлей — иначе диалог уйдёт под него.
            dlg.Topmost = System.Windows.Application.Current?.Windows
                .Cast<Window>().Any(w => w.IsVisible && w.Topmost) == true;
        }
        dlg.ShowDialog();
        return dlg.Result;
    }

    /// <summary>Короткая форма без владельца (окно центрируется по экрану).</summary>
    public static MessageBoxResult Show(string text, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
        => Show(null, text, caption, buttons, icon);
}
