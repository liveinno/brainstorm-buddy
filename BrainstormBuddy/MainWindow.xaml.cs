using System.Windows;

namespace BrainstormBuddy;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = Native.StealthModeService.DisplayName; // стелс-alias, если задан
        Loaded += (s, e) => StatusText.Text = (string)FindResource("L_Main_StatusListening");
    }

    private void OnOpenOverlay(object sender, RoutedEventArgs e)
    {
        App.Current.ShowOverlay();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        App.Current.OpenSettings();
    }
}
