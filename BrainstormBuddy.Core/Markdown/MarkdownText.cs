using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BrainstormBuddy.Markdown;

/// <summary>
/// Извлечение видимого текста из AST Markdig: без служебной разметки
/// (маркеры списков и заголовков, звёздочки, вертикальные черты таблиц, теги HTML).
/// Публичный вход — <see cref="MarkdownSupport"/>.
/// </summary>
internal static class MarkdownText
{
    /// Разделитель между блоками верхнего уровня (абзацами, заголовками, списками).
    private const string BlockSeparator = "\n\n";

    /// Разделитель между элементами одного списка.
    private const char ItemSeparator = '\n';

    /// Разделитель между ячейками таблицы.
    private const char CellSeparator = '\t';

    /// Видимый текст документа без разметки.
    public static string ToPlainText(MarkdownDocument document)
    {
        var sb = new StringBuilder();
        AppendBlocks(sb, document, BlockSeparator);
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// Есть ли в узле видимый текст (не только пробелы).
    public static bool HasVisibleText(ContainerInline? inline)
    {
        if (inline is null)
            return false;

        var sb = new StringBuilder();
        AppendInlines(sb, inline);
        return !string.IsNullOrWhiteSpace(sb.ToString());
    }

    /// Есть ли в блоке видимое содержимое (текст, вложенные блоки).
    public static bool HasVisibleContent(Block block)
    {
        var sb = new StringBuilder();
        AppendBlock(sb, block);
        return !string.IsNullOrWhiteSpace(sb.ToString());
    }

    private static void AppendBlocks(StringBuilder sb, ContainerBlock container, string separator)
    {
        var first = true;
        foreach (var child in container)
        {
            if (!first)
                sb.Append(separator);
            first = false;
            AppendBlock(sb, child);
        }
    }

    private static void AppendBlock(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                AppendInlines(sb, paragraph.Inline);
                break;

            case HeadingBlock heading:
                AppendInlines(sb, heading.Inline);
                break;

            case CodeBlock code:
                AppendCodeLines(sb, code);
                break;

            case QuoteBlock quote:
                AppendBlocks(sb, quote, BlockSeparator);
                break;

            case ListBlock list:
                AppendList(sb, list);
                break;

            case Table table:
                AppendTable(sb, table);
                break;

            case ThematicBreakBlock:
                // Видимый разделитель: в тексте копирования остаётся горизонтальная линия.
                sb.Append("---");
                break;

            case HtmlBlock:
                // HTML из ответа не показываем.
                break;

            case LeafBlock leaf:
                AppendInlines(sb, leaf.Inline);
                break;

            case ContainerBlock container:
                AppendBlocks(sb, container, BlockSeparator);
                break;
        }
    }

    private static void AppendList(StringBuilder sb, ListBlock list)
    {
        var first = true;
        foreach (var item in list)
        {
            if (item is not ContainerBlock itemBlock)
                continue;

            if (!first)
                sb.Append(ItemSeparator);
            first = false;
            AppendBlocks(sb, itemBlock, BlockSeparator);
        }
    }

    private static void AppendTable(StringBuilder sb, Table table)
    {
        var firstRow = true;
        foreach (var row in table)
        {
            if (row is not TableRow tableRow)
                continue;

            if (!firstRow)
                sb.Append(ItemSeparator);
            firstRow = false;

            var firstCell = true;
            foreach (var cell in tableRow)
            {
                if (cell is not TableCell tableCell)
                    continue;

                if (!firstCell)
                    sb.Append(CellSeparator);
                firstCell = false;
                AppendBlocks(sb, tableCell, ItemSeparator.ToString());
            }
        }
    }

    private static void AppendCodeLines(StringBuilder sb, CodeBlock code)
    {
        var first = true;
        foreach (var line in code.Lines)
        {
            var text = line?.ToString() ?? string.Empty;
            if (text.Length == 0)
                continue;

            if (!first)
                sb.Append('\n');
            first = false;
            sb.Append(text.TrimEnd('\r'));
        }
    }

    private static void AppendInlines(StringBuilder sb, ContainerInline? inline)
    {
        if (inline is null)
            return;

        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;

                case CodeInline code:
                    sb.Append(code.Content);
                    break;

                case EmphasisInline emphasis:
                    // Маркеры выделения не видны пользователю.
                    AppendInlines(sb, emphasis);
                    break;

                case LinkInline link:
                    // Виден текст ссылки (для картинки — альтернативный текст), не сам URL.
                    AppendInlines(sb, link);
                    break;

                case AutolinkInline autolink:
                    sb.Append(autolink.Url);
                    break;

                case LineBreakInline:
                    sb.Append('\n');
                    break;

                case HtmlEntityInline entity:
                    sb.Append(entity.Transcoded.ToString());
                    break;

                case HtmlInline:
                    // HTML не исполняем и не показываем.
                    break;

                case PipeTableDelimiterInline:
                    // Одиночная «|» вне таблицы — обычный символ текста.
                    sb.Append('|');
                    break;

                case ContainerInline container:
                    AppendInlines(sb, container);
                    break;
            }
        }
    }
}
