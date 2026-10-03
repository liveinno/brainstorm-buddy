using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrainstormBuddy.Config;
using BrainstormBuddy.Native;

namespace BrainstormBuddy;

/// <summary>
/// Караоке-оверлей (телесуфлёр): полупрозрачная полоса с лентой подсказок.
/// Новые строки въезжают снизу (TranslateTransform ленты), старые уплывают вверх
/// и гаснут по возрасту. Референс — karaoke-overlay-v1-teleprompter.html.
///
/// Env-хуки (см. контракт §6):
///   BUDDY_CAPTURE_VISIBLE=1 — не применять exclude-from-capture (окно видно на скриншотах);
///   BUDDY_KARAOKE_DEMO=1    — автопоказ окна + StartDemo() из Initialize.
/// </summary>
public partial class KaraokeWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    // Затухание по возрасту (как в прототипе): age → opacity.
    private static readonly double[] AgeOpacity = { 1.0, 0.72, 0.48, 0.28, 0.14, 0.06 };

    // Мягкое затухание верха ленты (аналог mask-image в прототипе). Вешается на
    // LinesPanel, а не на Strip: RelativeToBoundingBox берёт 24% от высоты ленты,
    // а не всего окна — иначе на полноэкранной полосе верх затирался на сотни px.
    private static readonly LinearGradientBrush _topFadeMask = Freeze(
        new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0.0),
                new GradientStop(Colors.Black, 0.24)
            },
            new System.Windows.Point(0, 0), new System.Windows.Point(0, 1)));

    // Тонкая обводка полосы (белый ~10%, как в прототипе).
    private static readonly SolidColorBrush _borderBrush = Freeze(
        new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)));

    private static readonly CubicEase _easeOut = new() { EasingMode = EasingMode.EaseOut };

    // Тень текста при скрытом фоне (ShowBackground=false): читаемость на любом фоне.
    // Мягкая (BlurRadius 8), статичная, заморожена — без перфоманс-просадки.
    private static readonly DropShadowEffect _textShadow = Freeze(new DropShadowEffect
    {
        Color = Colors.Black,
        BlurRadius = 8,
        Opacity = 0.8,
        ShadowDepth = 0,
        RenderingBias = RenderingBias.Quality
    });

    // Кисти строк: _textBrush — единый цвет при AgeColorFade=false (TextColor);
    // _ageBrushes[age] — цвет по возрасту при фейде (золото FreshColor на age0,
    // старшие строки — OldLinesColor; TextColor в фейде не участвует).
    // Пересобираются в ApplyConfig — на push лишь выбор из кэша, без аллокаций.
    // Все кисти заморожены.
    private Brush _textBrush = FrozenBrush(null, Colors.White);
    private Brush[] _ageBrushes = Array.Empty<Brush>();

    // Демо-лента: 8 русских подсказок, роли по кругу answer/question/user.
    private static readonly string[] DemoRoles = { "answer", "question", "user" };
    private static readonly string[] DemoHints =
    {
        "Подчеркните опыт с WASAPI loopback и низколатентным аудио — это ваше ключевое отличие.",
        "Можно ответить так: буфер держит 10 секунд контекста и отправляет в модель только последний вопрос собеседника.",
        "Упомяните, что VAD отсекает тишину, поэтому токены не тратятся на паузы.",
        "Если спросят про приватность: аудио не пишется на диск, буфер живёт только в памяти.",
        "Короткий вариант: оверлей click-through, не перехватывает фокус и не виден на демонстрации экрана.",
        "Стоит добавить цифру: сквозная задержка от слова до подсказки — около полутора секунд.",
        "Спросите в ответ, какой у них сейчас стек распознавания речи — это покажет экспертность.",
        "Сворачивайте тему: предложите показать живое демо прямо на этом звонке."
    };

    private KaraokeConfig _cfg = new();
    private Action<Window> _applyClickThrough = _ => { };
    private Action<Window> _applyExclude = _ => { };
    private bool _clickThroughApplied;
    private bool _excludeFromCaptureApplied;

    private readonly List<LineEntry> _lines = new();
    private readonly DispatcherTimer _demoTimer;
    private int _demoIndex;

    // Активная стрим-строка (SSE): создаётся BeginStreamLine, дописывается
    // AppendToStreamLine, отпускается EndStreamLine. _streamSuppressed — Begin
    // прошёл, но роль отфильтрована/полоса скрыта: дельты проглатываем, как PushLine.
    private LineEntry? _streamEntry;
    private bool _streamSuppressed;

    /// <summary>Строка ленты: VM (текст/роль/возраст) + созданный для неё UI.</summary>
    private sealed class LineEntry
    {
        public KaraokeLineVm Vm = new();
        public Grid Root = new();
        public Border Marker = new();
        public TextBlock Tb = new();
        public bool Removing;
    }

    public KaraokeWindow()
    {
        InitializeComponent();
        LinesPanel.OpacityMask = _topFadeMask;
        BgLayer.BorderBrush = _borderBrush;
        // Демо-тик ~1.5с; без per-frame таймеров — все анимации на composition thread.
        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _demoTimer.Tick += OnDemoTick;
    }

    /// <summary>
    /// Первичная настройка: конфиг, хуки окна (click-through, exclude-from-capture).
    /// Аналог OverlayWindow.Initialize. Делегаты сохраняются — ApplyConfig умеет
    /// горячо переключать нативные режимы.
    /// </summary>
    public void Initialize(KaraokeConfig cfg, Action<Window> applyClickThrough, Action<Window> applyExclude)
    {
        _cfg = cfg;
        _applyClickThrough = applyClickThrough ?? (_ => { });
        _applyExclude = applyExclude ?? (_ => { });

        ApplyConfig(cfg);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;

        // Тестовый хук: BUDDY_KARAOKE_DEMO=1 → автопоказ + демо-лента.
        if (Environment.GetEnvironmentVariable("BUDDY_KARAOKE_DEMO") == "1")
        {
            Show();
            StartDemo();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowHelper.InstallHwndSourceHook(this, WndProc);
        // hwnd уже есть — применяем нативные режимы сразу, до Loaded.
        ApplyNativeModes();
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        // Повторное применение безопасно (SetWindowLong/DisplayAffinity идемпотентны);
        // нужно на случай, если hwnd появился только к Loaded.
        ApplyNativeModes();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Click-through: клики проходят сквозь полосу к окнам ниже.
        if (msg == WM_NCHITTEST && _clickThroughApplied)
        {
            handled = true;
            return (IntPtr)HTTRANSPARENT;
        }
        return IntPtr.Zero;
    }

    /// <summary>Горячее применение конфига: геометрия, цвета, шрифт, стелс/click-through.</summary>
    public void ApplyConfig(KaraokeConfig cfg)
    {
        _cfg = cfg;

        // Прозрачность всего окна — на Window.Opacity.
        Opacity = Clamp(cfg.Opacity, 0.25, 1.0);

        // Фон-слой: цвет полосы, а BackgroundOpacity зашита в альфу кисти —
        // так затухание не трогает дочерние строки (в отличие от Border.Opacity).
        var bg = ParseColor(cfg.BackgroundColor, Color.FromArgb(0xFF, 0x0A, 0x16, 0x22));
        var bgBrush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(bg.A * Clamp(cfg.BackgroundOpacity, 0.0, 1.0)), bg.R, bg.G, bg.B));
        bgBrush.Freeze();
        BgLayer.Background = bgBrush;
        BgLayer.BorderBrush = _borderBrush;

        // ShowBackground=false → BgLayer полностью Collapsed, прямоугольника нет вообще,
        // остаётся плавающий текст (тень включается в StyleLine, только если TextShadow=true).
        BgLayer.Visibility = cfg.ShowBackground ? Visibility.Visible : Visibility.Collapsed;

        // Кисти строк (TextColor/FreshColor → градиент по возрасту) — до рестайла строк.
        RebuildAgeBrushes();

        LinesPanel.Margin = new Thickness(Clamp(cfg.InnerPadding, 0, 80));

        // Рестайл уже показанных строк; затухание по возрасту — с учётом DimByAge.
        foreach (var entry in _lines)
        {
            StyleLine(entry);
            if (!entry.Removing)
            {
                double target = cfg.DimByAge ? AgeOpacity[Math.Min(entry.Vm.Age, AgeOpacity.Length - 1)] : 1.0;
                entry.Vm.Opacity = target;
                AnimateOpacity(entry.Root, target);
            }
        }

        ApplyNativeModes();
        ApplyPosition();
    }

    /// <summary>
    /// Добавить строку в ленту. role ∈ "answer"|"question"|"user"; фильтрация по
    /// Show*-флагам — внутри (App зовёт всегда, без проверок).
    /// </summary>
    public void PushLine(string role, string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => PushLine(role, text)));
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;
        if (!CanShowRole(role)) return;

        AddLineCore(role, text);
    }

    // Фильтр строки по роли (Show*-флаги) + гейт на скрытую полосу: строк не копим,
    // иначе при повторном показе (хоткей ИЛИ чекбокс в настройках) всплывёт залпом всё
    // буферизованное. Демо/предпросмотр не задевает — StartDemo/PushLine идут после Show().
    private bool CanShowRole(string role)
    {
        if (!IsVisible) return false;
        return role switch
        {
            "answer" => _cfg.ShowAnswers,
            "question" => _cfg.ShowQuestions,
            "user" => _cfg.ShowUserLines,
            _ => true
        };
    }

    /// <summary>Стриминг (SSE): начать новую пустую строку ленты роли role
    /// ("answer"|"user"). Потокобезопасно — маршаллится на UI-диспетчер, как PushLine.
    /// Фильтрация Show*/видимость — как у PushLine: отфильтрованная роль глушит дельты.</summary>
    public void BeginStreamLine(string role)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => BeginStreamLine(role)));
            return;
        }

        _streamEntry = null;
        if (!CanShowRole(role))
        {
            _streamSuppressed = true; // Begin был — Append не должен создавать "answer"
            return;
        }
        _streamSuppressed = false;
        _streamEntry = AddLineCore(role, "");
    }

    /// <summary>Стриминг (SSE): дописать дельту в последнюю стрим-строку (INPC на
    /// KaraokeLineVm.Text — строка не пересоздаётся). Если Begin не вызывался —
    /// создаёт строку роли "answer" (через CanShowRole — те же фильтры, что у PushLine).</summary>
    public void AppendToStreamLine(string delta)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendToStreamLine(delta)));
            return;
        }

        if (string.IsNullOrEmpty(delta) || _streamSuppressed) return;

        // Clear()/TrimExcess() могли выкинуть стрим-строку из ленты — не пишем в невидимое.
        if (_streamEntry != null && (_streamEntry.Removing || !_lines.Contains(_streamEntry)))
            _streamEntry = null;

        if (_streamEntry == null)
        {
            if (!CanShowRole("answer")) return;
            _streamEntry = AddLineCore("answer", "");
        }

        _streamEntry.Vm.Text += delta;
    }

    /// <summary>Стриминг (SSE): финализировать стрим-строку — остаётся обычной строкой
    /// ленты. Пустая строка (дельт не было) убирается, как пустой PushLine.</summary>
    public void EndStreamLine()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(EndStreamLine));
            return;
        }

        var entry = _streamEntry;
        _streamEntry = null;
        _streamSuppressed = false;

        if (entry != null && !entry.Removing
            && string.IsNullOrWhiteSpace(entry.Vm.Text)
            && _lines.Remove(entry))
        {
            LinesPanel.Children.Remove(entry.Root);
            RefreshAges(); // маркер/возраст соседей пересчитываются под новый хвост
        }
    }

    /// <summary>Полностью очистить ленту (строки + панель). Юзается при включении
    /// караоке хоткеем — иначе накопленные за время Hide() PushLine покажутся разом.</summary>
    public void Clear()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Clear));
            return;
        }
        _lines.Clear();
        LinesPanel.Children.Clear();
        _streamEntry = null;
        _streamSuppressed = false;
        LinesTransform.BeginAnimation(TranslateTransform.YProperty, null);
        LinesTransform.Y = 0;
    }

    /// <summary>Демо-режим: цикл по 8 русским подсказкам, роли по кругу. Без фильтрации —
    /// это самотест отображения, а не реальные реплики (PushLine фильтрует по Show*).</summary>
    public void StartDemo()
    {
        _demoTimer.Start();
    }

    public void StopDemo()
    {
        _demoTimer.Stop();
    }

    private void OnDemoTick(object? sender, EventArgs e)
    {
        int i = _demoIndex++;
        AddLineCore(DemoRoles[i % DemoRoles.Length], DemoHints[i % DemoHints.Length]);
    }

    // ─── Лента строк ──────────────────────────────────────────────────────

    private LineEntry AddLineCore(string role, string text)
    {
        var entry = new LineEntry();
        entry.Vm.Text = text;
        entry.Vm.Role = role;

        var root = entry.Root;
        root.Opacity = 0.0; // въезжающая строка проявляется через RefreshAges

        var marker = entry.Marker;
        marker.Width = 16;
        marker.Height = 5;
        marker.CornerRadius = new CornerRadius(3);
        marker.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        marker.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        marker.Visibility = Visibility.Collapsed; // покажется при age==0 в RefreshAges

        var tb = entry.Tb;
        tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(KaraokeLineVm.Text)) { Mode = System.Windows.Data.BindingMode.OneWay });
        tb.DataContext = entry.Vm;

        root.Children.Add(marker);
        root.Children.Add(tb);
        StyleLine(entry);

        // Замер до добавления — нужна высота новой строки для сдвига ленты.
        double stripWidth = Strip.ActualWidth > 0 ? Strip.ActualWidth : Width;
        if (double.IsNaN(stripWidth) || stripWidth <= 0) stripWidth = 800;
        double pad = LinesPanel.Margin.Left + LinesPanel.Margin.Right;
        root.Measure(new System.Windows.Size(Math.Max(stripWidth - pad, 1), double.PositiveInfinity));
        double newLineHeight = root.DesiredSize.Height;

        _lines.Add(entry);
        LinesPanel.Children.Add(root);

        // Slide-in: лента мгновенно сдвинута вниз на высоту новой строки
        // (накопление Y сохраняет незавершённый сдвиг при частых пушах),
        // затем плавно возвращается к 0 — вся лента скользит вверх.
        double startY = LinesTransform.Y + newLineHeight;
        LinesTransform.BeginAnimation(TranslateTransform.YProperty, null);
        LinesTransform.Y = startY;
        var slide = new DoubleAnimation(startY, 0.0, SlideDuration)
        {
            EasingFunction = _easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        LinesTransform.BeginAnimation(TranslateTransform.YProperty, slide);

        RefreshAges();
        TrimExcess();
        return entry;
    }

    /// <summary>Пересчёт возрастов: age = позиция от конца; маркер у свежей (age==0).</summary>
    private void RefreshAges()
    {
        int n = _lines.Count;
        for (int k = 0; k < n; k++)
        {
            var entry = _lines[k];
            int age = n - 1 - k;
            entry.Vm.Age = age;
            entry.Tb.Foreground = BrushForAge(age); // цвет по возрасту: золото → OldLinesColor

            double target = _cfg.DimByAge ? AgeOpacity[Math.Min(age, AgeOpacity.Length - 1)] : 1.0;
            entry.Vm.Opacity = target;

            if (entry.Removing) continue;
            entry.Marker.Visibility = (_cfg.ShowMarker && age == 0) ? Visibility.Visible : Visibility.Collapsed;
            AnimateOpacity(entry.Root, target);
        }
    }

    /// <summary>Строки сверх MaxLines гаснут и удаляются по завершении анимации.</summary>
    private void TrimExcess()
    {
        int excess = _lines.Count - Math.Max(_cfg.MaxLines, 1);
        if (excess <= 0) return;

        var doomed = _lines.GetRange(0, excess);
        _lines.RemoveRange(0, excess);
        foreach (var entry in doomed)
        {
            entry.Removing = true;
            var fadeOut = new DoubleAnimation(entry.Root.Opacity, 0.0, FadeDuration)
            {
                FillBehavior = FillBehavior.HoldEnd
            };
            var root = entry.Root;
            fadeOut.Completed += (_, _) => LinesPanel.Children.Remove(root);
            root.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    private void StyleLine(LineEntry entry)
    {
        var tb = entry.Tb;
        double fs = Clamp(_cfg.FontSize, 12, 64);
        tb.FontSize = fs;
        tb.FontWeight = FontWeights.SemiBold;
        tb.Foreground = BrushForAge(entry.Vm.Age);
        tb.TextWrapping = _cfg.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        tb.TextAlignment = string.Equals(_cfg.TextAlignment, "Center", StringComparison.OrdinalIgnoreCase)
            ? TextAlignment.Center
            : TextAlignment.Left;
        // Отступ слева — место под маркер (как padding-left в прототипе).
        tb.Margin = new Thickness(_cfg.ShowMarker ? 36 : 0, fs * 0.1, 0, fs * 0.1);
        // Маркер — у первой текстовой строки (top = padding + 0.55em в прототипе).
        entry.Marker.Margin = new Thickness(0, fs * 0.1 + fs * 0.55 - 2.5, 0, 0);
        entry.Marker.Background = FrozenBrush(_cfg.AccentColor, Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
        // Плавающий текст без полосы: лёгкая тень для читаемости на любом фоне
        // (отключаемая — KaraokeConfig.TextShadow).
        tb.Effect = (!_cfg.ShowBackground && _cfg.TextShadow) ? _textShadow : null;
    }

    private void AnimateOpacity(UIElement element, double target)
    {
        var anim = new DoubleAnimation(element.Opacity, target, FadeDuration)
        {
            FillBehavior = FillBehavior.HoldEnd
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // Пересборка кистей строк: вызывается из ApplyConfig (зависит от
    // TextColor/FreshColor/OldLinesColor), не на каждый push.
    // При AgeColorFade=false в RefreshAges/StyleLine берётся _textBrush.
    private void RebuildAgeBrushes()
    {
        var text = ParseColor(_cfg.TextColor, Colors.White);
        _textBrush = Freeze(new SolidColorBrush(text));

        var fresh = ParseColor(_cfg.FreshColor, Color.FromArgb(0xFF, 0xFF, 0xE9, 0xA8));
        // Битый/пустой hex → фолбэк на дефолт (золото/белый) через ParseColor.
        var old = ParseColor(_cfg.OldLinesColor, Colors.White);
        var brushes = new Brush[AgeOpacity.Length];
        for (int age = 0; age < brushes.Length; age++)
            brushes[age] = Freeze(new SolidColorBrush(AgeColor(age, fresh, old)));
        _ageBrushes = brushes;
    }

    // Кисть строки по возрасту: градиент при AgeColorFade, иначе единый TextColor.
    private Brush BrushForAge(int age)
        => _cfg.AgeColorFade && _ageBrushes.Length > 0
            ? _ageBrushes[Math.Min(Math.Max(age, 0), _ageBrushes.Length - 1)]
            : _textBrush;

    // age0 = FreshColor (золото), age>=1 = OldLinesColor. TextColor в фейде не
    // участвует — ручки Fresh/Old полностью задают ленту (регрессия 2.7.0 лерпила
    // к TextColor: при чёрном TextColor старшие строки уходили в тёмное).
    // Плавность переходу даёт DimByAge (альфа старших строк), лерп цвета убран.
    private static Color AgeColor(int age, Color fresh, Color old)
        => age <= 0 ? fresh : old;

    // ─── Позиционирование ─────────────────────────────────────────────────

    private void ApplyPosition()
    {
        var wa = SystemParameters.WorkArea;
        double fs = Clamp(_cfg.FontSize, 12, 64);
        double pad = Clamp(_cfg.InnerPadding, 0, 80);
        double margin = Clamp(_cfg.MarginPx, 0, 200);
        double width = wa.Width * Clamp(_cfg.WidthPct, 20, 100) / 100.0;

        var pos = (_cfg.Position ?? "Bottom").Trim();
        if (pos.Equals("Top", StringComparison.OrdinalIgnoreCase))
        {
            Width = width;
            Height = BandHeight(wa.Height, StripHeight(fs, pad));
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + margin;
        }
        else if (pos.Equals("Left", StringComparison.OrdinalIgnoreCase))
        {
            Width = width;
            Height = BandHeight(wa.Height, wa.Height * 0.7);
            Left = wa.Left + margin;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
        else if (pos.Equals("Right", StringComparison.OrdinalIgnoreCase))
        {
            Width = width;
            Height = BandHeight(wa.Height, wa.Height * 0.7);
            Left = wa.Right - width - margin;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
        else // Bottom
        {
            Width = width;
            Height = BandHeight(wa.Height, StripHeight(fs, pad));
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Bottom - margin - Height;
        }
    }

    // Фиксированная высота полосы: FontSize*1.38*min(MaxLines,4) + 2*InnerPadding (контракт §3).
    private double StripHeight(double fs, double pad)
        => fs * 1.38 * Math.Min(Math.Max(_cfg.MaxLines, 1), 4) + 2 * pad;

    // Высота полосы: HeightPct>0 → % высоты рабочей области для ВСЕХ позиций
    // (у Left/Right это высота вертикальной полосы, WidthPct остаётся шириной).
    // HeightPct=0 → autoH: Top/Bottom — StripHeight по шрифту/строкам, Left/Right — 70%.
    // OpacityMask _topFadeMask висит на LinesPanel с относительными координатами
    // (0→0.24): верх гасится пропорционально высоте ленты при любой высоте полосы.
    private double BandHeight(double waHeight, double autoH)
    {
        int pct = (int)Clamp(_cfg.HeightPct, 0, 100);
        return pct > 0 ? waHeight * pct / 100.0 : autoH;
    }

    // ─── Нативные режимы ──────────────────────────────────────────────────

    private void ApplyNativeModes()
    {
        var hwnd = WindowHelper.GetHandle(this);
        if (hwnd == IntPtr.Zero) return; // до SourceInitialized — повторим там же и в Loaded

        if (_cfg.ClickThrough)
        {
            _applyClickThrough(this);
            _clickThroughApplied = true;
        }
        else
        {
            WindowHelper.RemoveClickThrough(this);
            _clickThroughApplied = false;
        }

        // Тестовый хук: BUDDY_CAPTURE_VISIBLE=1 оставляет окно видимым для скриншотов.
        bool captureVisible = Environment.GetEnvironmentVariable("BUDDY_CAPTURE_VISIBLE") == "1";
        if (_cfg.HiddenFromCapture && !captureVisible)
        {
            _applyExclude(this);
            _excludeFromCaptureApplied = true;
        }
        else if (_excludeFromCaptureApplied)
        {
            WindowHelper.RemoveExcludeFromCapture(this);
            _excludeFromCaptureApplied = false;
        }
    }

    // ─── Утилиты ──────────────────────────────────────────────────────────

    private Duration SlideDuration => new(TimeSpan.FromMilliseconds(Clamp(_cfg.SlideDurationMs, 100, 2000)));
    private Duration FadeDuration => new(TimeSpan.FromMilliseconds(Clamp(_cfg.FadeDurationMs, 100, 2000)));

    private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

    private static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                var obj = System.Windows.Media.ColorConverter.ConvertFromString(hex.Trim());
                if (obj is Color c) return c;
            }
        }
        catch (FormatException) { }
        catch (NotSupportedException) { }
        return fallback;
    }

    private static SolidColorBrush FrozenBrush(string? hex, Color fallback)
    {
        var b = new SolidColorBrush(ParseColor(hex, fallback));
        b.Freeze();
        return b;
    }

    private static T Freeze<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Состояние строки ленты (INPC): текст, роль, возраст, целевая прозрачность.</summary>
public class KaraokeLineVm : INotifyPropertyChanged
{
    private string _text = "";
    private string _role = "answer";
    private int _age;
    private double _opacity = 1.0;

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public string Role
    {
        get => _role;
        set { _role = value; OnPropertyChanged(); }
    }

    public int Age
    {
        get => _age;
        set { _age = value; OnPropertyChanged(); }
    }

    public double Opacity
    {
        get => _opacity;
        set { _opacity = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
