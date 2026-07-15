using System.Windows;

namespace BrainstormBuddy;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (s, e) => StatusText.Text = "Оверлей слушает микрофон и системный звук.";
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
