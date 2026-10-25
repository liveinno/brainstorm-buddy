using UglyToad.PdfPig;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Быстрая проверка уже сохранённых PDF: какие из запрошенных страниц
/// действительно пусты (нет текстового слоя и нет изображений). Нужна для
/// документов, извлечённых до появления адресуемых заглушек «page-empty»:
/// вопрос «что на стр. N» не должен получать «текст не извлечён», если на
/// странице нет ни текста, ни картинок.
/// </summary>
public static class PdfPageInspector
{
    /// <summary>
    /// Возвращает подмножество <paramref name="pages"/>, страницы которого
    /// пусты. Ошибки чтения PDF трактуются как «не пусто»: лучше честное
    /// «не извлечён», чем ложное «страница пустая».
    /// </summary>
    public static IReadOnlySet<int> FindBlankPages(
        string pdfPath, IEnumerable<int> pages, CancellationToken ct = default)
    {
        var wanted = pages.Where(p => p > 0).ToHashSet();
        var blank = new HashSet<int>();
        if (wanted.Count == 0 || !File.Exists(pdfPath)) return blank;

        try
        {
            using var document = PdfDocument.Open(pdfPath);
            foreach (var pageNumber in wanted.OrderBy(p => p))
            {
                ct.ThrowIfCancellationRequested();
                if (pageNumber > document.NumberOfPages) continue;
                try
                {
                    var page = document.GetPage(pageNumber);
                    // Есть любой текстовый слой — страница не пустая, даже
                    // если извлекатель её почему-то не сохранил.
                    if (!string.IsNullOrWhiteSpace(page.Text)) continue;
                    // Картинка без распознанного текста — не пустая: OCR мог
                    // не сработать, и «пустая» здесь была бы ложью.
                    if (page.GetImages().Any()) continue;
                    blank.Add(pageNumber);
                }
                catch
                {
                    // Повреждённая страница: не объявляем её пустой.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new HashSet<int>();
        }
        return blank;
    }
}
