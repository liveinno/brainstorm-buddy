namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Атомарная запись файла: tmp в том же каталоге → File.Move(overwrite).
/// Крэш между write и move оставляет старый файл целым; блуждающий *.tmp
/// зачищается при следующей записи. Опциональный бэкап прежней версии
/// (path+".bak") — для миграции чужих конфигов (claude_desktop_config.json).
/// </summary>
public static class AtomicFileWriter
{
    public static void WriteText(string path, string content, bool backup = false)
        => WriteBytes(path, System.Text.Encoding.UTF8.GetBytes(content), backup);

    public static void WriteBytes(string path, byte[] bytes, bool backup = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        try
        {
            if (backup && File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* tmp уже перемещён/занят — останется мусором */ }
            throw;
        }
    }
}
