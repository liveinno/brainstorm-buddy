using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BrainstormBuddy.Markdown;

/// <summary>
/// Разбор ответов LLM как Markdown: общий конвейер Markdig, безопасный разбор
/// и структурная проверка наличия разметки.
/// </summary>
public static class MarkdownSupport
{
    /// Общий конвейер разбора: CommonMark + pipe-таблицы + зачёркивание.
    /// Без автоссылок, без HTML, без «advanced extensions».
    /// </summary>
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build();

    /// Исходный текст документа. AST не хранит пустые строки между блоками,
    /// поэтому для текста без разметки plain-текст берём из источника — тогда он
    /// совпадает с исходником посимвольно.
    /// </summary>
    private static readonly ConditionalWeakTable<MarkdownDocument, string> Sources = new();

    /// Разобрать текст. null/пустая строка -> пустой документ (без исключений).
    /// </summary>
    [return: NotNull]
    public static MarkdownDocument Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkdownDocument();

        try
        {
            // Явно указываем Markdig.Markdown: в namespace BrainstormBuddy.Markdown
            // простое имя Markdown указывает на сам namespace.
            var document = Markdig.Markdown.Parse(text, Pipeline);
            Sources.Add(document, text);
            return document!;
        }
        catch (Exception)
        {
            // Разбор не должен ронять UI: любой сбой Markdig -> пустой документ.
            return new MarkdownDocument();
        }
    }

    /// Есть ли в разобранном тексте markdown-конструкции.
    /// true только для реальных конструкций: emphasis (bold/italic), strikethrough,
    /// heading, code (inline/block), link, image, quote, list, table, thematic break.
    /// false для одиночных «*», «_», «#», «-», «`», для текста без парных конструкций
    /// и для неразобранной/незакрытой разметки (незакрытая «**» -> текст остаётся текстом).
    /// </summary>
    /// <remarks>
    /// Параметр помечен как nullable: типы Markdig не аннотированы (null-oblivious),
    /// поэтому non-nullable параметр даёт CS8604 в каждом вызывающем коде.
    /// </remarks>
    public static bool ContainsMarkup(MarkdownDocument? doc)
    {
        if (doc is null)
            return false;

        foreach (var block in doc)
        {
            if (IsMarkupBlock(block))
                return true;
        }

        return false;
    }

    /// Текст без разметки — ровно то, что видно пользователю (без «**», «#», «- » и т.п.).
    /// Используется для копирования, поиска и как «plain»-представление.
    /// </summary>
    /// <remarks>
    /// Параметр помечен как nullable по той же причине, что и в <see cref="ContainsMarkup"/>.
    /// </remarks>
    public static string ToPlainText(MarkdownDocument? doc)
    {
        if (doc is null)
            return string.Empty;

        // Для текста без разметки visible-текст совпадает с исходником.
        if (!ContainsMarkup(doc) && Sources.TryGetValue(doc, out var source))
            return TrimEndNewlines(source);

        return MarkdownText.ToPlainText(doc);
    }

    /// Блок считается разметкой, если он меняет вид текста.
    /// Пустые конструкции («#», «*», «>» без содержимого) разметкой не считаются.
    /// </summary>
    private static bool IsMarkupBlock(Block block)
    {
        switch (block)
        {
            case CodeBlock:
                return true;

            case ThematicBreakBlock:
                return true;

            case HtmlBlock:
                // HTML из ответа не показывается — это тоже разметка.
                return true;

            case HeadingBlock heading:
                return MarkdownText.HasVisibleText(heading.Inline);

            case QuoteBlock quote:
                return quote.Count > 0;

            case ListBlock list:
                return list.Any(item => MarkdownText.HasVisibleContent(item));

            case Table table:
                return table.Count > 0;

            case LeafBlock leaf:
                return HasMarkupInlines(leaf.Inline);

            case ContainerBlock container:
                foreach (var child in container)
                {
                    if (IsMarkupBlock(child))
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

    /// Проверка inline-узлов: выделение, код, ссылки, картинки, HTML.
    /// </summary>
    private static bool HasMarkupInlines(ContainerInline? inline)
    {
        if (inline is null)
            return false;

        foreach (var child in inline)
        {
            switch (child)
            {
                case EmphasisInline:
                case CodeInline:
                case LinkInline:
                case AutolinkInline:
                case HtmlInline:
                    return true;

                case ContainerInline container:
                    if (HasMarkupInlines(container))
                        return true;
                    break;
            }
        }

        return false;
    }

    private static string TrimEndNewlines(string value)
    {
        var end = value.Length;
        while (end > 0 && (value[end - 1] == '\n' || value[end - 1] == '\r'))
            end--;

        return end == value.Length ? value : value[..end];
    }
}
