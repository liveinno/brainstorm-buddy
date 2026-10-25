using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using BrainstormBuddy.Markdown;
using Markdig.Helpers;
using MdBlock = Markdig.Syntax.Block;
using MdCodeBlock = Markdig.Syntax.CodeBlock;
using MdContainerBlock = Markdig.Syntax.ContainerBlock;
using MdHeading = Markdig.Syntax.HeadingBlock;
using MdHtmlBlock = Markdig.Syntax.HtmlBlock;
using MdLeafBlock = Markdig.Syntax.LeafBlock;
using MdList = Markdig.Syntax.ListBlock;
using MdListItem = Markdig.Syntax.ListItemBlock;
using MdParagraph = Markdig.Syntax.ParagraphBlock;
using MdQuote = Markdig.Syntax.QuoteBlock;
using MdThematicBreak = Markdig.Syntax.ThematicBreakBlock;
using MdAutolink = Markdig.Syntax.Inlines.AutolinkInline;
using MdCodeInline = Markdig.Syntax.Inlines.CodeInline;
using MdContainerInline = Markdig.Syntax.Inlines.ContainerInline;
using MdEmphasis = Markdig.Syntax.Inlines.EmphasisInline;
using MdEntity = Markdig.Syntax.Inlines.HtmlEntityInline;
using MdHtmlInline = Markdig.Syntax.Inlines.HtmlInline;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdLineBreak = Markdig.Syntax.Inlines.LineBreakInline;
using MdLink = Markdig.Syntax.Inlines.LinkInline;
using MdLiteral = Markdig.Syntax.Inlines.LiteralInline;
using MdPipeDelimiter = Markdig.Extensions.Tables.PipeTableDelimiterInline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace BrainstormBuddy.Controls;

/// <summary>
/// Отображение ответа модели с markdown-разметкой.
///
/// Исходный текст с разметкой хранится в <see cref="Text"/> и нигде не переписывается:
/// форматирование применяется только при отрисовке, поэтому ответы, сохранённые до включения
/// фичи, открываются без миграции. Дословная транскрипция, имена файлов и редактируемые поля
/// этот компонент не используют — они остаются обычным текстом.
///
/// Разметка определяется структурно (AST Markdig): одиночные «*», «_», «#» не превращаются в
/// разметку, незакрытые конструкции не теряют текст и не вызывают исключений.
/// </summary>
public partial class MarkdownView : UserControl
{
    /// <summary>Текст ответа с исходной markdown-разметкой.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(default(string), OnTextPropertyChanged));

    /// <summary>Базовый кегль обычного текста, в пунктах.</summary>
    public static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.Register(
        nameof(BaseFontSize), typeof(double), typeof(MarkdownView),
        new FrameworkPropertyMetadata(13d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnBaseFontSizePropertyChanged));

    /// <summary>Высота строки обычного текста; <see cref="double.NaN"/> — системный интервал.</summary>
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight), typeof(double), typeof(MarkdownView),
        new FrameworkPropertyMetadata(double.NaN,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnLineHeightPropertyChanged));

    private const string ResourceCodeBackground = "MarkdownCodeBgBrush";
    private const string ResourceCodeBorder = "MarkdownCodeBorderBrush";
    private const string ResourceQuoteBar = "MarkdownQuoteBarBrush";
    private const string ResourceQuoteText = "MarkdownQuoteTextBrush";
    private const string ResourceTableBorder = "MarkdownTableBorderBrush";
    private const string ResourceTableHeader = "MarkdownTableHeaderBgBrush";
    private const string ResourceLink = "MarkdownLinkBrush";
    private const string ResourceHighlight = "SearchHighlightBrush";
    private const string ResourceMuted = "TextMutedBrush";
    private const string ResourceBodyText = "TextBrush";
    private const string ResourceAccent = "AccentBrush";
    private const string ResourceBorder = "BorderBrush";

    private readonly FlowDocument _document;
    private readonly List<Leaf> _leaves = new();
    private readonly Dictionary<Block, string> _widgetText = new();
    private readonly List<Leaf> _highlighted = new();
    private readonly StringBuilder _plain = new();

    private LeafStyle _paraStyle = new();
    private string _plainText = string.Empty;
    private bool _containsMarkup;
    private string _pendingText = string.Empty;
    private string _renderedText = string.Empty;
    private string _nextSeparator = "\n";
    private DispatcherTimer? _renderTimer;
    private DateTime _lastRenderUtc = DateTime.MinValue;
    private bool _renderScheduled;
    private int _selStart = -1;
    private int _selEnd = -1;

    public MarkdownView()
    {
        InitializeComponent();

        _document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = BaseFontSize,
        };
        Doc.Document = _document;

        // Неявный стиль RichTextBox задаёт системный чёрный Foreground, и Run без явного
        // цвета наследует именно его — на тёмной теме обычный текст ответа (абзацы и
        // подписи без собственного цвета) становился чёрным на тёмном фоне. Цвет задаём
        // на корне документа: его наследуют абзацы, строки и ячейки таблиц. Заголовки,
        // цитаты, маркеры списков и блоки кода по-прежнему переопределяют цвет сами.
        _document.SetResourceReference(TextElement.ForegroundProperty, ResourceBodyText);

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // Выделение и копирование делает сам RichTextBox (как раньше — TextBox с ответом).
        // Фокус, который он получает по клику, не должен «притягивать» страницу: иначе клик
        // по ответу прокручивает родительский ScrollViewer к началу блока.
        Doc.RequestBringIntoView += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Doc))
                e.Handled = true;
        };

        IsTabStop = false;

        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));

        // Своей прокрутки у ответа нет (она у родительского ScrollViewer), но RichTextBox всё
        // равно помечает колесо обработанным и страница под курсором замирает. Возвращаем
        // событие в маршрут: прокручивается тот ScrollViewer, внутри которого лежит ответ.
        Doc.AddHandler(Mouse.MouseWheelEvent, new MouseWheelEventHandler(OnDocMouseWheel), true);

        BuildContextMenu();
    }

    private void OnDocMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled)
            e.Handled = false;
    }

    /// <summary>
    /// Контекстное меню ответа: своё «Копировать» использует тот же сборщик видимого текста,
    /// что и Ctrl+C, поэтому в буфер попадают строки блоков кода и таблиц без разметки.
    /// </summary>
    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = T("L_Md_Copy", "Копировать") };
        copy.Click += (_, _) => CopyToClipboard(null);
        menu.Items.Add(copy);

        var selectAll = new MenuItem { Header = T("L_Md_SelectAll", "Выделить всё") };
        selectAll.Click += (_, _) =>
        {
            if (_plainText.Length == 0)
                return;
            Doc.Focus();
            // Настоящее выделение документа (не подсветка поиска): видимый текст вместе
            // с блоками кода и таблицами даёт сборщик CopyToClipboard.
            Doc.Selection.Select(Doc.Document.ContentStart, Doc.Document.ContentEnd);
        };
        menu.Items.Add(selectAll);

        menu.Opened += (_, _) =>
        {
            copy.IsEnabled = _plainText.Length > 0;
            selectAll.IsEnabled = _plainText.Length > 0;
        };

        Doc.ContextMenu = menu;
    }

    private static string T(string key, string fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

    /// <summary>Текст ответа с исходной markdown-разметкой.</summary>
    public string Text
    {
        get => (string?)GetValue(TextProperty) ?? string.Empty;
        set => SetValue(TextProperty, value);
    }

    /// <summary>Базовый кегль обычного текста, в пунктах.</summary>
    public double BaseFontSize
    {
        get => (double)GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    /// <summary>Высота строки обычного текста; <see cref="double.NaN"/> — системный интервал.</summary>
    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    /// <summary>Видимый текст без служебной разметки — то, что копируется в буфер обмена.</summary>
    public string PlainText => _plainText;

    /// <summary>Есть ли в <see cref="Text"/> разметка; если нет — вид как у обычного текста.</summary>
    public bool ContainsMarkup => _containsMarkup;

    /// <summary>Клик по ссылке в тексте. URL уже проверен: только http/https/mailto.</summary>
    public event EventHandler<MarkdownLinkEventArgs>? LinkClicked;

    /// <summary>
    /// Выделяет видимый диапазон по смещениям в <see cref="PlainText"/>.
    /// Вне диапазона ничего не делает, исключений не бросает.
    /// </summary>
    public void SelectPlainRange(int start, int length)
    {
        try
        {
            if (start < 0 || length <= 0)
            {
                ClearSelection();
                return;
            }

            long end = (long)start + length;
            if (end < start || start >= _plainText.Length)
                return;

            int last = (int)Math.Min(end, _plainText.Length);
            if (last <= start)
                return;

            ClearHighlights();
            foreach (var leaf in _leaves)
            {
                int leafEnd = leaf.Start + leaf.Length;
                int from = Math.Max(start, leaf.Start);
                int to = Math.Min(last, leafEnd);
                if (to <= from)
                    continue;

                ApplyHighlight(leaf, from - leaf.Start, to - from);
                if (!_highlighted.Contains(leaf))
                    _highlighted.Add(leaf);
            }

            _selStart = start;
            _selEnd = last;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MarkdownView] Не удалось выделить диапазон: " + ex.Message);
        }
    }

    /// <summary>Снимает выделение.</summary>
    public void ClearSelection()
    {
        _selStart = -1;
        _selEnd = -1;
        ClearHighlights();
    }

    // ---------- перерисовка ----------

    private static void OnTextPropertyChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)sender;
        view._pendingText = e.NewValue as string ?? string.Empty;
        view.ScheduleRender();
    }

    private static void OnBaseFontSizePropertyChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)sender;
        view.ScheduleRender();
    }

    private static void OnLineHeightPropertyChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)sender;
        view.ScheduleRender();
    }

    /// <summary>
    /// Перерисовка откладывается на диспетчер: во время стриминга текст меняется часто,
    /// и частота перерисовок ограничена примерно десятью разами в секунду.
    /// </summary>
    private void ScheduleRender()
    {
        if (_renderScheduled)
            return;

        _renderScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _renderScheduled = false;
            RenderThrottled();
        }));
    }

    private void RenderThrottled()
    {
        if ((DateTime.UtcNow - _lastRenderUtc).TotalMilliseconds < 100)
        {
            if (_renderTimer == null)
            {
                _renderTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(90),
                };
                _renderTimer.Tick += OnRenderTimerTick;
            }

            if (!_renderTimer.IsEnabled)
                _renderTimer.Start();
            return;
        }

        RenderNow();
    }

    private void OnRenderTimerTick(object? sender, EventArgs e)
    {
        _renderTimer?.Stop();
        RenderNow();
        if (!string.Equals(_pendingText, _renderedText, StringComparison.Ordinal))
            ScheduleRender();
    }

    private void RenderNow()
    {
        _lastRenderUtc = DateTime.UtcNow;
        var text = _pendingText;

        // Было ли уже отрисовано содержимое до этого прохода: на первой отрисовке
        // ScrollableHeight ещё нулевой и «прилипание к низу» уводило статичный ответ
        // (история, саммари) к концу, пряча его начало.
        bool hadContent = !string.IsNullOrEmpty(_renderedText);
        _renderedText = text;

        // Запоминаем позицию родительского скролла: перерисовка не должна «уводить» страницу.
        var scroll = FindParentScrollViewer();
        double offset = scroll?.VerticalOffset ?? 0d;

        bool atBottom = hadContent
            && (scroll is null
                || scroll.ScrollableHeight <= 0.5
                || offset >= scroll.ScrollableHeight - 0.5);

        try
        {
            BuildContent(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MarkdownView] Ошибка отрисовки, показываю исходный текст: " + ex.Message);
            BuildFallback(text);
        }

        if (scroll is null)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RestoreScroll(scroll, offset, atBottom)));
    }

    private static void RestoreScroll(ScrollViewer scroll, double offset, bool atBottom)
    {
        try
        {
            if (atBottom)
                scroll.ScrollToEnd();
            else
                scroll.ScrollToVerticalOffset(Math.Max(0d, Math.Min(offset, scroll.ScrollableHeight)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MarkdownView] Не удалось вернуть позицию скролла: " + ex.Message);
        }
    }

    private ScrollViewer? FindParentScrollViewer()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is ScrollViewer viewer)
                return viewer;

            current = GetParentObject(current);
        }

        return null;
    }

    private static DependencyObject? GetParentObject(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(child);

        return LogicalTreeHelper.GetParent(child);
    }

    // ---------- построение содержимого ----------

    private void BuildContent(string text)
    {
        _plain.Clear();
        _leaves.Clear();
        _widgetText.Clear();
        _highlighted.Clear();
        _document.Blocks.Clear();
        _document.FontSize = BaseFontSize;
        _document.LineHeight = LineHeight;
        _document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _nextSeparator = "\n";
        _selStart = -1;
        _selEnd = -1;
        _paraStyle = new LeafStyle { FontSize = BaseFontSize, Margin = new Thickness(0, 0, 0, 5) };

        var document = MarkdownSupport.Parse(text);
        _containsMarkup = MarkdownSupport.ContainsMarkup(document);

        if (!_containsMarkup)
        {
            // Обычный текст: тот же вид, что у простого TextBlock — без отступов и рамок.
            RenderPlainText(CollapseBlankLines(text));
            return;
        }

        foreach (var block in EnumerateBlocks(document))
            RenderBlock(block, _document.Blocks, 0);

        if (_leaves.Count == 0 && text.Length > 0)
        {
            // Ответ состоит только из HTML: теги не показываем, но и пустую карточку не оставляем.
            RenderPlainText(CollapseBlankLines(StripHtmlTags(text)));
            return;
        }

        _plainText = _plain.ToString();
    }

    private void RenderPlainText(string plain)
    {
        var paragraph = new Paragraph
        {
            FontSize = BaseFontSize,
            Margin = new Thickness(0),
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        AddPlainLines(paragraph.Inlines, plain);
        _document.Blocks.Add(paragraph);
        _plain.Clear();
        _plain.Append(plain);
        _plainText = plain;
        _leaves.Add(new Leaf
        {
            Paragraph = paragraph,
            Start = 0,
            Length = plain.Length,
            Segments = new List<Segment> { new Segment { Text = plain } },
        });
    }

    /// <summary>Обычный текст в Inlines: перевод строки — LineBreak, символы не трогаем.</summary>
    private static void AddPlainLines(InlineCollection inlines, string plain)
    {
        int start = 0;
        while (true)
        {
            int index = plain.IndexOf('\n', start);
            if (index < 0)
            {
                if (start < plain.Length)
                    inlines.Add(new Run(plain.Substring(start)));
                return;
            }

            if (index > start)
                inlines.Add(new Run(plain.Substring(start, index - start)));

            inlines.Add(new LineBreak());
            start = index + 1;
        }
    }

    private static string StripHtmlTags(string text)
    {
        try
        {
            return Regex.Replace(text, "<[^>]+>", string.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MarkdownView] Не удалось убрать HTML-теги: " + ex.Message);
            return text;
        }
    }

    private void BuildFallback(string text)
    {
        try
        {
            _plain.Clear();
            _leaves.Clear();
            _widgetText.Clear();
            _highlighted.Clear();
            _document.Blocks.Clear();

            var plain = CollapseBlankLines(text);
            _containsMarkup = false;
            RenderPlainText(plain);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MarkdownView] Резервный вывод не удался: " + ex.Message);
        }
    }

    /// <summary>Схлопывает пустые строки обычного текста: «\n\n» → «\n».</summary>
    private static string CollapseBlankLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        int newlines = 0;
        foreach (var ch in text)
        {
            if (ch == '\r')
                continue;

            if (ch == '\n')
            {
                newlines++;
                if (newlines > 1)
                    continue;

                builder.Append('\n');
                continue;
            }

            newlines = 0;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private void RenderBlock(MdBlock block, BlockCollection parent, int indent)
    {
        switch (block)
        {
            case MdHeading heading:
                RenderHeading(heading, parent, indent);
                return;
            case MdCodeBlock code:
                RenderCode(code, parent, indent);
                return;
            case MdThematicBreak:
                RenderThematicBreak(parent, indent);
                return;
            case MdHtmlBlock:
                return; // HTML из ответа не показываем и не выполняем.
            case MdParagraph paragraph:
                RenderParagraph(paragraph, parent);
                return;
            case MdList list:
                RenderList(list, parent, indent);
                return;
            case MdQuote quote:
                RenderQuote(quote, parent, indent);
                return;
            case MdTable table:
                RenderTable(table, parent, indent);
                return;
            case MdContainerBlock container:
                foreach (var child in EnumerateBlocks(container))
                    RenderBlock(child, parent, indent);
                return;
            case MdLeafBlock leaf:
                foreach (var line in BuildLines(leaf.Inline))
                    AddLeaf(line, parent, _paraStyle);
                return;
            default:
                return;
        }
    }

    private void RenderParagraph(MdParagraph paragraph, BlockCollection parent)
    {
        foreach (var line in BuildLines(paragraph.Inline))
            AddLeaf(line, parent, _paraStyle);
    }

    private void RenderHeading(MdHeading heading, BlockCollection parent, int indent)
    {
        double factor = heading.Level switch
        {
            1 => 1.6,
            2 => 1.4,
            3 => 1.25,
            4 => 1.15,
            5 => 1.08,
            _ => 1.0,
        };

        var style = new LeafStyle
        {
            FontSize = Math.Max(8d, BaseFontSize * factor),
            Weight = FontWeights.Bold,
            Margin = new Thickness(indent * 18, 10, 0, 3),
        };

        foreach (var line in BuildLines(heading.Inline))
            AddLeaf(line, parent, style);
    }

    private void RenderList(MdList list, BlockCollection parent, int indent)
    {
        // Маркер — часть первого абзаца пункта: так он остаётся текстом (выделяется и
        // копируется вместе с пунктом), а перенос строк выравнивается с текстом, а не с маркером.
        int number = ParseOrderedStart(list);
        foreach (var item in EnumerateBlocks(list))
        {
            if (item is not MdListItem listItem)
                continue;

            string marker = list.IsOrdered
                ? number.ToString(CultureInfo.InvariantCulture) + "."
                : "•";

            bool rendered = false;
            foreach (var child in EnumerateBlocks(listItem))
            {
                if (!rendered && child is MdParagraph firstParagraph)
                {
                    var style = new LeafStyle
                    {
                        FontSize = BaseFontSize,
                        Margin = new Thickness(indent * 18 + 18, 0, 0, 3),
                        TextIndent = -18,
                    };

                    foreach (var line in BuildLines(firstParagraph.Inline))
                        AddLeaf(line, parent, style, prefix: marker);

                    rendered = true;
                    continue;
                }

                rendered = true;
                RenderBlock(child, parent, indent + 1);
            }

            if (!rendered)
            {
                AddLeaf(new List<Segment>(), parent, new LeafStyle
                {
                    FontSize = BaseFontSize,
                    Margin = new Thickness(indent * 18 + 18, 0, 0, 3),
                    TextIndent = -18,
                }, prefix: marker);
            }

            number++;
        }
    }

    /// <summary>Номер первого пункта нумерованного списка (в Markdig это строка).</summary>
    private static int ParseOrderedStart(MdList list)
    {
        if (int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            && start > 0)
        {
            return start;
        }

        return 1;
    }

    private void RenderQuote(MdQuote quote, BlockCollection parent, int indent)
    {
        // Section — вложенный блок со своей рамкой: полоса слева достаётся всем абзацам цитаты,
        // а текст внутри остаётся обычными Paragraph (выделяется и копируется).
        var section = new Section
        {
            Margin = new Thickness(indent * 18, 5, 0, 5),
            Padding = new Thickness(10, 0, 0, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };
        section.SetResourceReference(Block.BorderBrushProperty, ResourceQuoteBar);
        parent.Add(section);

        var saved = _paraStyle;
        _paraStyle = new LeafStyle
        {
            FontSize = BaseFontSize,
            ForegroundResource = ResourceQuoteText,
            Margin = new Thickness(0, 0, 0, 5),
        };

        try
        {
            foreach (var child in EnumerateBlocks(quote))
                RenderBlock(child, section.Blocks, indent);
        }
        finally
        {
            _paraStyle = saved;
        }
    }

    private void RenderCode(MdCodeBlock code, BlockCollection parent, int indent)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        border.SetResourceReference(Border.BackgroundProperty, ResourceCodeBackground);
        border.SetResourceReference(Border.BorderBrushProperty, ResourceCodeBorder);

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var lines = new List<string>();
        foreach (var line in EnumerateCodeLines(code))
            lines.Add(line);

        // Текст кода — обычный (не markdown) и выделяется как текст: read-only TextBox без рамки.
        var box = new TextBox
        {
            Text = string.Join("\n", lines),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            FontSize = BaseFontSize,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // Блок кода — не поле ввода: неявный стиль TextBox (MinHeight 36, фон панели,
        // фокусное кольцо) сплющил бы короткие блоки и спорил с собственной рамкой кода.
        // Без стиля TextBox берёт системный чёрный Foreground — на тёмной теме это
        // невидимый код, поэтому цвет текста, каретки и выделения задаём явно.
        box.SetValue(StyleProperty, null);
        box.SetResourceReference(TextBox.ForegroundProperty, ResourceBodyText);
        box.SetResourceReference(TextBox.SelectionBrushProperty, ResourceAccent);

        scroller.Content = box;
        border.Child = scroller;

        var codeContainer = new BlockUIContainer(border)
        {
            Margin = new Thickness(indent * 18, 6, 0, 6),
        };
        _widgetText[codeContainer] = string.Join("\n", lines);
        parent.Add(codeContainer);

        foreach (var line in lines)
        {
            // Пустая строка кода сохраняет высоту и участвует в видимом тексте как пустая строка.
            var segments = line.Length > 0
                ? new List<Segment> { new Segment { Text = line } }
                : new List<Segment>();

            int start = AppendPlain(line);
            _leaves.Add(new Leaf { Start = start, Length = line.Length, Segments = segments });
        }
    }

    private void RenderThematicBreak(BlockCollection parent, int indent)
    {
        // Горизонтальная линия — абзац с нижней рамкой: в BlockCollection он растягивается по ширине.
        var rule = new Paragraph
        {
            Margin = new Thickness(indent * 18, 4, 0, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
            FontSize = BaseFontSize,
        };
        rule.SetResourceReference(Block.BorderBrushProperty, ResourceBorder);
        parent.Add(rule);
    }

    private void RenderTable(MdTable table, BlockCollection parent, int indent)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        border.SetResourceReference(Border.BorderBrushProperty, ResourceTableBorder);

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var grid = new Grid();
        scroller.Content = grid;
        border.Child = scroller;

        var tableContainer = new BlockUIContainer(border)
        {
            Margin = new Thickness(indent * 18, 6, 0, 6),
        };
        parent.Add(tableContainer);

        var tableText = new StringBuilder();

        int rowIndex = 0;
        foreach (var row in EnumerateBlocks(table))
        {
            if (row is not MdTableRow tableRow)
                continue;

            int columnIndex = 0;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int gridRowIndex = grid.RowDefinitions.Count - 1;

            foreach (var cell in EnumerateBlocks(tableRow))
            {
                if (cell is not MdTableCell)
                    continue;

                while (grid.ColumnDefinitions.Count <= columnIndex)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isHeader = tableRow.IsHeader || rowIndex == 0;

                var cellBorder = new Border
                {
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 4, 8, 4),
                    MinWidth = 40,
                };
                cellBorder.SetResourceReference(Border.BorderBrushProperty, ResourceTableBorder);
                if (isHeader)
                    cellBorder.SetResourceReference(Border.BackgroundProperty, ResourceTableHeader);

                var cellPanel = new StackPanel { Orientation = Orientation.Vertical };
                cellBorder.Child = cellPanel;
                Grid.SetColumn(cellBorder, columnIndex);
                Grid.SetRow(cellBorder, gridRowIndex);
                grid.Children.Add(cellBorder);

                if (columnIndex > 0)
                    _nextSeparator = "\t";

                var cellText = new StringBuilder();
                foreach (var child in EnumerateBlocks(cell))
                    cellText.Append(RenderCellBlock(child, cellPanel, isHeader));

                if (cellText.Length > 0)
                {
                    if (columnIndex > 0)
                        tableText.Append('\t');
                    tableText.Append(cellText);
                }

                columnIndex++;
            }

            if (columnIndex == 0)
            {
                // строка без ячеек - определение строки убираем
                grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
                continue;
            }

            tableText.Append('\n');
            rowIndex++;
            _nextSeparator = "\n";
        }

        _widgetText[tableContainer] = tableText.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Блок внутри ячейки таблицы: ячейка — StackPanel, поэтому markdown разворачивается в
    /// плоский список TextBlock (без вложенных Paragraph: они живут только в FlowDocument).
    /// </summary>
    private string RenderCellBlock(MdBlock block, Panel parent, bool bold)
    {
        var text = new StringBuilder();

        switch (block)
        {
            case MdParagraph paragraph:
                foreach (var line in BuildLines(paragraph.Inline))
                    text.Append(AddCellLeaf(line, parent, bold, null));
                break;
            case MdHeading heading:
                foreach (var line in BuildLines(heading.Inline))
                    text.Append(AddCellLeaf(line, parent, true, null));
                break;
            case MdList list:
                int number = ParseOrderedStart(list);
                foreach (var item in EnumerateBlocks(list))
                {
                    if (item is not MdListItem listItem)
                        continue;

                    string marker = list.IsOrdered
                        ? number.ToString(CultureInfo.InvariantCulture) + "."
                        : "•";

                    foreach (var child in EnumerateBlocks(listItem))
                    {
                        if (child is MdParagraph itemParagraph)
                        {
                            foreach (var line in BuildLines(itemParagraph.Inline))
                                text.Append(AddCellLeaf(line, parent, bold, marker));
                        }
                        else
                        {
                            text.Append(RenderCellBlock(child, parent, bold));
                        }
                    }

                    number++;
                }

                break;
            case MdCodeBlock code:
                foreach (var line in EnumerateCodeLines(code))
                {
                    var segments = line.Length > 0
                        ? new List<Segment> { new Segment { Text = line } }
                        : new List<Segment>();
                    text.Append(AddCellLeaf(segments, parent, bold, null));
                }

                break;
            case MdQuote quote:
                foreach (var child in EnumerateBlocks(quote))
                    text.Append(RenderCellBlock(child, parent, bold));
                break;
            case MdContainerBlock container:
                foreach (var child in EnumerateBlocks(container))
                    text.Append(RenderCellBlock(child, parent, bold));
                break;
            case MdLeafBlock leaf:
                foreach (var line in BuildLines(leaf.Inline))
                    text.Append(AddCellLeaf(line, parent, bold, null));
                break;
            default:
                // Вложенная таблица/HR внутри ячейки не поддерживаются — не рисуем, но не падаем.
                break;
        }

        return text.ToString();
    }

    // ---------- инлайны ----------

    private sealed class LineBuilder
    {
        public List<List<Segment>> Lines = new();
        public List<Segment> Current = new();

        public void Break()
        {
            Lines.Add(Current);
            Current = new List<Segment>();
        }
    }

    private readonly record struct Fmt(bool Bold, bool Italic, bool Strike, bool Code, string? Url);

    private List<List<Segment>> BuildLines(MdContainerInline? inline)
    {
        var builder = new LineBuilder();
        if (inline is not null)
            Walk(EnumerateInlines(inline), default, builder);

        builder.Lines.Add(builder.Current);
        return builder.Lines;
    }

    private void Walk(IEnumerable<MdInline> inlines, Fmt fmt, LineBuilder builder)
    {
        foreach (var inline in inlines)
        {
            if (inline is null)
                continue;

            switch (inline)
            {
                case MdLineBreak:
                    builder.Break();
                    continue;

                case MdHtmlInline:
                    continue; // HTML из ответа не показываем.

                case MdEntity entity:
                    AddSegment(builder.Current, entity.Transcoded.ToString(), fmt);
                    continue;

                case MdCodeInline code:
                    AddSegment(builder.Current, code.Content ?? string.Empty, fmt with { Code = true });
                    continue;

                case MdLiteral literal:
                    AddSegment(builder.Current, literal.Content.ToString(), fmt);
                    continue;

                case MdPipeDelimiter:
                    AddSegment(builder.Current, "|", fmt);
                    continue;

                case MdEmphasis emphasis:
                    Walk(EnumerateInlines(emphasis), FormatEmphasis(fmt, emphasis), builder);
                    continue;

                case MdLink link:
                    RenderLink(link, fmt, builder);
                    continue;

                case MdAutolink autolink:
                    RenderAutolink(autolink, fmt, builder);
                    continue;

                case MdContainerInline container:
                    Walk(EnumerateInlines(container), fmt, builder);
                    continue;

                default:
                    var raw = inline.ToString();
                    if (!string.IsNullOrEmpty(raw))
                        AddSegment(builder.Current, raw, fmt);
                    continue;
            }
        }
    }

    /// <summary>«**» — жирный, «*» — курсив, «~~» — зачёркнутый.</summary>
    private static Fmt FormatEmphasis(Fmt fmt, MdEmphasis emphasis)
    {
        if (emphasis.DelimiterChar == '~')
            return fmt with { Strike = true };

        return emphasis.DelimiterCount >= 2
            ? fmt with { Bold = true }
            : fmt with { Italic = true };
    }

    private void RenderLink(MdLink link, Fmt fmt, LineBuilder builder)
    {
        var url = link.Url;
        var label = EnumerateInlines(link).ToList();

        if (link.IsImage)
        {
            // Картинку не показываем, оставляем alt-текст обычным текстом.
            if (label.Count > 0)
                Walk(label, fmt, builder);
            else
                AddSegment(builder.Current, link.Label ?? url ?? string.Empty, fmt);
            return;
        }

        if (IsSafeUrl(url))
        {
            var next = fmt with { Url = url };
            if (label.Count > 0)
                Walk(label, next, builder);
            else
                AddSegment(builder.Current, url ?? string.Empty, next);
            return;
        }

        // Небезопасная схема (javascript:, data:, file:) — обычный текст, без подсветки ссылки.
        if (label.Count > 0)
            Walk(label, fmt, builder);
        else
            AddSegment(builder.Current, link.Label ?? url ?? string.Empty, fmt);
    }

    private void RenderAutolink(MdAutolink autolink, Fmt fmt, LineBuilder builder)
    {
        var url = autolink.Url ?? string.Empty;
        var target = autolink.IsEmail ? "mailto:" + url : url;
        if (IsSafeUrl(target))
            AddSegment(builder.Current, url, fmt with { Url = target });
        else
            AddSegment(builder.Current, url, fmt);
    }

    private static void AddSegment(List<Segment> list, string text, Fmt fmt)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (list.Count > 0)
        {
            var last = list[^1];
            if (last.Bold == fmt.Bold
                && last.Italic == fmt.Italic
                && last.Strike == fmt.Strike
                && last.Code == fmt.Code
                && string.Equals(last.Url, fmt.Url, StringComparison.Ordinal))
            {
                last.Text += text;
                return;
            }
        }

        list.Add(new Segment
        {
            Text = text,
            Bold = fmt.Bold,
            Italic = fmt.Italic,
            Strike = fmt.Strike,
            Code = fmt.Code,
            Url = fmt.Url,
        });
    }

    // ---------- листья текста ----------

    private sealed class LeafStyle
    {
        public double FontSize { get; set; } = 13d;
        public FontWeight Weight { get; set; } = FontWeights.Normal;
        public string? ForegroundResource { get; set; }
        public TextWrapping Wrapping { get; set; } = TextWrapping.Wrap;
        public Thickness Margin { get; set; } = new Thickness(0, 0, 0, 5);

        /// <summary>Отрицательный отступ первой строки: маркер списка висит в поле отступа.</summary>
        public double TextIndent { get; set; }
    }

    private sealed class Segment
    {
        public string Text { get; set; } = string.Empty;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Strike { get; set; }
        public bool Code { get; set; }
        public string? Url { get; set; }
    }

    private sealed class Leaf
    {
        /// <summary>Абзац FlowDocument — основной носитель текста (выделение и копирование штатные).</summary>
        public Paragraph? Paragraph { get; set; }

        /// <summary>Ячейка таблицы — TextBlock внутри BlockUIContainer.</summary>
        public TextBlock? CellBlock { get; set; }

        /// <summary>Маркер списка, нарисованный перед текстом (в видимом тексте тоже есть).</summary>
        public string? Prefix { get; set; }

        public List<Segment> Segments { get; set; } = new();
        public int Start { get; set; }
        public int Length { get; set; }
    }

    private Paragraph CreateParagraph(LeafStyle style)
    {
        var paragraph = new Paragraph
        {
            FontSize = style.FontSize,
            FontWeight = style.Weight,
            Margin = style.Margin,
            TextIndent = style.TextIndent,
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };

        if (style.ForegroundResource is not null)
            paragraph.SetResourceReference(TextElement.ForegroundProperty, style.ForegroundResource);

        return paragraph;
    }

    private TextBlock CreateTextBlock(LeafStyle style)
    {
        var block = new TextBlock
        {
            TextWrapping = style.Wrapping,
            FontSize = style.FontSize,
            FontWeight = style.Weight,
            Margin = style.Margin,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        if (style.ForegroundResource is not null)
            block.SetResourceReference(TextElement.ForegroundProperty, style.ForegroundResource);

        TextBlock.SetLineHeight(block, LineHeight);
        TextBlock.SetLineStackingStrategy(block, LineStackingStrategy.BlockLineHeight);
        return block;
    }

    /// <summary>Дописывает текст в видимый PlainText с текущим разделителем и вернёт его начало.</summary>
    private int AppendPlain(string text)
    {
        if (_plain.Length > 0)
            _plain.Append(_nextSeparator);

        _nextSeparator = "\n";

        int start = _plain.Length;
        _plain.Append(text);
        return start;
    }

    private static string BuildLeafText(string? prefix, List<Segment> segments, string? plainTextOverride)
    {
        var text = plainTextOverride ?? string.Concat(segments.Select(s => s.Text));
        return string.IsNullOrEmpty(prefix) ? text : prefix + " " + text;
    }

    private void AddLeaf(
        List<Segment> segments,
        BlockCollection parent,
        LeafStyle style,
        string? plainTextOverride = null,
        string? prefix = null)
    {
        if (segments.Count == 0 && string.IsNullOrEmpty(prefix))
            return;

        var paragraph = CreateParagraph(style);

        if (!string.IsNullOrEmpty(prefix))
        {
            var marker = new Run(prefix + " ");
            marker.SetResourceReference(TextElement.ForegroundProperty, ResourceMuted);
            paragraph.Inlines.Add(marker);
        }

        foreach (var segment in segments)
            paragraph.Inlines.Add(CreateInline(segment, segment.Text));

        parent.Add(paragraph);

        var text = BuildLeafText(prefix, segments, plainTextOverride);
        _leaves.Add(new Leaf
        {
            Paragraph = paragraph,
            Prefix = prefix,
            Start = AppendPlain(text),
            Length = text.Length,
            Segments = segments,
        });
    }

    private string AddCellLeaf(List<Segment> segments, Panel parent, bool bold, string? prefix)
    {
        var block = CreateTextBlock(new LeafStyle
        {
            FontSize = BaseFontSize,
            Weight = bold ? FontWeights.Bold : FontWeights.Normal,
            Margin = new Thickness(0),
        });

        if (!string.IsNullOrEmpty(prefix))
        {
            var marker = new Run(prefix + " ");
            marker.SetResourceReference(TextElement.ForegroundProperty, ResourceMuted);
            block.Inlines.Add(marker);
        }

        foreach (var segment in segments)
            block.Inlines.Add(CreateInline(segment, segment.Text));

        parent.Children.Add(block);

        var text = BuildLeafText(prefix, segments, null);
        _leaves.Add(new Leaf
        {
            CellBlock = block,
            Prefix = prefix,
            Start = AppendPlain(text),
            Length = text.Length,
            Segments = segments,
        });

        return text;
    }

    private Inline CreateInline(Segment segment, string text, string? backgroundResource = null)
    {
        if (!string.IsNullOrEmpty(segment.Url) && TryCreateUri(segment.Url, out var uri))
        {
            var link = new Hyperlink { NavigateUri = uri };
            link.RequestNavigate += OnLinkRequestNavigate;
            link.SetResourceReference(TextElement.ForegroundProperty, ResourceLink);
            link.TextDecorations = TextDecorations.Underline;
            link.Inlines.Add(CreateRun(segment, text, backgroundResource));
            return link;
        }

        return CreateRun(segment, text, backgroundResource);
    }

    private static Run CreateRun(Segment segment, string text, string? backgroundResource = null)
    {
        var run = new Run(text);
        if (segment.Bold)
            run.FontWeight = FontWeights.Bold;
        if (segment.Italic)
            run.FontStyle = FontStyles.Italic;
        if (segment.Strike)
            run.TextDecorations = TextDecorations.Strikethrough;
        if (segment.Code)
        {
            // Inline-код: тот же шрифт, что у основного текста, но на фоне блока кода.
            run.SetResourceReference(TextElement.BackgroundProperty, ResourceCodeBackground);
            run.FontWeight = FontWeights.Normal;
        }

        if (backgroundResource is not null)
            run.SetResourceReference(TextElement.BackgroundProperty, backgroundResource);

        return run;
    }

    private void ApplyHighlight(Leaf leaf, int localStart, int localLength)
    {
        InlineCollection? inlines = leaf.Paragraph?.Inlines ?? leaf.CellBlock?.Inlines;
        if (inlines is null)
            return;

        inlines.Clear();

        if (!string.IsNullOrEmpty(leaf.Prefix))
        {
            var marker = new Run(leaf.Prefix + " ");
            marker.SetResourceReference(TextElement.ForegroundProperty, ResourceMuted);
            inlines.Add(marker);
        }

        int position = 0;
        int end = localStart + localLength;
        foreach (var segment in leaf.Segments)
        {
            int segmentStart = position;
            int segmentEnd = position + segment.Text.Length;
            position = segmentEnd;

            int from = Math.Max(localStart, segmentStart);
            int to = Math.Min(end, segmentEnd);
            if (to <= from)
            {
                inlines.Add(CreateInline(segment, segment.Text));
                continue;
            }

            int head = from - segmentStart;
            int tail = to - segmentStart;
            if (head > 0)
                inlines.Add(CreateInline(segment, segment.Text.Substring(0, head)));

            var highlighted = CreateInline(segment, segment.Text.Substring(head, tail - head), ResourceHighlight);
            inlines.Add(highlighted);

            if (tail < segment.Text.Length)
                inlines.Add(CreateInline(segment, segment.Text.Substring(tail)));
        }
    }

    private void ClearHighlights()
    {
        foreach (var leaf in _highlighted)
            ApplyHighlight(leaf, 0, 0);

        _highlighted.Clear();
    }

    // ---------- ссылки ----------

    private void OnLinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Открывать должен вызывающий код: снимаем стандартное поведение WPF.
        e.Handled = true;

        var url = e.Uri?.AbsoluteUri;
        if (!IsSafeUrl(url))
            return;

        var handler = LinkClicked;
        if (handler is not null)
        {
            handler.Invoke(this, new MarkdownLinkEventArgs(url!));
            return;
        }

        OpenInShell(url!);
    }

    private static void OpenInShell(string url)
    {
        try
        {
            var info = new ProcessStartInfo(url) { UseShellExecute = true };
            using var process = Process.Start(info);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MarkdownView] Не удалось открыть ссылку: " + ex.Message);
        }
    }

    /// <summary>Безопасные схемы: только http, https и mailto.</summary>
    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        foreach (var ch in url)
        {
            if (char.IsControl(ch))
                return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var scheme = uri.Scheme;
        return scheme == Uri.UriSchemeHttp
            || scheme == Uri.UriSchemeHttps
            || scheme == Uri.UriSchemeMailto;
    }

    private static bool TryCreateUri(string url, out Uri? uri)
    {
        uri = null;
        if (!IsSafeUrl(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;

        uri = parsed;
        return true;
    }

    // ---------- копирование ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            return;

        // Код в блоке — обычный TextBox: там Ctrl+C обрабатывает сам редактор,
        // иначе выделенный кусок кода заменялся бы всем ответом. Сам ответ (Doc) —
        // тоже TextBoxBase, но его выделение копирует наш сборщик видимого текста.
        if (!ShouldHandleCopy(Keyboard.FocusedElement))
            return;

        if (CopyToClipboard(null))
            e.Handled = true;
    }

    /// <summary>
    /// Ctrl+C обрабатывает компонент, если фокус не в чужом поле ввода: текст ответа
    /// (Doc) и отсутствие фокуса — наши, а блок кода — отдельный TextBox со своим копированием.
    /// </summary>
    internal bool ShouldHandleCopy(object? focused) =>
        focused is not System.Windows.Controls.Primitives.TextBoxBase tb || ReferenceEquals(tb, Doc);

    /// <summary>
    /// Кладёт в буфер видимый текст: выделенный мышью фрагмент, программно подсвеченный
    /// диапазон или, если выделения нет, весь ответ. Штатное копирование RichTextBox
    /// не берёт строки блоков кода и таблиц — они вне TextRange.
    /// </summary>
    private bool CopyToClipboard(object? _)
    {
        if (!Doc.Selection.IsEmpty)
        {
            var selected = BuildSelectionText();
            if (selected.Length > 0)
                return SetClipboard(selected);
        }

        string text;
        if (_selStart >= 0 && _selEnd > _selStart)
        {
            int from = Math.Min(_selStart, _plainText.Length);
            int to = Math.Min(_selEnd, _plainText.Length);
            if (to <= from)
                return false;
            text = _plainText[from..to];
        }
        else
        {
            if (_plainText.Length == 0)
                return false;
            text = _plainText;
        }

        return SetClipboard(text);
    }

    private static bool SetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MarkdownView] Не удалось скопировать текст: " + ex.Message);
            return false;
        }
    }

    /// <summary>Видимый текст текущего выделения, включая блоки кода и таблицы.</summary>
    private string BuildSelectionText()
    {
        var builder = new StringBuilder();
        AppendBlockText(_document.Blocks, Doc.Selection.Start, Doc.Selection.End, builder);
        return builder.ToString().Trim('\n', '\r', ' ', '\t');
    }

    private void AppendBlockText(BlockCollection blocks, TextPointer start, TextPointer end, StringBuilder builder)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    var from = paragraph.ContentStart.CompareTo(start) < 0 ? start : paragraph.ContentStart;
                    var to = paragraph.ContentEnd.CompareTo(end) > 0 ? end : paragraph.ContentEnd;
                    if (from.CompareTo(to) >= 0)
                        continue;

                    // Блок — отдельная строка: TextRange внутри одного абзаца не даёт перевод строки.
                    builder.Append(new TextRange(from, to).Text).Append('\n');
                    break;
                case Section section:
                    AppendBlockText(section.Blocks, start, end, builder);
                    break;
                case BlockUIContainer container:
                    if (!_widgetText.TryGetValue(container, out var widget))
                        break;

                    // Код и таблица — неделимые виджеты: берём текст целиком, если выделение их задело.
                    if (container.ContentEnd.CompareTo(start) <= 0 || container.ContentStart.CompareTo(end) >= 0)
                        break;

                    builder.Append(widget).Append('\n');
                    break;
            }
        }
    }

    // ---------- обход дерева Markdig ----------

    private static IEnumerable<MdBlock> EnumerateBlocks(object? collection)
    {
        if (collection is IEnumerable<MdBlock> typed)
            return typed;

        if (collection is IEnumerable sequence)
        {
            var list = new List<MdBlock>();
            foreach (var item in sequence)
            {
                if (item is MdBlock block)
                    list.Add(block);
            }

            return list;
        }

        return Array.Empty<MdBlock>();
    }

    private static IEnumerable<MdInline> EnumerateInlines(object? collection)
    {
        if (collection is IEnumerable<MdInline> typed)
            return typed;

        if (collection is IEnumerable sequence)
        {
            var list = new List<MdInline>();
            foreach (var item in sequence)
            {
                if (item is MdInline inline)
                    list.Add(inline);
            }

            return list;
        }

        return Array.Empty<MdInline>();
    }

    /// <summary>Строки блока кода: в Markdig это группы строк исходника.</summary>
    private static IEnumerable<string> EnumerateCodeLines(MdCodeBlock code)
    {
        foreach (var item in code.Lines)
        {
            if (item is StringLine line)
            {
                yield return line.Slice.ToString().TrimEnd('\r');
                continue;
            }

            yield return Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}

/// <summary>Аргументы клика по ссылке в тексте ответа.</summary>
public sealed class MarkdownLinkEventArgs : EventArgs
{
    public MarkdownLinkEventArgs(string url)
    {
        Url = url;
    }

    /// <summary>Адрес ссылки: только http, https или mailto.</summary>
    public string Url { get; }
}
