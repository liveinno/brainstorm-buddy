using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Извлечение текста из DOCX/XLSX/PPTX через Open XML SDK.
/// DOCX: абзацы (заголовки → якорь «§ …») и таблицы в порядке документа;
/// удалённые правки, колонтитулы, комментарии не читаем.
/// XLSX: только видимые листы/строки/столбцы, адреса ячеек, shared/inline
/// строки, числа/даты, сохранённые результаты формул (формулы не вычисляем;
/// без кэша помечаем «результат не сохранён»).
/// PPTX: текст слайдов и таблиц по порядку слайдов; заметки докладчика и
/// скрытые слайды пропускаем с предупреждением.
/// Любая порча/шифрование пакета — <see cref="AttachmentRejectedException"/>.
/// </summary>
internal static class OpenXmlExtractor
{
    public static Task<ExtractedDocument> ExtractAsync(
        string storedPath, DocumentKind kind, string displayName, CancellationToken ct)
    {
        return Task.Run(() => kind switch
        {
            DocumentKind.Docx => ExtractDocx(storedPath, ct),
            DocumentKind.Xlsx => ExtractXlsx(storedPath, ct),
            DocumentKind.Pptx => ExtractPptx(storedPath, ct),
            _ => throw new AttachmentRejectedException("Формат файла не поддерживается")
        }, ct);
    }

    // ---------- DOCX ----------

    private static ExtractedDocument ExtractDocx(string path, CancellationToken ct)
    {
        var segments = new List<DocSegment>();
        var warnings = new List<string>();
        var order = 0; var total = 0;
        string? currentHeading = null;
        var paragraphNo = 0; var tableNo = 0;

        try
        {
            using var doc = WordprocessingDocument.Open(path, isEditable: false);
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new AttachmentRejectedException("Не удалось прочитать документ");
            var headingStyles = CollectHeadingStyleIds(doc);

            foreach (var element in body.ChildElements)
            {
                ct.ThrowIfCancellationRequested();
                switch (element)
                {
                    case W.Paragraph paragraph:
                    {
                        paragraphNo++;
                        var text = TextSanitizer.Normalize(ParagraphText(paragraph));
                        if (text.Length == 0)
                            continue;
                        TextAnchor anchor;
                        if (IsHeading(paragraph, headingStyles))
                        {
                            currentHeading = Truncate(text, 80);
                            anchor = new TextAnchor("section", $"§ {currentHeading}");
                        }
                        else
                        {
                            anchor = currentHeading is not null
                                ? new TextAnchor("section", $"§ {currentHeading}")
                                : new TextAnchor("paragraph", $"абзац {paragraphNo}");
                        }
                        SegmentAccumulator.Add(segments, text, anchor, ref order, ref total);
                        break;
                    }
                    case W.Table table:
                    {
                        tableNo++;
                        var text = TextSanitizer.Normalize(TableText(table));
                        if (text.Length == 0)
                            continue;
                        SegmentAccumulator.Add(segments, text,
                            new TextAnchor("table", $"таблица {tableNo}"), ref order, ref total);
                        break;
                    }
                    case W.SdtBlock sdt:
                    {
                        // структурный контейнер (content control): текст внутри SdtContentBlock
                        var sdtText = TextSanitizer.Normalize(
                            sdt.GetFirstChild<W.SdtContentBlock>()?.InnerText);
                        if (sdtText?.Length > 0)
                        {
                            paragraphNo++;
                            SegmentAccumulator.Add(segments, sdtText,
                                new TextAnchor("paragraph", $"абзац {paragraphNo}"), ref order, ref total);
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (TranslatePackageError(ex) is { } reason)
        {
            throw new AttachmentRejectedException(reason);
        }

        if (segments.Count == 0)
            throw new AttachmentRejectedException("В документе нет читаемого текста");
        return new ExtractedDocument(segments, total, warnings);
    }

    /// <summary>Текст абзаца: пробелы табуляции и переносы <w:br/> сохраняем внутри абзаца.</summary>
    private static string ParagraphText(W.Paragraph paragraph)
    {
        var sb = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node is W.Text t) sb.Append(t.Text);
            else if (node is W.TabChar) sb.Append(' ');
            else if (node is W.Break) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string TableText(W.Table table)
    {
        var sb = new StringBuilder();
        foreach (var row in table.Elements<W.TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<W.TableCell>())
            {
                var cellText = new StringBuilder();
                foreach (var p in cell.Elements<W.Paragraph>())
                {
                    var t = TextSanitizer.Normalize(ParagraphText(p));
                    if (t.Length > 0)
                    {
                        if (cellText.Length > 0) cellText.Append(' ');
                        cellText.Append(t);
                    }
                }
                cells.Add(cellText.ToString());
            }
            sb.Append(string.Join(" | ", cells));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Заголовок: outline level 0–8 или стиль «Heading N»/«Заголовок N».
    /// StyleId часто локализован — проверяем и имя стиля из styles.xml.
    /// </summary>
    private static bool IsHeading(W.Paragraph paragraph, IReadOnlySet<string> headingStyleIds)
    {
        var props = paragraph.ParagraphProperties;
        if (props is null) return false;
        var outline = props.GetFirstChild<W.OutlineLevel>()?.Val?.Value;
        if (outline is >= 0 and <= 8) return true;
        var styleId = props.ParagraphStyleId?.Val?.Value;
        return styleId is not null && headingStyleIds.Contains(styleId);
    }

    private static HashSet<string> CollectHeadingStyleIds(WordprocessingDocument doc)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var styles = doc.MainDocumentPart?.StyleDefinitionsPart?.Styles;
        if (styles is null) return ids;
        foreach (var style in styles.Elements<W.Style>())
        {
            var id = style.StyleId?.Value;
            if (id is null) continue;
            var name = style.GetFirstChild<W.StyleName>()?.Val?.Value ?? string.Empty;
            var isHeading = name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("заголовок", StringComparison.OrdinalIgnoreCase)
                            || id.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                            || id.StartsWith("Заголовок", StringComparison.OrdinalIgnoreCase);
            if (isHeading)
                ids.Add(id);
        }
        return ids;
    }

    // ---------- XLSX ----------

    private static ExtractedDocument ExtractXlsx(string path, CancellationToken ct)
    {
        var segments = new List<DocSegment>();
        var warnings = new List<string>();
        var order = 0; var total = 0;
        var hiddenSheets = 0; var hiddenCells = 0; var noCacheFormulas = 0; var pivotCount = 0;
        var sawFormula = false;

        try
        {
            using var doc = SpreadsheetDocument.Open(path, isEditable: false);
            var wbPart = doc.WorkbookPart
                ?? throw new AttachmentRejectedException("Не удалось прочитать документ");
            var sharedStrings = LoadSharedStrings(wbPart);
            var styles = LoadStyles(wbPart);

            var sheetsEl = wbPart.Workbook.Sheets
                ?? throw new AttachmentRejectedException("В книге нет листов");
            foreach (var sheet in sheetsEl.Elements<S.Sheet>())
            {
                ct.ThrowIfCancellationRequested();
                if (sheet.State?.Value == S.SheetStateValues.Hidden
                    || sheet.State?.Value == S.SheetStateValues.VeryHidden)
                {
                    hiddenSheets++;
                    continue;
                }
                var wsPart = wbPart.GetPartById(sheet.Id?.Value ?? string.Empty) as WorksheetPart;
                var sheetData = wsPart?.Worksheet?.GetFirstChild<S.SheetData>();
                if (wsPart is null || sheetData is null)
                    continue;

                pivotCount += wsPart.PivotTableParts.Count();
                var hiddenCols = CollectHiddenColumns(wsPart.Worksheet);
                var sheetName = TextSanitizer.Normalize(sheet.Name?.Value ?? "Лист");

                var sb = new StringBuilder();
                string? firstRef = null; string? lastRef = null;

                void FlushSheetChunk()
                {
                    var text = sb.ToString().TrimEnd('\n');
                    sb.Clear();
                    if (text.Length == 0 || firstRef is null) { firstRef = null; return; }
                    var label = firstRef == lastRef
                        ? $"{sheetName}!{firstRef}"
                        : $"{sheetName}!{firstRef}:{lastRef}";
                    SegmentAccumulator.Add(segments, text,
                        new TextAnchor("sheet", label), ref order, ref total);
                    firstRef = null;
                }

                foreach (var row in sheetData.Elements<S.Row>())
                {
                    ct.ThrowIfCancellationRequested();
                    if (row.Hidden?.Value == true)
                    {
                        hiddenCells++;
                        continue;
                    }
                    var cells = new List<string>();
                    var rowFirst = true;
                    foreach (var cell in row.Elements<S.Cell>())
                    {
                        var colIdx = ColumnIndex(cell.CellReference?.Value);
                        if (colIdx is not null && hiddenCols.Contains(colIdx.Value))
                        {
                            hiddenCells++;
                            continue;
                        }
                        var value = CellText(cell, sharedStrings, styles, ref sawFormula, ref noCacheFormulas);
                        if (value is null) continue;
                        var cellRef = cell.CellReference?.Value
                            ?? (colIdx is not null && row.RowIndex?.Value is { } ri
                                ? $"{ColumnName(colIdx.Value)}{ri}" : null);
                        if (cellRef is not null)
                        {
                            if (rowFirst) { rowFirst = false; if (firstRef is null) firstRef = cellRef; }
                            lastRef = cellRef;
                            cells.Add($"{cellRef}: {value}");
                        }
                        else
                        {
                            cells.Add(value);
                        }
                    }
                    if (cells.Count == 0) continue;
                    var line = string.Join(" | ", cells);
                    if (sb.Length > 0 && sb.Length + line.Length + 1 > 3000)
                        FlushSheetChunk();
                    sb.Append(line); sb.Append('\n');
                }
                FlushSheetChunk();
            }
        }
        catch (Exception ex) when (TranslatePackageError(ex) is { } reason)
        {
            throw new AttachmentRejectedException(reason);
        }

        if (hiddenSheets > 0)
            warnings.Add($"Пропущены скрытые листы: {hiddenSheets}");
        if (hiddenCells > 0)
            warnings.Add("Пропущены скрытые строки и столбцы");
        if (pivotCount > 0)
            warnings.Add("Сводные таблицы показаны сохранёнными значениями");
        if (noCacheFormulas > 0)
            warnings.Add("Часть формул без сохранённого результата — показана формула");
        else if (sawFormula)
            warnings.Add("Формулы показаны сохранёнными значениями");

        if (segments.Count == 0)
            throw new AttachmentRejectedException("В книге нет видимых данных");
        return new ExtractedDocument(segments, total, warnings);
    }

    private static List<string> LoadSharedStrings(WorkbookPart wbPart) =>
        wbPart.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>()
            .Select(item => TextSanitizer.Normalize(item.InnerText))
            .ToList()
        ?? new List<string>();

    private static S.Stylesheet? LoadStyles(WorkbookPart wbPart) =>
        wbPart.WorkbookStylesPart?.Stylesheet;

    private static HashSet<int> CollectHiddenColumns(S.Worksheet worksheet)
    {
        var hidden = new HashSet<int>();
        foreach (var col in worksheet.Elements<S.Column>())
        {
            if (col.Hidden?.Value != true) continue;
            var min = (int)(col.Min?.Value ?? 0u);
            var max = (int)(col.Max?.Value ?? (uint)min);
            // разумный потолок — реальный предел Excel 16384; злой файл с мин.широким
            // диапазоном не должен раздувать множество
            for (var c = min; c <= max && c <= 16_384 && hidden.Count <= 16_384; c++)
                hidden.Add(c);
        }
        return hidden;
    }

    /// <summary>
    /// Значение ячейки: shared/inline строки, числа, булевы, даты (по стилю
    /// формата), сохранённые результаты формул. Формулы не вычисляем; без кэша —
    /// текст формулы с пометкой.
    /// </summary>
    private static string? CellText(S.Cell cell, IReadOnlyList<string> sharedStrings,
        S.Stylesheet? styles, ref bool sawFormula, ref int noCacheFormulas)
    {
        var formula = cell.CellFormula?.Text;
        var hasFormula = !string.IsNullOrEmpty(formula);
        if (hasFormula) sawFormula = true;

        string? value = null;
        var type = cell.DataType?.Value;
        if (type == S.CellValues.SharedString)
        {
            if (int.TryParse(cell.CellValue?.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var idx)
                && idx >= 0 && idx < sharedStrings.Count)
                value = sharedStrings[idx];
        }
        else if (type == S.CellValues.InlineString)
        {
            value = TextSanitizer.Normalize(cell.InlineString?.InnerText);
        }
        else if (type == S.CellValues.Boolean)
        {
            value = cell.CellValue?.Text == "1" ? "ИСТИНА" : "ЛОЖЬ";
        }
        else if (type == S.CellValues.Date)
        {
            if (DateTime.TryParse(cell.CellValue?.Text, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
                value = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else
                value = cell.CellValue?.Text;
        }
        else
        {
            var raw = cell.CellValue?.Text;
            if (raw is not null && LooksLikeDate(cell, styles)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
                && serial >= 1 && serial <= 2_958_465) // предел дат Excel
            {
                value = DateTime.FromOADate(serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else
            {
                value = raw;
            }
        }

        value = value is null ? null : TextSanitizer.Normalize(value);
        if (hasFormula)
        {
            if (value is null)
            {
                noCacheFormulas++;
                return $"={formula} (результат не сохранён)";
            }
            return value;
        }
        return value;
    }

    /// <summary>
    /// Ячейка с числом без типа — дата, если её стиль ссылается на встроенный
    /// формат даты (14–22, 45–47) или пользовательский формат с d/m/y-кодами.
    /// </summary>
    private static bool LooksLikeDate(S.Cell cell, S.Stylesheet? styles)
    {
        if (styles?.CellFormats is null) return false;
        var styleIndex = cell.StyleIndex?.Value;
        if (styleIndex is null) return false;
        var format = styles.CellFormats.Elements<S.CellFormat>()
            .ElementAtOrDefault((int)styleIndex.Value);
        var numId = format?.NumberFormatId?.Value;
        if (numId is null) return false;
        if (numId.Value is >= 14 and <= 22 or 45 or 46 or 47) return true;
        var code = styles.NumberingFormats?.Elements<S.NumberingFormat>()
            .FirstOrDefault(nf => nf.NumberFormatId?.Value == numId.Value)
            ?.FormatCode?.Value;
        if (code is null) return false;
        // ищем d/m/y-литералы формата вне квадратных скобок и кавычек
        var inBracket = false; var inQuote = false;
        foreach (var ch in code)
        {
            if (inQuote) { if (ch == '"') inQuote = false; continue; }
            if (ch == '"') { inQuote = true; continue; }
            if (ch == '[') { inBracket = true; continue; }
            if (ch == ']') { inBracket = false; continue; }
            if (!inBracket && (ch == 'd' || ch == 'm' || ch == 'y')) return true;
        }
        return false;
    }

    /// <summary>«A4» → 1, «BC12» → 55; без букв — null.</summary>
    private static int? ColumnIndex(string? cellRef)
    {
        if (cellRef is null) return null;
        var idx = 0; var seen = false;
        foreach (var ch in cellRef)
        {
            if (ch is < 'A' or > 'Z') break;
            idx = idx * 26 + (ch - 'A' + 1);
            seen = true;
        }
        return seen ? idx : null;
    }

    private static string ColumnName(int index)
    {
        var sb = new StringBuilder();
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return sb.ToString();
    }

    // ---------- PPTX ----------

    private static ExtractedDocument ExtractPptx(string path, CancellationToken ct)
    {
        var segments = new List<DocSegment>();
        var warnings = new List<string>();
        var order = 0; var total = 0;
        var hiddenSlides = 0; var hasNotes = false;

        try
        {
            using var doc = PresentationDocument.Open(path, isEditable: false);
            var presPart = doc.PresentationPart
                ?? throw new AttachmentRejectedException("Не удалось прочитать документ");
            var slideIds = presPart.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList();
            if (slideIds is null || slideIds.Count == 0)
                throw new AttachmentRejectedException("В презентации нет слайдов");

            var slideNo = 0;
            foreach (var slideId in slideIds)
            {
                ct.ThrowIfCancellationRequested();
                slideNo++;
                var slidePart = presPart.GetPartById(slideId.RelationshipId?.Value ?? string.Empty)
                    as SlidePart;
                if (slidePart?.Slide is null) continue;
                if (slidePart.Slide.Show?.Value == false)
                {
                    hiddenSlides++;
                    continue;
                }
                if (slidePart.NotesSlidePart is not null)
                    hasNotes = true;

                var sb = new StringBuilder();
                var title = SlideTitle(slidePart.Slide);
                if (title is not null)
                    sb.Append(title).Append('\n');
                SlideText(slidePart.Slide, sb);
                var text = TextSanitizer.Normalize(sb.ToString());
                if (text.Length == 0) continue;
                SegmentAccumulator.Add(segments, text,
                    new TextAnchor("slide", $"Слайд {slideNo}"), ref order, ref total);
            }
        }
        catch (Exception ex) when (TranslatePackageError(ex) is { } reason)
        {
            throw new AttachmentRejectedException(reason);
        }

        if (hiddenSlides > 0)
            warnings.Add($"Пропущены скрытые слайды: {hiddenSlides}");
        if (hasNotes)
            warnings.Add("Заметки докладчика не читаются");

        if (segments.Count == 0)
            throw new AttachmentRejectedException("В презентации нет читаемого текста");
        return new ExtractedDocument(segments, total, warnings);
    }

    /// <summary>Заголовок слайда — текст плейсхолдера Title/CenterTitle, если есть.</summary>
    private static string? SlideTitle(P.Slide slide)
    {
        var shapeTree = slide.CommonSlideData?.ShapeTree;
        if (shapeTree is null) return null;
        foreach (var shape in shapeTree.Descendants<P.Shape>())
        {
            var phType = shape.NonVisualShapeProperties?
                .ApplicationNonVisualDrawingProperties?
                .GetFirstChild<P.PlaceholderShape>()?.Type?.Value;
            if (phType == P.PlaceholderValues.Title
                || phType == P.PlaceholderValues.CenteredTitle)
            {
                var text = TextSanitizer.Normalize(shape.TextBody?.InnerText);
                if (text is { Length: > 0 }) return text;
            }
        }
        return null;
    }

    /// <summary>
    /// Текст слайда по дереву шейпов: абзацы текстовых полей, фигуры (включая
    /// группы) и таблицы (GraphicFrame → a:tbl), порядок — документный.
    /// </summary>
    private static void SlideText(P.Slide slide, StringBuilder sb)
    {
        var shapeTree = slide.CommonSlideData?.ShapeTree;
        if (shapeTree is null) return;

        foreach (var element in shapeTree.ChildElements)
            AppendShapeTreeElement(element, sb);
    }

    private static void AppendShapeTreeElement(OpenXmlElement element, StringBuilder sb)
    {
        switch (element)
        {
            case P.GroupShape group:
                foreach (var child in group.ChildElements)
                    AppendShapeTreeElement(child, sb);
                break;
            case P.GraphicFrame frame:
            {
                // в GraphicFrame живут таблицы и диаграммы; текст берём только из таблиц —
                // содержимое диаграмм не считаем прочитанным
                var table = frame.Descendants<A.Table>().FirstOrDefault();
                if (table is not null)
                    AppendDrawingTable(table, sb);
                break;
            }
            case P.Shape shape:
                AppendTextBody(shape.TextBody, sb);
                break;
            case P.ConnectionShape:
            {
                // у ConnectionShape нет свойства TextBody — текст берём из абзацев
                foreach (var paragraph in element.Descendants<A.Paragraph>())
                {
                    var t = TextSanitizer.Normalize(DrawingParagraphText(paragraph));
                    if (t.Length > 0)
                        sb.Append(t).Append('\n');
                }
                break;
            }
        }
    }

    private static void AppendTextBody(OpenXmlElement? textBody, StringBuilder sb)
    {
        if (textBody is null) return;
        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            var t = TextSanitizer.Normalize(DrawingParagraphText(paragraph));
            if (t.Length > 0)
                sb.Append(t).Append('\n');
        }
    }

    private static string DrawingParagraphText(A.Paragraph paragraph)
    {
        var sb = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node is A.Text t) sb.Append(t.Text);
            else if (node is A.Break) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void AppendDrawingTable(A.Table table, StringBuilder sb)
    {
        foreach (var row in table.Elements<A.TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<A.TableCell>())
            {
                var t = TextSanitizer.Normalize(cell.TextBody?.InnerText);
                cells.Add(t ?? string.Empty);
            }
            sb.Append(string.Join(" | ", cells)).Append('\n');
        }
    }

    // ---------- общее ----------

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    /// <summary>
    /// Ошибки пакета → русская причина отказа; null — не наша ошибка,
    /// не глотать (в т.ч. OperationCanceledException).
    /// </summary>
    private static string? TranslatePackageError(Exception ex) => ex switch
    {
        AttachmentRejectedException => throw ex,
        OpenXmlPackageException => "Не удалось прочитать документ",
        System.IO.InvalidDataException => "Не удалось прочитать документ",
        System.IO.FileFormatException => "Не удалось прочитать документ",
        _ => null
    };
}
