using System.IO.Compression;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Определение вида документа по расширению и сигнатуре (не доверяем имени файла):
/// снимаем первые байты, для ZIP-контейнеров читаем оглавление (макросы
/// <c>vbaProject.bin</c>, реальный тип по частям OOXML, суммарный распакованный размер),
/// для OLE2-файлов с современным расширением ищем потоки «EncryptionInfo»/
/// «EncryptedPackage» — это зашифрованный OOXML, а не legacy .doc/.xls/.ppt.
/// Любая проблема — <see cref="AttachmentRejectedException"/> с короткой русской
/// причиной для статуса файла.
/// </summary>
public static class Classifier
{
    private const int SignatureProbeBytes = 8;

    /// <summary>
    /// Классифицирует файл. Бросает <see cref="AttachmentRejectedException"/>,
    /// если формат не поддерживается, файл пустой, слишком большой или повреждён.
    /// </summary>
    public static DocumentKind Classify(string filePath)
    {
        FileInfo info;
        try { info = new FileInfo(filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AttachmentRejectedException("Файл недоступен");
        }

        if (!info.Exists)
            throw new AttachmentRejectedException("Файл недоступен");
        if (info.Length == 0)
            throw new AttachmentRejectedException("Файл пустой");
        if (info.Length > AttachmentLimits.MaxFileBytes)
            throw new AttachmentRejectedException("Файл больше 25 МБ — разделите его");

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // Старые и макросные Office-форматы — по расширению, до чтения байтов.
        if (ext is ".doc" or ".xls" or ".ppt" or ".docm" or ".xlsm" or ".pptm")
            throw new AttachmentRejectedException(
                "Пересохраните документ в DOCX, XLSX, PPTX или PDF");

        var head = ReadPrefix(filePath, SignatureProbeBytes);
        if (head is null)
            throw new AttachmentRejectedException("Не удалось прочитать файл");

        return ext switch
        {
            ".pdf" => StartsWith(head, "%PDF-")
                ? DocumentKind.Pdf
                : throw new AttachmentRejectedException("Файл не является PDF"),
            ".docx" or ".xlsx" or ".pptx" => ClassifyOffice(filePath, ext, head),
            ".txt" => DocumentKind.Txt,
            ".md" or ".markdown" => DocumentKind.Markdown,
            _ => throw new AttachmentRejectedException(
                "Формат файла не поддерживается (PDF, DOCX, XLSX, PPTX, TXT, MD)")
        };
    }

    /// <summary>
    /// Современные офисные форматы — ZIP-контейнеры. Проверяем сигнатуру,
    /// оглавление (макросы, реальный тип частей) и суммарный размер записей.
    /// OLE2-сигнатура при современном расширении — либо зашифрованный OOXML,
    /// либо переименованный legacy-бинарник: различаем по именам потоков.
    /// </summary>
    private static DocumentKind ClassifyOffice(string filePath, string ext, byte[] head)
    {
        if (StartsWith(head, "\xD0\xCF\x11\xE0\xA1\xB1\x1A\xE1"u8))
        {
            return LooksLikeEncryptedOoxml(filePath)
                ? throw new AttachmentRejectedException(
                    "Документ защищён паролем — сохраните незашифрованную копию")
                : throw new AttachmentRejectedException(
                    "Пересохраните документ в DOCX, XLSX, PPTX или PDF");
        }
        if (!StartsWith(head, "PK\x03\x04"u8) && !StartsWith(head, "PK\x05\x06"u8))
            throw new AttachmentRejectedException(
                "Содержимое файла не совпадает с расширением");

        DocumentKind kind;
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            long totalSize = 0;
            var hasWordDoc = false; var hasWorkbook = false; var hasPresentation = false;
            var hasOther = false;
            foreach (var entry in zip.Entries)
            {
                if (entry.Length < 0)
                    continue;
                totalSize += entry.Length;
                if (totalSize > AttachmentLimits.MaxOfficeUncompressedBytes)
                    throw new AttachmentRejectedException(
                        "Содержимое документа слишком объёмное (более 200 МБ после распаковки)");

                var name = entry.FullName.Replace('\\', '/');

                // Agile/standard OOXML-шифрование в ZIP-обёртке (не OLE2):
                // вместо word/document.xml лежат EncryptionInfo + EncryptedPackage.
                if (name.Equals("EncryptionInfo", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("EncryptedPackage", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AttachmentRejectedException(
                        "Документ защищён паролем — сохраните незашифрованную копию");
                }
                if (name.Equals("word/vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("xl/vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ppt/vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AttachmentRejectedException(
                        "Файл содержит макросы — сохраните копию без макросов");
                }

                if (name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase))
                    hasWordDoc = true;
                else if (name.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
                    hasWorkbook = true;
                else if (name.Equals("ppt/presentation.xml", StringComparison.OrdinalIgnoreCase))
                    hasPresentation = true;
                else if (name.StartsWith("word/", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase))
                    hasOther = true;
            }

            if (hasWordDoc) kind = DocumentKind.Docx;
            else if (hasWorkbook) kind = DocumentKind.Xlsx;
            else if (hasPresentation) kind = DocumentKind.Pptx;
            else if (!hasOther)
                throw new AttachmentRejectedException(
                    "Содержимое файла не совпадает с расширением");
            else
                // есть служебные части OOXML, но нет главной — битый или нестандартный пакет
                throw new AttachmentRejectedException("Не удалось прочитать документ");
        }
        catch (InvalidDataException)
        {
            throw new AttachmentRejectedException("Не удалось прочитать документ");
        }
        catch (IOException)
        {
            throw new AttachmentRejectedException("Не удалось прочитать файл");
        }

        var expected = ext switch
        {
            ".docx" => DocumentKind.Docx,
            ".xlsx" => DocumentKind.Xlsx,
            ".pptx" => DocumentKind.Pptx,
            _ => kind
        };
        if (kind != expected)
            throw new AttachmentRejectedException(
                $"Содержимое файла — не {ext.TrimStart('.').ToUpperInvariant()}");

        return kind;
    }

    /// <summary>
    /// OLE2-файл с современным расширением: зашифрованный OOXML содержит потоки
    /// «EncryptionInfo»/«EncryptedPackage» (имена в каталоге — UTF-16LE). Ищем
    /// их сигнатуры в байтах; файловая система OLE2 не разбираем — это хрупко.
    /// </summary>
    private static bool LooksLikeEncryptedOoxml(string filePath)
    {
        var info = new FileInfo(filePath);
        var scanBytes = Math.Min(info.Length, 16L * 1024 * 1024); // каталог OLE2 обычно близко к началу
        var markers = new[] { "EncryptionInfo", "EncryptedPackage" };
        byte[] buffer;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            buffer = new byte[scanBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            Array.Resize(ref buffer, read);
        }
        catch (IOException) { return false; }

        foreach (var marker in markers)
        {
            var needle = System.Text.Encoding.Unicode.GetBytes(marker);
            if (IndexOf(buffer, needle) >= 0)
                return true;
        }
        return false;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static bool StartsWith(byte[] head, ReadOnlySpan<byte> prefix) =>
        head.Length >= prefix.Length && head.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool StartsWith(byte[] head, string asciiPrefix)
    {
        if (head.Length < asciiPrefix.Length) return false;
        for (var i = 0; i < asciiPrefix.Length; i++)
            if (head[i] != (byte)asciiPrefix[i]) return false;
        return true;
    }

    private static byte[]? ReadPrefix(string filePath, int count)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[count];
            var read = fs.Read(buffer, 0, count);
            Array.Resize(ref buffer, read);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
