namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Служебные сегменты страниц: адресуемая отметка «страница пустая» вместо
/// выпадения страницы из охвата. Такие сегменты не несут пользовательского
/// текста и пропускаются в map-reduce/фактах, но участвуют в адресации.
/// </summary>
public static class AttachmentPageMarkers
{
    public const string EmptyKind = "page-empty";

    public static string EmptyText(int page) =>
        $"[Страница {page} — пустая: текста нет]";

    public static string EmptyLabel(int page) => $"Стр. {page}";
}
