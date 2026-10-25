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
using Application = System.Windows.Application;

namespace BrainstormBuddy;

/// <summary>
/// Караоке-оверлей (телесуфлёр): полупрозрачная полоса с лентой подсказок.
/// Строки прибиты к нижнему краю полосы (свежая реплика у нижней кромки —
/// у взгляда, история нарастает вверх); полоса резиновая — высота окна едет
/// за контентом до потолка StripHeight (пустая лента схлопывается в 0);
/// при переполнении лента скроллится вверх (Canvas.Top ленты → -(overflow)),
/// старшие строки гаснут по возрасту.
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

    // Затухание верха ленты (аналог mask-image в прототипе). Вешается на LinesPanel,
    // а не на Strip: RelativeToBoundingBox берёт 24% от высоты контента ленты —
    // при скролле маска движется вместе со строками и гасит только верхние строки.
    // Альфа верхнего стопа = MinHistoryOpacity (старшие строки остаются читаемыми);
    // при HistoryFade=false маска снимается совсем (ApplyFadeMask в ApplyConfig).
    private const double TopFadePortion = 0.24;

    private static LinearGradientBrush TopFadeMask(double minOpacity) => Freeze(
        new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromScRgb((float)Clamp(minOpacity, 0.0, 1.0), 0f, 0f, 0f), 0.0),
                new GradientStop(Colors.Black, TopFadePortion)
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
    // старшие строки — OldLinesColor; TextColor в фейде не участвует);
    // _questionBrush — реплики собеседника (роль "question"), розовый акцент
    // вне зависимости от возраста/фейда — их надо отличать от ответов.
    // Пересобираются в ApplyConfig — на push лишь выбор из кэша, без аллокаций.
    // Все кисти заморожены.
    private Brush _textBrush = FrozenBrush(null, Colors.White);
    private Brush _questionBrush = FrozenBrush(null, Color.FromArgb(0xFF, 0xF2, 0x7A, 0x8A));
    private Brush[] _ageBrushes = Array.Empty<Brush>();

    // Демо-лента: 8 подсказок из ресурсов (локализуемые), роли по кругу answer/question/user.
    private static readonly string[] DemoRoles = { "answer", "question", "user" };
    private static readonly string[] DemoHintKeys =
    {
        "L_Kar_Demo1", "L_Kar_Demo2", "L_Kar_Demo3", "L_Kar_Demo4",
        "L_Kar_Demo5", "L_Kar_Demo6", "L_Kar_Demo7", "L_Kar_Demo8"
    };

    private KaraokeConfig _cfg = new();
    private Action<Window> _applyClickThrough = _ => { };
    private Action<Window> _applyExclude = _ => { };
    private bool _clickThroughApplied;
    private bool _excludeFromCaptureApplied;

    private readonly List<LineEntry> _lines = new();
    private readonly DispatcherTimer _demoTimer;
    private int _demoIndex;


    // Активные стрим-строки (SSE) по streamId: BeginStreamLine(role) возвращает id,
    // дельты идут AppendToStreamLine(id,…), финал — EndStreamLine/DropStreamLine(id).
    // Несколько параллельных стримов не смешиваются: у каждого свой LineEntry.
    // suppressed — Begin прошёл, но роль отфильтрована/полоса скрыта: дельты глушим.
    private readonly Dictionary<int, (LineEntry? entry, bool suppressed)> _streamEntries = new();
    private int _nextStreamId;

    // Текущая цель позиции ленты (Canvas.Top LinesPanel): >0 — контент ниже
    // вьюпорта, прижат к нижнему краю (пустота сверху); <0 — лента сдвинута
    // вверх при переполнении (старшие строки ушли за верхний край). Хранится,
    // чтобы LayoutUpdated-реконсиляция не сбивала бегущую анимацию скролла.
    private double _panelTargetY;
    // Ширина ленты при последнем замере строк. Если окно/боковой якорь поменяли
    // ширину после первого Measure, DesiredSize строк зависает на старой ширине
    // (StackPanel не перевешивает, пока не изменится его собственный constraint).
    // Отслеживаем ActualWidth и при смене инвалидируем measure — см. OnLayoutUpdated.
    private double _lastPanelWidth = -1;

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
        ApplyFadeMask(); // OpacityMask зависит от HistoryFade/MinHistoryOpacity
        BgLayer.BorderBrush = _borderBrush;
        // Любой проход layout (Show, ресайс, рост ленты от пушей/стрим-дельт,
        // удаление строк, рестайл) — подгоняем высоту окна под контент и
        // реконсилим скролл. LayoutUpdated, а не SizeChanged: RenderSize ленты
        // при переполнении упирается в вьюпорт и перестаёт расти, а DesiredSize
        // (содержимое за краем) продолжает меняться — ловим каждый проход.
        LayoutUpdated += (_, _) => OnLayoutUpdated();
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

    /// <summary>Окно сейчас показано (Show() был после последнего Hide()).
    /// Единственный источник истины о видимости: WPF IsVisible у прозрачного
    /// неактивированного окна врёт (false при нулевой высоте и до первого
    /// рендера) — тогл по IsVisible требовал двух нажатий хоткея: первое шло
    /// по ветке «показать» и Clear() затирал ленту. Ставится в Show()/Hide(),
    /// сбрасывается в OnClosed.</summary>
    public bool IsShown { get; private set; }

    // Shadowing, не override: Window.Show()/Hide() невиртуальные. Все вызовы
    // идут по ссылке типа KaraokeWindow (_karaoke в App, PreviewKaraoke) —
    // флаг не разойдётся с реальным состоянием.
    public new void Show()
    {
        IsShown = true; // до base.Show(): layout при показе уже видит флаг
        base.Show();
    }

    public new void Hide()
    {
        IsShown = false; // до base.Hide(): синхронные колбэки уже видят «скрыто»
        base.Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        IsShown = false;
        _demoTimer.Stop(); // иначе тикающий таймер диспетчера держит окно живым
        base.OnClosed(e);
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
        // Ширина ленты = ширина окна МИНУС горизонтальный паддинг. Раньше Width
        // биндился к Strip.ActualWidth, а Margin добавлялся снаружи — лента
        // выходила за правый край окна на 2*InnerPadding и текст резался
        // (баг «текст уехал вправо, слева пустота»).
        SyncPanelWidth();

        // OpacityMask верхнего фейда: снимается при HistoryFade=false, альфа верха
        // масштабируется MinHistoryOpacity — иначе верх ленты гаснет внезависимости
        // от выключенного димминга (регрессия: «строки светлого, а верх тёмный»).
        ApplyFadeMask();

        // Рестайл уже показанных строк; затухание по возрасту — с учётом HistoryFade.
        foreach (var entry in _lines)
        {
            StyleLine(entry);
            if (!entry.Removing)
            {
                double target = EffectiveOpacity(entry.Vm.Age);
                entry.Vm.Opacity = target;
                AnimateOpacity(entry.Root, target);
            }
        }

        ApplyPosition(); // ширина/якорь + резиновая высота под контент (через UpdateElasticHeight)
        SnapScroll();    // высоты строк могли измениться (шрифт/перенос) — к низу мгновенно
        ApplyNativeModes();
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
        if (!IsShown) return false;
        return role switch
        {
            "answer" => _cfg.ShowAnswers,
            "question" => _cfg.ShowQuestions,
            "user" => _cfg.ShowUserLines,
            _ => true
        };
    }

    /// <summary>Стриминг (SSE): начать новую пустую строку ленты роли role
    /// ("answer"|"user"). Возвращает streamId для AppendToStreamLine/End/Drop.
    /// Потокобезопасно — маршаллится на UI-диспетчер (id выдаётся до маршалла,
    /// поэтому Append можно звать сразу, не дожидаясь выполнения Begin).
    /// Фильтрация Show*/видимость — как у PushLine: отфильтрованная роль глушит дельты.</summary>
    public int BeginStreamLine(string role)
    {
        var id = System.Threading.Interlocked.Increment(ref _nextStreamId);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => BeginStreamLineCore(id, role)));
            return id;
        }
        BeginStreamLineCore(id, role);
        return id;
    }

    private void BeginStreamLineCore(int id, string role)
    {
        if (_streamEntries.ContainsKey(id)) return; // дельта обогнала Begin — поток уже зарегистрирован
        if (!CanShowRole(role))
        {
            _streamEntries[id] = (null, true); // Begin был — Append не создаёт строку
            return;
        }
        _streamEntries[id] = (AddLineCore(role, ""), false);
    }

    /// <summary>Стриминг (SSE): дописать дельту в стрим-строку streamId (INPC на
    /// KaraokeLineVm.Text — строка не пересоздаётся). Незнакомый id (гонка с Begin
    /// на диспетчере) создаёт строку роли "answer" — те же фильтры, что у PushLine.</summary>
    public void AppendToStreamLine(int streamId, string delta)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendToStreamLine(streamId, delta)));
            return;
        }

        if (string.IsNullOrEmpty(delta)) return;

        if (!_streamEntries.TryGetValue(streamId, out var st))
        {
            // Дельта обогнала отложенный Begin (BeginInvoke): регистрируем поток под
            // этим id — когда BeginStreamLineCore добежит, увидит запись и не создаст вторую.
            if (!CanShowRole("answer")) { _streamEntries[streamId] = (null, true); return; }
            _streamEntries[streamId] = (AddLineCore("answer", ""), false);
            st = _streamEntries[streamId];
        }
        if (st.suppressed) return;

        var entry = st.entry;
        // Clear()/TrimExcess() могли выкинуть стрим-строку из ленты — не пишем в невидимое.
        if (entry != null && (entry.Removing || !_lines.Contains(entry)))
            entry = null;
        if (entry == null)
        {
            if (!CanShowRole("answer")) return;
            entry = AddLineCore("answer", "");
            _streamEntries[streamId] = (entry, false);
        }
        entry.Vm.Text += delta;
    }

    /// <summary>Стриминг (SSE): финализировать стрим-строку — остаётся обычной строкой
    /// ленты. Пустая строка (дельт не было) убирается, как пустой PushLine.</summary>
    public void EndStreamLine(int streamId)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => EndStreamLine(streamId)));
            return;
        }

        if (!_streamEntries.Remove(streamId, out var st)) return;
        var entry = st.entry;

        if (entry != null && !entry.Removing
            && string.IsNullOrWhiteSpace(entry.Vm.Text)
            && _lines.Remove(entry))
        {
            RemoveLine(entry);
            RefreshAges(); // маркер/возраст соседей пересчитываются под новый хвост
        }
    }

    /// <summary>Стриминг (SSE): ВЫКИНУТЬ стрим-строку целиком — ответ отменён.
    /// Отличие от EndStreamLine (которая убирает лишь пустую строку): снимает
    /// строку даже с текстом — для случая «модель стримом написала маркер
    /// молчания» («(empty)», «(空)», «…»), который на уровне Core считается
    /// осознанной тишиной и не должен оставаться в ленте (2.16.1).</summary>
    public void DropStreamLine(int streamId)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => DropStreamLine(streamId)));
            return;
        }

        if (!_streamEntries.Remove(streamId, out var st)) return;
        var entry = st.entry;

        if (entry != null && !entry.Removing && _lines.Remove(entry))
        {
            RemoveLine(entry);
            RefreshAges();
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
        _streamEntries.Clear();
        SetPanelY(0);
        _panelTargetY = 0;
        UpdateElasticHeight(); // пустая лента → полоса схлопывается до нулевой высоты
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
        if (!IsShown) return; // скрытая полоса молчит: не копим ленту в фоне
        int i = _demoIndex++;
        var hint = Application.Current?.TryFindResource(DemoHintKeys[i % DemoHintKeys.Length]) as string
                   ?? DemoHintKeys[i % DemoHintKeys.Length];
        AddLineCore(DemoRoles[i % DemoRoles.Length], hint);
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

        // Замер до добавления — нужна высота новой строки для сдвига/подъёма
        // ленты. Ширину берём по факту у ленты; ВАЖНО: LinesPanel.ActualWidth уже
        // ЧИСТАЯ от Margin — второй раз паддинг вычитать нельзя (иначе TextBlock
        // мерится уже финала → DesiredSize завышен → снизу дыра/лишний скролл).
        // Фолбэк на Width/Strip.ActualWidth — это ширина СТРИПА до margin, там
        // паддинг вычитаем один раз.
        double pad = LinesPanel.Margin.Left + LinesPanel.Margin.Right;
        double measureW;
        if (LinesPanel.ActualWidth > 0) measureW = LinesPanel.ActualWidth;
        else if (Strip.ActualWidth > 0) measureW = Strip.ActualWidth - pad;
        else measureW = (double.IsNaN(Width) ? 800 : Width) - pad;
        root.Measure(new System.Windows.Size(Math.Max(measureW, 1), double.PositiveInfinity));
        double newLineHeight = root.DesiredSize.Height;

        _lines.Add(entry);
        LinesPanel.Children.Add(root);

        // Полоса резиновая: приращение под контент до потолка; над потолком —
        // скролл вверх (targetY < 0). Если выросли (якорь Bottom/центр дал
        // компенсацию в UpdateElasticHeight → Y > 0) либо переполнение —
        // единый слайд ленты к целевому Y; это и есть телесуфлёр-эффект.
        UpdateElasticHeight();

        double targetY = TargetPanelY();
        if (Math.Abs(targetY - PanelY) > 0.5)
        {
            AnimatePanelTo(PanelY, targetY);
        }
        else
        {
            // Скролл не нужен (окно приросло вниз от верхнего якоря): строка
            // проявляется opacity-фейдом (RefreshAges) и лёгким подъёмом —
            // без сдвига остальных строк (они и так на своих местах).
            SetPanelY(targetY);
            _panelTargetY = targetY;
            var rise = new DoubleAnimation(Math.Min(newLineHeight, 40), 0.0, SlideDuration)
            {
                EasingFunction = _easeOut
            };
            var riseTransform = new TranslateTransform();
            riseTransform.BeginAnimation(TranslateTransform.YProperty, rise);
            root.RenderTransform = riseTransform;
        }

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
            entry.Tb.Foreground = BrushFor(entry, age); // роль question розовая; прочие: золото → OldLinesColor

            double target = EffectiveOpacity(age);
            entry.Vm.Opacity = target;

            if (entry.Removing) continue;
            entry.Marker.Visibility = (_cfg.ShowMarker && age == 0) ? Visibility.Visible : Visibility.Collapsed;
            AnimateOpacity(entry.Root, target);
        }
    }

    // ─── Скролл ленты (верхняя привязка) ─────────────────────────────────

    // Суммарная высота контента ленты, включая строки, ещё догорающие после
    // TrimExcess (они занимают место в layout до физического удаления из Children —
    // иначе на время фейд-аута лента «проседала» бы и нижняя строка клиповалась).
    // Высота строки для расчётов: после первого arrange — реальный RenderSize
    // (DesiredSize мог замериться по временной ширине до появления окна и
    // завысить контент → лишний скролл + пустота снизу; регрессия на Left/Right).
    private static double LineHeight(UIElement child)
    {
        double h = child.RenderSize.Height;
        return h > 0 ? h : child.DesiredSize.Height;
    }

    private double PanelContentHeight()
    {
        double h = 0;
        foreach (UIElement child in LinesPanel.Children)
            h += LineHeight(child);
        return h;
    }

    // Целевой Canvas.Top ленты. Якорь задаёт KaraokeConfig.ContentAnchor:
    // Bottom (дефолт) — контент ПРИЖАТ К НИЗУ: нижний край контента (без нижнего
    // margin) у нижнего края полосы (без нижнего padding'а). Контент короче
    // вьюпорта → t>0 — пустота остаётся СВЕРХУ (свежая строка у нижней кромки —
    // у взгляда; клэмп Min(t,0) давал верхний якорь и дыру снизу).
    // Top — верхний якорь: Canvas.Top=0, контент начинается сразу под верхним
    // padding'ом (Canvas.Top позиционирует край margin-box, ink = t + Margin.Top),
    // пустота уходит вниз — для высокой полосы (HeightPct=100) с короткой лентой.
    // Переполнение → t<0 — скролл вверх одинаково при любом якоре: свежая строка
    // у нижней кромки, старшие уезжают за верх. Вьюпорт = Window.Height: именно
    // её назначает UpdateElasticHeight синхронно, в отличие от Strip.ActualHeight,
    // которая догоняет layout'ом кадром позже.
    private double TargetPanelY()
    {
        double v = Height;
        if (double.IsNaN(v)) v = Strip.ActualHeight;
        if (double.IsNaN(v) || v <= 0) v = 200; // окно ещё не отмерено — безопасный фолбэк
        var m = LinesPanel.Margin;
        double bottom = v - m.Bottom - m.Top - PanelContentHeight();
        // Верхний якорь = Canvas.Top 0 (ink у верхнего padding'а); при переполнении
        // bottom < 0 → тот же скролл к нижней кромке.
        return string.Equals(_cfg.ContentAnchor, "Top", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(0.0, bottom)
            : bottom;
    }

    // Скролл ленты — layout-позиция верха LinesPanel внутри Strip-Canvas
    // (Canvas.Top), а НЕ RenderTransform: при большом отрицательном сдвиге
    // трансформного скролла WPF терял прорисовку детей StackPanel (лента
    // визуально становилась пустой при переполнении полосы).
    private double PanelY => Canvas.GetTop(LinesPanel);

    private void SetPanelY(double v)
    {
        LinesPanel.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetTop(LinesPanel, v);
    }

    // Запуск скролл-анимации ленты from → to с запоминанием цели (см. _panelTargetY).
    private void AnimatePanelTo(double from, double to)
    {
        var anim = new DoubleAnimation(from, to, SlideDuration)
        {
            EasingFunction = _easeOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        LinesPanel.BeginAnimation(Canvas.TopProperty, anim);
        _panelTargetY = to;
    }

    // Мгновенная посадка ленты на целевой скролл (конец фейд-аута строки,
    // ApplyConfig): удалённая строка освобождает место сверху — сдвиг цели на её
    // высоту ровно компенсирует layout-скок, видимые строки остаются на месте.
    private void SnapScroll()
    {
        double t = TargetPanelY();
        if (Math.Abs(t - PanelY) < 0.5) { _panelTargetY = t; return; }
        SetPanelY(t);
        _panelTargetY = t;
    }

    // Плавная реконсиляция скролла при изменении размеров (растущая стрим-строка,
    // смена высоты полосы). Не дёргаемся, если уже едем к актуальной цели — иначе
    // постоянные BeginAnimation из LayoutUpdated душили бы пуш-анимацию.
    private void ReconcileScroll()
    {
        if (_lines.Count == 0) return;
        double t = TargetPanelY();
        if (Math.Abs(t - _panelTargetY) > 0.5 && Math.Abs(t - PanelY) > 0.5)
            AnimatePanelTo(PanelY, t);
        else
            _panelTargetY = t;
    }

    // Физическое удаление строки из ленты с компенсацией скролла: строка уносит
    // свою высоту h — строки НИЖЕ неё поднимаются на h, +h к Y держит их на месте
    // (в том же dispatcher-ходе, до рендера — без кадра-глючка). Затем анимация
    // к новой цели закрывает освободившееся место (строки «сжимаются» вверх).
    // Для последней строки сдвигать нечего — при переполнении лишь пришиваем низ.
    private void RemoveLine(LineEntry entry)
    {
        double h = LineHeight(entry.Root);
        int idx = LinesPanel.Children.IndexOf(entry.Root);
        LinesPanel.Children.Remove(entry.Root);
        // Резина первая: пересчитывает Height/Top и компенсирует Y на сдвиг
        // якоря — компенсацию ниже (+h за освободившееся место) считаем уже
        // от скорректированного Y.
        UpdateElasticHeight();
        if (h <= 0 || idx < 0) return;
        double y = PanelY + h;
        double t = TargetPanelY();
        if (Math.Abs(y - t) > 0.5)
            AnimatePanelTo(y, t);
        else
        {
            SetPanelY(t);
            _panelTargetY = t;
        }
    }

    // Целевая прозрачность строки по возрасту: HistoryFade=false → всегда 1.0;
    // иначе таблица AgeOpacity линейно масштабируется так, чтобы самая старая
    // строка не опускалась ниже MinHistoryOpacity (читаемость истории).
    private double EffectiveOpacity(int age)
    {
        if (!_cfg.HistoryFade) return 1.0;
        double a = AgeOpacity[Math.Min(Math.Max(age, 0), AgeOpacity.Length - 1)];
        double floor = Clamp(_cfg.MinHistoryOpacity, 0.0, 1.0);
        return floor + a * (1.0 - floor);
    }

    // Верхний фейд-маск: HistoryFade=false → маска снята (все строки полной яркости);
    // иначе градиент с альфой верха = MinHistoryOpacity (самая старая строка не
    // уходит в ноль, как при исходной маске Transparent→Black).
    private void ApplyFadeMask()
        => LinesPanel.OpacityMask = _cfg.HistoryFade ? TopFadeMask(_cfg.MinHistoryOpacity) : null;

    /// <summary>Строки сверх лимита гаснут и удаляются по завершении анимации.
    /// Лимит = min(MaxLines, HistoryLineCount+1): свежая строка + не более
    /// HistoryLineCount старших над ней (высокая полоса не «съедает» историю).</summary>
    private void TrimExcess()
    {
        int excess = _lines.Count - Math.Min(Math.Max(_cfg.MaxLines, 1), Math.Max(_cfg.HistoryLineCount, 0) + 1);
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
            fadeOut.Completed += (_, _) => RemoveLine(entry);
            entry.Root.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    private void StyleLine(LineEntry entry)
    {
        var tb = entry.Tb;
        double fs = Clamp(_cfg.FontSize, 12, 64);
        tb.FontSize = fs;
        tb.FontWeight = FontWeights.Regular;
        tb.LineHeight = Math.Round(fs * 1.4);
        tb.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        tb.Foreground = BrushFor(entry, entry.Vm.Age);
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
    // TextColor/FreshColor/OldLinesColor/QuestionColor), не на каждый push.
    // При AgeColorFade=false в RefreshAges/StyleLine берётся _textBrush
    // (для роли "question" — всегда _questionBrush).
    private void RebuildAgeBrushes()
    {
        var text = ParseColor(_cfg.TextColor, Colors.White);
        _textBrush = Freeze(new SolidColorBrush(text));

        _questionBrush = FrozenBrush(_cfg.QuestionColor, Color.FromArgb(0xFF, 0xF2, 0x7A, 0x8A));

        var fresh = ParseColor(_cfg.FreshColor, Color.FromArgb(0xFF, 0xFF, 0xE9, 0xA8));
        // Битый/пустой hex → фолбэк на дефолт (золото/белый) через ParseColor.
        var old = ParseColor(_cfg.OldLinesColor, Colors.White);
        var brushes = new Brush[AgeOpacity.Length];
        for (int age = 0; age < brushes.Length; age++)
            brushes[age] = Freeze(new SolidColorBrush(AgeColor(age, fresh, old)));
        _ageBrushes = brushes;
    }

    // Кисть строки: роль "question" → _questionBrush (реплика собеседника должна
    // отличаться от ответа при любом фейде); остальные — градиент по возрасту
    // при AgeColorFade, иначе единый TextColor.
    private Brush BrushFor(LineEntry entry, int age)
        => entry.Vm.Role == "question"
            ? _questionBrush
            : (_cfg.AgeColorFade && _ageBrushes.Length > 0
                ? _ageBrushes[Math.Min(Math.Max(age, 0), _ageBrushes.Length - 1)]
                : _textBrush);

    // age0 = FreshColor (золото), age>=1 = OldLinesColor. TextColor в фейде не
    // участвует — ручки Fresh/Old полностью задают ленту (регрессия 2.7.0 лерпила
    // к TextColor: при чёрном TextColor старшие строки уходили в тёмное).
    // Плавность переходу даёт HistoryFade (альфа старших строк), лерп цвета убран.
    private static Color AgeColor(int age, Color fresh, Color old)
        => age <= 0 ? fresh : old;

    // ─── Позиционирование + резиновая высота ─────────────────────────────

    // Потолок/высота: auto-режим — FontSize*1.38*min(MaxLines,4) + 2*InnerPadding
    // (Top/Bottom), для вертикальных якорей — 70% рабочей области. HeightPct>0
    // задаёт ЖЁСТКУЮ высоту полосы в % экрана (см. UpdateElasticHeight).
    private double CeilingHeight(Rect wa)
    {
        double fs = Clamp(_cfg.FontSize, 12, 64);
        double pad = Clamp(_cfg.InnerPadding, 0, 80);
        double autoH = IsSidePosition() ? wa.Height * 0.7 : StripHeight(fs, pad);
        return BandHeight(wa.Height, autoH);
    }

    // Якорь Bottom → Top отсчитывается от низа (полоса прирастает ВВЕРХ);
    // Top → от верха (Top фиксирован — растёт вниз); Left/Right → вертикальный
    // центр (прирастает симметрично от середины, Left/Right не трогаем).
    private double ComputeTop(Rect wa, double h)
    {
        double margin = Clamp(_cfg.MarginPx, 0, 200);
        var pos = (_cfg.Position ?? "Bottom").Trim();
        if (pos.Equals("Top", StringComparison.OrdinalIgnoreCase))
            return wa.Top + margin;
        if (IsSidePosition())
            return wa.Top + (wa.Height - h) / 2;
        return wa.Bottom - margin - h; // Bottom (и дефолт)
    }

    private bool IsSidePosition()
    {
        var pos = (_cfg.Position ?? "Bottom").Trim();
        return pos.Equals("Left", StringComparison.OrdinalIgnoreCase)
            || pos.Equals("Right", StringComparison.OrdinalIgnoreCase);
    }

    // Высота полосы: HeightPct>0 → ЖЁСТКАЯ высота (% рабочей области — юзер задал
    // размер прямоугольника сам; внутри работает «Якорь текста»: короткая лента
    // у нижней или верхней кромки). HeightPct=0 → резиновая авто-высота:
    // Height = min(потолок, контент ленты + оба padding'а).
    // Пустая лента (нет строк вообще — включая догорающие) схлопывается в ноль:
    // «пустого прямоугольника» на экране нет; окно остаётся живым hwnd и
    // развернётся на первой же строке (Visibility не трогаем — гейт
    // CanShowRole по IsShown продолжает работать как раньше).
    // При смене высоты пересчитываем якорь (ComputeTop) и компенсируем
    // Canvas.Top ленты на сдвиг окна — видимые строки не дёргаются.
    private void UpdateElasticHeight()
    {
        var wa = SystemParameters.WorkArea;
        var m = LinesPanel.Margin;
        double padV = m.Top + m.Bottom;
        double ceiling = Math.Max(CeilingHeight(wa), 1.0);
        double targetH = LinesPanel.Children.Count == 0
            ? 0.0
            : _cfg.HeightPct > 0
                ? ceiling
                : Clamp(PanelContentHeight() + padV, 0.0, ceiling);

        double cur = double.IsNaN(Height) ? Strip.ActualHeight : Height;
        if (double.IsNaN(cur) || cur < 0) cur = 0;
        double newTop = ComputeTop(wa, targetH);
        if (Math.Abs(targetH - cur) > 0.5)
        {
            double dTop = double.IsNaN(Top) ? 0.0 : newTop - Top;
            Height = targetH;
            CompensateScrollShift(dTop);
            Top = newTop;
        }
        else if (double.IsNaN(Top) || Math.Abs(Top - newTop) > 0.5)
        {
            Top = newTop; // якорь/отступ сменили при той же высоте (или Top ещё не задан)
        }
    }

    // Окно поднялось/опустилось на dTop из-за прироста/сжатия резиновой высоты —
    // сдвигаем ленту обратно, чтобы контент остался на экранном месте (компенсация
    // в том же dispatcher-ходе, до рендера — без кадра-гличка). Бегущую анимацию
    // Y гасим: скролл пересчитается от нового вьюпорта (SnapScroll/ReconcileScroll
    // либо AnimatePanelTo из пуша — вызов идёт сразу следом).
    private void CompensateScrollShift(double dTop)
    {
        if (Math.Abs(dTop) < 0.5) return;
        SetPanelY(PanelY - dTop);
    }

    // Layout-проход по любой причине (стрим-дельта удлинила строку, фейд-аут
    // освободил место, Show/изменение размеров): резина подгоняет вьюпорт,
    // скролл дожимает ленту к нижнему краю.
    private void OnLayoutUpdated()
    {
        if (!IsShown) return; // скрытое окно не пересчитывает layout/скролл
        // Лента поменяла ширину (смена WidthPct/позиции, ресайс) — строки, чей
        // DesiredSize был замерен на старой ширине, иначе зависают (StackPanel
        // перевешивает детей только при смене собственного constraint). Трекаем
        // ActualWidth и инвалидируем measure — точные высоты без дыр снизу.
        double pw = LinesPanel.ActualWidth;
        if (pw > 0 && _lastPanelWidth >= 0 && Math.Abs(pw - _lastPanelWidth) > 0.5)
            LinesPanel.InvalidateMeasure();
        if (pw > 0) _lastPanelWidth = pw;
        SyncPanelWidth();
        UpdateElasticHeight();
        ReconcileScroll();
    }

    // Лента ДОЛЖНА быть уже стрипа на горизонтальный паддинг: Margin снаружи
    // добавляется к Width, иначе лента выходит за правый край окна и текст
    // режется (регрессия «текст уехал вправо за кулисы»). Strip.ActualWidth=0
    // до первого layout — тогда ставим NaN и дожмём на следующем проходе.
    private void SyncPanelWidth()
    {
        double sw = Strip.ActualWidth;
        if (sw <= 0)
        {
            LinesPanel.Width = double.NaN;
            return;
        }
        double pad = LinesPanel.Margin.Left + LinesPanel.Margin.Right;
        double w = Math.Max(1, sw - pad);
        if (!w.Equals(LinesPanel.Width)) LinesPanel.Width = w;
    }

    private void ApplyPosition()
    {
        var wa = SystemParameters.WorkArea;
        double margin = Clamp(_cfg.MarginPx, 0, 200);
        double width = wa.Width * Clamp(_cfg.WidthPct, 20, 100) / 100.0;

        Width = width;
        var pos = (_cfg.Position ?? "Bottom").Trim();
        if (pos.Equals("Left", StringComparison.OrdinalIgnoreCase))
            Left = wa.Left + margin;
        else if (pos.Equals("Right", StringComparison.OrdinalIgnoreCase))
            Left = wa.Right - width - margin;
        else
            Left = wa.Left + (wa.Width - width) / 2; // Top/Bottom — по центру

        UpdateElasticHeight(); // высота под контент + вертикальный якорь (Top)
    }

    // Потолок резиновой высоты для Top/Bottom: FontSize*1.38*min(MaxLines,4) + 2*InnerPadding (контракт §3).
    private double StripHeight(double fs, double pad)
        => fs * 1.38 * Math.Min(Math.Max(_cfg.MaxLines, 1), 4) + 2 * pad;

    // Высота полосы: HeightPct>0 → ЖЁСТКАЯ высота в % высоты рабочей области
    // для ВСЕХ позиций (у Left/Right — высота вертикальной полосы, WidthPct —
    // ширина; короткая лента внутри кладётся «Якорем текста»).
    // HeightPct=0 → autoH: Top/Bottom — StripHeight по шрифту/строкам,
    // Left/Right — 70%. OpacityMask верхнего фейда висит на LinesPanel
    // с относительными координатами (0→0.24): верх гасится пропорционально
    // высоте ленты при любой высоте полосы.
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
