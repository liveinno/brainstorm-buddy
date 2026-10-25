using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrainstormBuddy.Core.Backup;

/// <summary>
/// Отложенное восстановление профиля при следующем запуске приложения.
///
/// Зачем: RestoreAsync не может заменить профиль, пока приложение держит
/// открытыми stores/writers. UI планирует восстановление маркером
/// %LOCALAPPDATA%\BrainstormBuddy\pending-restore.json (НЕ внутри профиля —
/// иначе маркер уехал бы вместе со старым каталогом при swap). При старте,
/// ДО открытия хранилищ, App читает маркер, вызывает
/// PortableArchive.RestoreAsync(StoresClosed: true — ещё ничего не открыто)
/// и удаляет маркер. Ошибка применения оставляет маркер на месте, чтобы
/// показать пользователю причину и не зациклиться.
///
/// Пароль архива хранится DPAPI-блобом (CurrentUser): маркер живёт один
/// перезапуск и доступен только этой учётной записи. Plaintext-пароль на
/// диск не падает.
/// </summary>
public static class DeferredProfileRestore
{
    /// <summary>Содержимое маркера отложенного восстановления.</summary>
    public sealed record PendingRestore(
        [property: JsonPropertyName("archivePath")] string ArchivePath,
        [property: JsonPropertyName("destination")] string Destination,
        [property: JsonPropertyName("protectedPassword")] string ProtectedPassword,
        [property: JsonPropertyName("createdUtc")] DateTime CreatedUtc);

    /// <summary>Фиксированный путь маркера (вне каталога профиля).</summary>
    public static string MarkerPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrainstormBuddy", "pending-restore.json");

    /// <summary>
    /// Планирует восстановление при следующем запуске. Перезаписывает
    /// предыдущий маркер атомарно (temp + move). Пароль DPAPI-шифруется.
    /// </summary>
    public static void Schedule(string archivePath, string destinationProfile, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var path = MarkerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Отложенное восстановление требует DPAPI (Windows).");
        var marker = new PendingRestore(
            Path.GetFullPath(archivePath), Path.GetFullPath(destinationProfile),
            Convert.ToBase64String(ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(password), null,
                DataProtectionScope.CurrentUser)),
            DateTime.UtcNow);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(marker));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Читает ожидающий маркер и расшифровывает пароль. null — маркера нет,
    /// он битый или DPAPI не сработал (вызывающий решает: удалить или
    /// сообщить). Битый маркер здесь же снимается, чтобы не блокировать запуск.
    /// </summary>
    public static (string ArchivePath, string Destination, string Password)? TryTake()
    {
        var path = MarkerPath();
        if (!File.Exists(path)) return null;
        PendingRestore? m = null;
        try
        {
            m = JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(path));
        }
        catch { }
        if (m == null || string.IsNullOrWhiteSpace(m.ArchivePath) ||
            string.IsNullOrWhiteSpace(m.Destination) ||
            string.IsNullOrWhiteSpace(m.ProtectedPassword))
        {
            Clear();
            return null;
        }
        try
        {
            if (!OperatingSystem.IsWindows()) { Clear(); return null; }
            var password = System.Text.Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(m.ProtectedPassword),
                    null, DataProtectionScope.CurrentUser));
            return (m.ArchivePath, m.Destination, password);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Чужая учётная запись, повреждённый блоб или битый base64 —
            // маркер бесполезен: снимаем, чтобы не блокировать запуск.
            Clear();
            return null;
        }
    }

    /// <summary>Удаляет маркер (после успешного применения или отказа).</summary>
    public static void Clear()
    {
        try { File.Delete(MarkerPath()); } catch { }
    }

    /// <summary>Есть ли запланированное восстановление (без расшифровки).</summary>
    public static bool IsPending => File.Exists(MarkerPath());
}
