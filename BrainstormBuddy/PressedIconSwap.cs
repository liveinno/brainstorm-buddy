using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using RoutedEventArgs = System.Windows.RoutedEventArgs;

namespace BrainstormBuddy;

/// <summary>
/// Своп иконки кнопки на «нажатый» арт дизайн-наборов. У BuddyV2.Icon.* и
/// BuddySettings.Icon.* каждый глиф существует в двух видах: .Default/.Pressed.
/// Пока Button.IsPressed — показываем Pressed-вариант, после отпускания — базовый.
/// Правило ключа: «…Default» → «…Pressed»; иначе baseKey + "Pressed"
/// (алиасы Icon*Pressed лежат в Resources/IconsDark.xaml/IconsLight.xaml).
/// Если Pressed-ключа нет — кнопка ведёт себя как раньше (только scale-триггер).
/// </summary>
public static class PressedIconSwap
{
    private sealed class Holder
    {
        public string BaseKey = "";
        public bool Listening;
    }

    private static readonly ConditionalWeakTable<Button, Holder> _map = new();

    /// <summary>Идемпотентно: запоминает базовый ключ и вешает слушатель IsPressed.</summary>
    public static void Wire(Button btn, string baseKey)
    {
        UpdateKey(btn, baseKey);
        var h = _map.GetOrCreateValue(btn);
        if (h.Listening) return;
        h.Listening = true;
        var dpd = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button));
        if (dpd == null) return;
        // DPD.AddValueChanged держит сильную ссылку на btn (классическая утечка WPF):
        // снимаем при Unloaded — иначе закрытое окно настроек не собирается GC.
        btn.Unloaded += OnBtnUnloaded;
        dpd.AddValueChanged(btn, OnIsPressedChanged);
    }

    private static void OnBtnUnloaded(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var dpd = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button));
        dpd?.RemoveValueChanged(btn, OnIsPressedChanged);
        btn.Unloaded -= OnBtnUnloaded;
        if (_map.TryGetValue(btn, out var h))
            h.Listening = false; // DataTemplate-кнопки перезагружаются — Wire снова подхватит.
    }


    /// <summary>Семантический своп базовой иконки (mic→mic-off и т.п.):
    /// переключает и Pressed-вариант. Если кнопка сейчас нажата — перерисует pressed-арт нового ключа.</summary>
    public static void UpdateKey(Button btn, string baseKey)
    {
        var h = _map.GetOrCreateValue(btn);
        h.BaseKey = baseKey;
        Apply(btn, btn.IsPressed ? (PressedKeyOf(baseKey) ?? baseKey) : baseKey);
    }

    private static void OnIsPressedChanged(object? sender, EventArgs e)
    {
        if (sender is not Button btn || !_map.TryGetValue(btn, out var h) || h.BaseKey.Length == 0)
            return;
        var key = btn.IsPressed ? (PressedKeyOf(h.BaseKey) ?? h.BaseKey) : h.BaseKey;
        Apply(btn, key);
    }

    private static string? PressedKeyOf(string baseKey)
    {
        var pressed = baseKey.EndsWith(".Default", StringComparison.Ordinal)
            ? baseKey[..^".Default".Length] + ".Pressed"
            : baseKey + "Pressed";
        return Application.Current?.TryFindResource(pressed) != null ? pressed : null;
    }

    /// <summary>Подменяет визуал иконки внутри кнопки: именованный Cp из шаблона,
    /// ContentPresenter-контент (пилюли оверлея), Image или Viewbox→Image (настройки).
    /// Возвращает false — кнопка не иконочная, ничего не трогаем.</summary>
    private static bool Apply(Button btn, string key)
    {
        var res = Application.Current?.TryFindResource(key);
        if (res == null) return false;
        if (btn.Template?.FindName("Cp", btn) is ContentPresenter named)
        {
            named.Content = res;
            return true;
        }
        switch (btn.Content)
        {
            case ContentPresenter cp:
                cp.Content = res;
                return true;
            case Image img:
                img.Source = AsImageSource(res);
                return true;
            case Viewbox { Child: Image vbImg }:
                vbImg.Source = AsImageSource(res);
                return true;
            // Icon+text buttons: StackPanel/Panel с первым Image-ребёнком несёт глиф.
            // Нашли Image — свопаем его Source; нет — кнопка текстовая, выходим.
            case System.Windows.Controls.Panel panel:
                var pImg = panel.Children.OfType<Image>().FirstOrDefault();
                if (pImg == null) return false;
                pImg.Source = AsImageSource(res);
                return true;
            default:
                return false;
        }
    }

    private static ImageSource? AsImageSource(object res) => res switch
    {
        ImageSource s => s,
        Viewbox { Child: Image { Source: var s } } => s,
        Image { Source: var s } => s,
        _ => null
    };
}
