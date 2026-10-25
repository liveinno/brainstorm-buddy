using System.Collections.ObjectModel;

namespace BrainstormBuddy.Core.Backup;

/// <summary>Вариант файла в переносимом архиве.</summary>
public enum ArchiveEntryKind
{
    File,
    SqliteSnapshot,
}

/// <summary>Причина неуспешной проверки или восстановления.</summary>
public enum ArchiveFailureKind
{
    None,
    WrongPassword,
    Corrupt,
    InvalidFormat,
    Traversal,
    MissingRequiredSource,
    Io,
    LifecycleNotClosed,
}

/// <summary>Состояние компонента профиля после переноса.</summary>
public enum ArchiveComponentState
{
    Included,
    Remapped,
    RequiresLogin,
    Missing,
}

/// <summary>Явный внешний путь, который нужно добавить в инвентаризацию.</summary>
public sealed record ArchiveExternalPath(
    string SourcePath,
    string ArchivePath,
    bool Required = true);

/// <summary>Параметры инвентаризации профиля.</summary>
public sealed class ArchiveInventoryOptions
{
    /// <summary>Каталог профиля приложения. В него входят config, базы, встречи, вложения, MCP и модели.</summary>
    public required string ProfileDirectory { get; init; }

    /// <summary>Путь создаваемого архива. Если архив уже находится в профиле, он исключается.</summary>
    public string? ArchivePath { get; init; }

    /// <summary>Явные внешние файлы или каталоги. Ссылки на внешние данные не угадываются автоматически.</summary>
    public IReadOnlyList<ArchiveExternalPath> ExternalPaths { get; init; }
        = Array.Empty<ArchiveExternalPath>();

    /// <summary>
    /// Включать каталог profile\models (Whisper ggml, E5 ONNX, Nemotron).
    /// По умолчанию false — модели не едут в архиве: это самые тяжёлые файлы
    /// профиля (гигабайты), а на новом устройстве они качаются заново.
    /// Явное включение (UI: снять галочку «Исключить загруженные модели»)
    /// даёт полный перенос вместе с моделями.
    /// </summary>
    public bool IncludeModels { get; init; }

    /// <summary>Включать каталог profile\logs. По умолчанию false — журналы не переносятся.</summary>
    public bool IncludeLogs { get; init; }
}

/// <summary>Один объект инвентаризации.</summary>
public sealed record ArchiveInventoryEntry(
    string ArchivePath,
    string SourcePath,
    long Length,
    string Sha256,
    ArchiveEntryKind Kind,
    bool Required = true);

/// <summary>Снимок состава профиля перед созданием архива.</summary>
public sealed class ArchiveInventory
{
    public ArchiveInventory(
        string profileDirectory,
        IReadOnlyList<ArchiveInventoryEntry> entries,
        long totalBytes,
        IReadOnlyList<string>? warnings = null)
    {
        ProfileDirectory = profileDirectory;
        Entries = new ReadOnlyCollection<ArchiveInventoryEntry>(entries.ToList());
        TotalBytes = totalBytes;
        Warnings = new ReadOnlyCollection<string>((warnings ?? Array.Empty<string>()).ToList());
    }

    public string ProfileDirectory { get; }
    public IReadOnlyList<ArchiveInventoryEntry> Entries { get; }
    public long TotalBytes { get; }
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>Одна запись манифеста. Манифест шифруется вместе с данными.</summary>
public sealed class ArchiveManifestEntry
{
    public int Index { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public ArchiveEntryKind Kind { get; set; }
    public bool Required { get; set; } = true;
}

/// <summary>Корневой внешний каталог или файл, включённый в архив (источник → префикс).</summary>
public sealed class ArchiveManifestExternalSource
{
    public string ArchivePath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
}

/// <summary>Открытая часть проверенного манифеста.</summary>
public sealed class ArchiveManifest
{
    public string FormatVersion { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string ProfileDirectory { get; set; } = string.Empty;
    public List<ArchiveManifestEntry> Entries { get; set; } = new();
    /// <summary>
    /// Корни внешних каталогов-источников: точное соответствие «исходный
    /// каталог → префикс в архиве». Заполняется при создании; у архивов
    /// старой версии пуст — тогда Relocator оценивает предка по глубине.
    /// </summary>
    public List<ArchiveManifestExternalSource> ExternalSources { get; set; } = new();
    /// <summary>
    /// Постоянные заметки о компонентах, вычисленные при создании архива
    /// (напр. DPAPI-секреты → RequiresLogin). Переносятся вместе с архивом,
    /// чтобы validate/restore не догадывались по именам файлов.
    /// </summary>
    public List<ArchiveComponentStatus> ComponentNotes { get; set; } = new();
}

/// <summary>Ход длительной операции. CompletedBytes не превышает TotalBytes.</summary>
public sealed record ArchiveProgress(
    string Operation,
    string? CurrentPath,
    long CompletedBytes,
    long TotalBytes);

/// <summary>Статус компонента, в частности DPAPI-секретов MCP.</summary>
public sealed record ArchiveComponentStatus(
    string Component,
    ArchiveComponentState State,
    string Detail);

/// <summary>Отчёт о переназначении пути. Новый путь всегда относителен к каталогу восстановления.</summary>
public sealed record ArchivePathRemap(
    string ArchivePath,
    string EffectivePath,
    string OriginalPath);

/// <summary>Позволяет дополнительно переназначить путь внутри выбранного каталога восстановления.</summary>
public interface IArchivePathRemapper
{
    /// <returns>Относительный путь внутри каталога восстановления или null для исходного archivePath.</returns>
    string? Remap(string archivePath, string originalPath);
}

/// <summary>Параметры восстановления. Caller обязан закрыть stores и writers до swap.</summary>
public sealed class ArchiveRestoreOptions
{
    /// <summary>
    /// Обязательное подтверждение вызывающей стороны, что SQLite stores, журналы и writers закрыты.
    /// Архив сначала распаковывается во временный каталог, но финальный swap без этого флага запрещён.
    /// </summary>
    public bool StoresClosed { get; init; }

    public IArchivePathRemapper? PathRemapper { get; init; }

    /// <summary>Перешифровать перенесённые секреты DPAPI текущей учётной записи. По умолчанию true.</summary>
    public bool RebindSecrets { get; init; } = true;

    /// <summary>Оставить резервную копию заменённого каталога. По умолчанию true.</summary>
    public bool KeepBackup { get; init; } = true;
}

/// <summary>Результат создания архива.</summary>
public sealed class ArchiveCreateResult
{
    public ArchiveCreateResult(
        string archivePath,
        ArchiveInventory inventory,
        IReadOnlyList<ArchiveComponentStatus> components)
    {
        ArchivePath = archivePath;
        Inventory = inventory;
        Components = new ReadOnlyCollection<ArchiveComponentStatus>(components.ToList());
    }

    public string ArchivePath { get; }
    public ArchiveInventory Inventory { get; }
    public IReadOnlyList<ArchiveComponentStatus> Components { get; }
}

/// <summary>Результат проверки архива.</summary>
public sealed class ArchiveValidationResult
{
    public ArchiveValidationResult(
        bool isValid,
        ArchiveFailureKind failure,
        string? error,
        ArchiveManifest? manifest,
        IReadOnlyList<ArchiveComponentStatus>? components = null)
    {
        IsValid = isValid;
        Failure = failure;
        Error = error;
        Manifest = manifest;
        Components = new ReadOnlyCollection<ArchiveComponentStatus>(
            (components ?? Array.Empty<ArchiveComponentStatus>()).ToList());
    }

    public bool IsValid { get; }
    public ArchiveFailureKind Failure { get; }
    public string? Error { get; }
    public ArchiveManifest? Manifest { get; }
    public IReadOnlyList<ArchiveComponentStatus> Components { get; }
}

/// <summary>Результат атомарного восстановления.</summary>
public sealed class ArchiveRestoreResult
{
    public ArchiveRestoreResult(
        bool restored,
        ArchiveFailureKind failure,
        string? error,
        string destinationProfile,
        string? backupDirectory,
        ArchiveManifest? manifest,
        IReadOnlyList<ArchiveComponentStatus>? components = null,
        IReadOnlyList<ArchivePathRemap>? remaps = null,
        IReadOnlyList<string>? warnings = null)
    {
        Restored = restored;
        Failure = failure;
        Error = error;
        DestinationProfile = destinationProfile;
        BackupDirectory = backupDirectory;
        Manifest = manifest;
        Components = new ReadOnlyCollection<ArchiveComponentStatus>(
            (components ?? Array.Empty<ArchiveComponentStatus>()).ToList());
        Remaps = new ReadOnlyCollection<ArchivePathRemap>(
            (remaps ?? Array.Empty<ArchivePathRemap>()).ToList());
        Warnings = new ReadOnlyCollection<string>(
            (warnings ?? Array.Empty<string>()).ToList());
    }

    public bool Restored { get; }
    public ArchiveFailureKind Failure { get; }
    public string? Error { get; }
    public string DestinationProfile { get; }
    public string? BackupDirectory { get; }
    public ArchiveManifest? Manifest { get; }
    public IReadOnlyList<ArchiveComponentStatus> Components { get; }
    public IReadOnlyList<ArchivePathRemap> Remaps { get; }
    /// <summary>Нефатальные замечания восстановления (напр. не удалось переписать путь в базе).</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>Ошибка создания или инвентаризации. Секреты в сообщении не передаются.</summary>
public sealed class PortableArchiveException : Exception
{
    public PortableArchiveException(string message) : base(message) { }
    public PortableArchiveException(string message, Exception inner) : base(message, inner) { }
    public PortableArchiveException(string message, ArchiveFailureKind kind)
        : base(message) => Kind = kind;
    public PortableArchiveException(string message, ArchiveFailureKind kind, Exception inner)
        : base(message, inner) => Kind = kind;

    /// <summary>Типизированная причина (по умолчанию Io).</summary>
    public ArchiveFailureKind Kind { get; } = ArchiveFailureKind.Io;
}
