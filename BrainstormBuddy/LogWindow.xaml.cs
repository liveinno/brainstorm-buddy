using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BrainstormBuddy.Services;

namespace BrainstormBuddy;

    public partial class LogWindow : Window
{
    private readonly LoggingService _logger;
    private readonly DispatcherTimer _uiTimer;
    private const int MaxItems = 1000;
    private volatile bool _paused;

    public LogWindow(LoggingService logger)
    {
        _logger = logger;
        InitializeComponent();

        // Подписываемся на новые логи
        _logger.LogAppended += OnLogAppended;

        // Загружаем последние события из ring buffer
        foreach (var ev in _logger.GetRecent(MaxItems))
            AppendLog(ev, scrollToEnd: false);

        UpdateCountText();
        UpdateAutoScroll();

        // Таймер обновляет счётчик даже когда событий нет
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (s, e) => UpdateCountText();
        _uiTimer.Start();

        // Синхронизация Pause-флага с чекбоксом
        PauseCheck.Checked += (s, e) => _paused = true;
        PauseCheck.Unchecked += (s, e) => _paused = false;

        Closed += (s, e) =>
        {
            _logger.LogAppended -= OnLogAppended;
            _uiTimer.Stop();
        };
    }

    private void OnLogAppended(object? sender, LogEvent ev)
    {
        if (_paused) return;
        // ТОЛЬКО BeginInvoke! Блокирующий Invoke здесь дедлочил приложение:
        // аудио-поток логирует из-под lock(_lock) AudioBuffer → ждёт UI,
        // а UI-поток в этот момент тянет слайдер → UpdateParameters → ждёт тот же lock.
        Dispatcher.BeginInvoke(() =>
        {
            AppendLog(ev, scrollToEnd: AutoScrollCheck.IsChecked == true);
        });
    }

    private void AppendLog(LogEvent ev, bool scrollToEnd)
    {
        // Фильтр уровня
        if (!PassesFilter(ev.Level)) return;

        var item = new ListBoxItem
        {
            Content = BuildInline(ev),
            Background = LevelBackground(ev.Level),
            Padding = new Thickness(4, 1, 4, 1)
        };
        LogList.Items.Add(item);

        // Ограничиваем размер
        while (LogList.Items.Count > MaxItems)
            LogList.Items.RemoveAt(0);

        if (scrollToEnd && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private static Inline BuildInline(LogEvent ev)
    {
        var span = new Span();
        span.Inlines.Add(new Run($"[{ev.Timestamp:HH:mm:ss.fff}] ")
        {
            Foreground = Brushes.Gray
        });
        span.Inlines.Add(new Run($"[{ev.Level,-5}] ")
        {
            Foreground = LevelBrush(ev.Level),
            FontWeight = ev.Level == LogLevel.Error ? FontWeights.Bold : FontWeights.Normal
        });
        span.Inlines.Add(new Run($"[{ev.Category,-10}] ")
        {
            Foreground = Brushes.SteelBlue
        });
        span.Inlines.Add(new Run(ev.Message)
        {
            Foreground = ev.Level == LogLevel.Error
                ? Brushes.LightCoral
                : Brushes.LightGray
        });
        return span;
    }

    private bool PassesFilter(LogLevel level)
    {
        var tag = (LevelFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        return tag switch
        {
            "All" => true,
            "InfoPlus" => level >= LogLevel.Info,
            "WarnPlus" => level >= LogLevel.Warn,
            "Error" => level == LogLevel.Error,
            _ => true
        };
    }

    private void OnLevelFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // ComboBox в XAML имеет SelectedIndex=0 по умолчанию — событие выстреливает
        // во время InitializeComponent(), когда LogList ещё null. Игнорируем.
        if (LogList == null || _logger == null) return;
        LogList.Items.Clear();
        foreach (var ev in _logger.GetRecent(MaxItems))
            AppendLog(ev, scrollToEnd: false);
        UpdateAutoScroll();
    }

    private void UpdateAutoScroll()
    {
        if (AutoScrollCheck.IsChecked == true && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        LogList.Items.Clear();
        UpdateCountText();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = _logger.GetRecentAsText();
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to copy logs to clipboard", ex);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _logger.LogDirectory;
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open logs folder", ex);
        }
    }

    private void UpdateCountText()
    {
        CountText.Text = $"Записей: {LogList.Items.Count}";
    }

    private static Brush LevelBrush(LogLevel level) => level switch
    {
        LogLevel.Debug => Brushes.DarkGray,
        LogLevel.Info => Brushes.LightSkyBlue,
        LogLevel.Warn => Brushes.Goldenrod,
        LogLevel.Error => Brushes.IndianRed,
        _ => Brushes.White
    };

    private static Brush LevelBackground(LogLevel level) => level switch
    {
        LogLevel.Error => new SolidColorBrush(Color.FromArgb(0x33, 0xC0, 0x39, 0x2B)),
        LogLevel.Warn => new SolidColorBrush(Color.FromArgb(0x22, 0xC0, 0x9A, 0x2B)),
        _ => Brushes.Transparent
    };
}
