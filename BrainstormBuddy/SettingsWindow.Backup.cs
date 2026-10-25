using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BrainstormBuddy.Core.Backup;
using BrainstormBuddy.Config;
using Microsoft.Win32;


// Часть SettingsWindow: вкладка «Резервная копия и перенос».
// Архив — PortableArchive (AES-256-GCM + PBKDF2, формат bbuddy-profile/1):
// манифест и данные шифруются, поэтому имена файлов и секреты профиля без
// пароля не утекают. Пароли живут только в PasswordBox и нигде не пишутся.
namespace BrainstormBuddy;

public partial class SettingsWindow
{
    // Одна backup-операция за раз: Create/Validate/Restore делят общий
    // прогресс-бар и кнопку отмены. _backupBusy гасит повторные клики.
    private CancellationTokenSource? _backupCts;
    private Progress<ArchiveProgress>? _backupProgress;
    private bool _backupBusy;

    // ------------------------------------------------------------------
    // Инфраструктура прогресса и статуса
    // ------------------------------------------------------------------

    /// <summary>Переводит вкладку в состояние «операция идёт». Возвращает false, если уже занята.</summary>
    private bool BeginBackupOp()
    {
        if (_backupBusy) return false;
        _backupBusy = true;
        _backupCts = new CancellationTokenSource();
        _backupProgress ??= new Progress<ArchiveProgress>(OnBackupProgress);
        BackupProgressRow.Visibility = Visibility.Visible;
        BackupProgressBar.IsIndeterminate = true;
        BackupProgressBar.Value = 0;
        BackupStageText.Text = Bak("L_Bak_Stage_working");
        BackupStatusText.Text = string.Empty;
        BackupCreateBtn.IsEnabled = false;
        BackupValidateBtn.IsEnabled = false;
        BackupRestoreBtn.IsEnabled = false;
        return true;
    }

    private void EndBackupOp()
    {
        _backupBusy = false;
        _backupCts?.Dispose();
        _backupCts = null;
        BackupProgressRow.Visibility = Visibility.Collapsed;
        BackupCreateBtn.IsEnabled = true;
        BackupValidateBtn.IsEnabled = true;
        BackupRestoreBtn.IsEnabled = true;
    }

    private void OnBackupProgress(ArchiveProgress p)
    {
        if (p.Operation == "decrypt" || p.TotalBytes <= 0)
        {
            // decrypt сообщает CompletedBytes == TotalBytes — показываем маятник.
            BackupProgressBar.IsIndeterminate = true;
        }
        else
        {
            BackupProgressBar.IsIndeterminate = false;
            BackupProgressBar.Value = Math.Clamp(
                (double)p.CompletedBytes / p.TotalBytes, 0, 1);
        }
        var stage = Bak("L_Bak_Stage_" + p.Operation);
        if (!string.IsNullOrEmpty(p.CurrentPath))
            stage += " · " + string.Format(Bak("L_Bak_CurrentFile"), p.CurrentPath);
        BackupStageText.Text = stage;
    }

    private void ShowBackupStatus(string text, bool ok)
    {
        BackupStatusText.Text = text;
        BackupStatusText.Foreground = (System.Windows.Media.Brush)(
            TryFindResource(ok ? "AccentBrush" : "ErrorBrush")
            ?? TryFindResource("TextMutedBrush")
            ?? System.Windows.Media.Brushes.Gray);
    }

    /// <summary>Строка ресурса с fallback на ключ: TryFindResource не бросает на промахе.</summary>
    private string Bak(string key)
        => TryFindResource(key) as string ?? key;

    private void OnBackupCancel(object sender, RoutedEventArgs e) => _backupCts?.Cancel();

    // ------------------------------------------------------------------
    // Создание архива
    // ------------------------------------------------------------------

    private async void OnCreateBackup(object sender, RoutedEventArgs e)
    {
        var password = BackupPasswordBox.Password;
        try
        {
            if (string.IsNullOrEmpty(password))
            {
                ShowBackupStatus(Bak("L_Bak_PasswordEmpty"), ok: false);
                return;
            }
            if (password != BackupPasswordConfirmBox.Password)
            {
                ShowBackupStatus(Bak("L_Bak_PasswordMismatch"), ok: false);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Bak("L_Bak_SaveDialogTitle"),
                Filter = Bak("L_Bak_Filter"),
                DefaultExt = ".bbpack",
                AddExtension = true,
                FileName = "BrainstormBuddy-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".bbpack",
            };
            if (dlg.ShowDialog(this) != true) return;
            var archivePath = dlg.FileName;

            if (!BeginBackupOp()) return;
            try
            {
                var includeModels = BackupExcludeModelsCheck.IsChecked != true;
                var options = new ArchiveInventoryOptions
                {
                    ProfileDirectory = App.Current.AppDataDir,
                    // Preview должен видеть тот же состав, что и CreateAsync:
                    // архив внутри профиля исключается из обеих инвентаризаций.
                    ArchivePath = archivePath,
                    IncludeModels = includeModels,
                    IncludeLogs = BackupIncludeLogsCheck.IsChecked == true,
                    ExternalPaths = BuildBackupExternalPaths(includeModels),
                };

                // Показываем состав ДО записи: инвентаризация идёт дважды
                // (preview, потом внутри CreateAsync) — хэширование файлов
                // дороже, но пользователь подтверждает то, что реально попадёт
                // в архив.
                var inventory = await Task.Run(() => PortableArchive.BuildInventoryAsync(
                    options, _backupProgress, _backupCts!.Token));
                var warnings = inventory.Warnings.Count > 0
                    ? string.Format(Bak("L_Bak_ConfirmWarningsFmt"),
                        string.Join("; ", inventory.Warnings))
                    : string.Empty;
                var confirm = string.Format(Bak("L_Bak_ConfirmCreateFmt"),
                    inventory.Entries.Count, FormatSize(inventory.TotalBytes),
                    inventory.ProfileDirectory, archivePath, warnings);
                if (AppMessageBox.Show(this, confirm, Bak("L_Bak_CreateTitle"),
                        MessageBoxButton.OKCancel, MessageBoxImage.Question)
                    != MessageBoxResult.OK)
                {
                    ShowBackupStatus(Bak("L_Bak_Cancelled"), ok: false);
                    return;
                }

                var result = await Task.Run(() => PortableArchive.CreateAsync(
                    options, archivePath, password,
                    progress: _backupProgress, ct: _backupCts!.Token));
                var notes = ComponentNotes(result.Components);
                ShowBackupStatus(string.Format(Bak("L_Bak_CreateDoneFmt"),
                    result.Inventory.Entries.Count,
                    FormatSize(result.Inventory.TotalBytes),
                    result.ArchivePath, notes), ok: true);
                App.Current.Logger?.Info(
                    $"Backup created: {result.Inventory.Entries.Count} files -> {result.ArchivePath}", "UI");
            }
            catch (OperationCanceledException)
            {
                ShowBackupStatus(Bak("L_Bak_Cancelled"), ok: false);
            }
            catch (PortableArchiveException ex)
            {
                ShowBackupStatus(ArchiveFailureText(ex.Kind, ex.Message), ok: false);
                App.Current.Logger?.Error("Backup create failed", ex, "UI");
            }
            catch (Exception ex)
            {
                ShowBackupStatus(ArchiveFailureText(ArchiveFailureKind.Io, ex.Message), ok: false);
                App.Current.Logger?.Error("Backup create failed", ex, "UI");
            }
            finally
            {
                EndBackupOp();
            }
        }
        finally
        {
            BackupPasswordBox.Clear();
            BackupPasswordConfirmBox.Clear();
        }
    }

    // ------------------------------------------------------------------
    // Проверка архива
    // ------------------------------------------------------------------

    private async void OnValidateBackup(object sender, RoutedEventArgs e)
    {
        var password = BackupRestorePasswordBox.Password;
        try
        {
            if (string.IsNullOrEmpty(password))
            {
                ShowBackupStatus(Bak("L_Bak_PasswordEmpty"), ok: false);
                return;
            }
            var archivePath = PickArchiveFile();
            if (archivePath == null) return;

            if (!BeginBackupOp()) return;
            try
            {
                var v = await Task.Run(() => PortableArchive.ValidateAsync(
                    archivePath, password, _backupProgress, _backupCts!.Token));
                ShowBackupStatus(BackupValidationSummary(v), v.IsValid);
            }
            catch (OperationCanceledException)
            {
                ShowBackupStatus(Bak("L_Bak_Cancelled"), ok: false);
            }
            catch (Exception ex)
            {
                ShowBackupStatus(ArchiveFailureText(ArchiveFailureKind.Io, ex.Message), ok: false);
                App.Current.Logger?.Error("Backup validate failed", ex, "UI");
            }
            finally
            {
                EndBackupOp();
            }
        }
        finally
        {
            BackupRestorePasswordBox.Clear();
        }
    }


    // ------------------------------------------------------------------
    // Восстановление
    // ------------------------------------------------------------------

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        var password = BackupRestorePasswordBox.Password;
        try
        {
            if (string.IsNullOrEmpty(password))
            {
                ShowBackupStatus(Bak("L_Bak_PasswordEmpty"), ok: false);
                return;
            }
            var archivePath = PickArchiveFile();
            if (archivePath == null) return;

            if (!BeginBackupOp()) return;
            try
            {
                // Сначала проверяем архив целиком — пользователь видит состав
                // и предупреждения до выбора папки назначения.
                var v = await Task.Run(() => PortableArchive.ValidateAsync(
                    archivePath, password, _backupProgress, _backupCts!.Token));
                var summary = BackupValidationSummary(v);
                ShowBackupStatus(summary, v.IsValid);
                if (!v.IsValid) return;

                var dest = PickRestoreDestination();
                if (dest == null)
                {
                    ShowBackupStatus(Bak("L_Bak_Cancelled"), ok: false);
                    return;
                }
                // Restore в ТЕКУЩИЙ профиль: заменить нельзя, пока приложение
                // держит stores — планируем отложенное восстановление при
                // следующем запуске (до открытия хранилищ).
                var destFull = Path.GetFullPath(dest)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var profileFull = Path.GetFullPath(App.Current.AppDataDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var deferred = string.Equals(destFull, profileFull,
                    StringComparison.OrdinalIgnoreCase);
                if (!deferred && !IsRestoreDestAllowed(dest))
                {
                    ShowBackupStatus(Bak("L_Bak_InvalidDest"), ok: false);
                    return;
                }
                var notes = ComponentNotes(v.Components);
                var confirm = string.Format(Bak("L_Bak_ConfirmRestoreFmt"),
                    v.Manifest!.Entries.Count, dest, notes);
                if (AppMessageBox.Show(this, confirm, Bak("L_Bak_RestoreTitle"),
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    != MessageBoxResult.OK)
                {
                    ShowBackupStatus(Bak("L_Bak_Cancelled"), ok: false);
                    return;
                }
                if (deferred)
                {
                    DeferredProfileRestore.Schedule(archivePath, dest, password);
                    ShowBackupStatus(Bak("L_Bak_Scheduled"), ok: true);
                    AppMessageBox.Show(this, Bak("L_Bak_RestartHint"),
                        Bak("L_Bak_RestoreTitle"), MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                // StoresClosed: swap в ЧУЖОЙ каталог не трогает открытые
                // файлы нашего профиля; IsRestoreDestAllowed отсекает любое
                // пересечение с AppDataDir.
                var r = await Task.Run(() => PortableArchive.RestoreAsync(
                    archivePath, password, dest,
                    new ArchiveRestoreOptions { StoresClosed = true },
                    _backupProgress, _backupCts!.Token));
                if (r.Restored)
                {
                    var backupNote = !string.IsNullOrEmpty(r.BackupDirectory)
                        ? Environment.NewLine
                          + string.Format(Bak("L_Bak_BackupKeptFmt"), r.BackupDirectory)
                        : string.Empty;
                    var warnings = r.Warnings.Count == 0 ? string.Empty
                        : Environment.NewLine + string.Join(Environment.NewLine, r.Warnings);
                    ShowBackupStatus(string.Format(Bak("L_Bak_RestoreDoneFmt"),
                        r.Manifest?.Entries.Count ?? 0, r.DestinationProfile,
                        backupNote, Bak("L_Bak_ExternalHint")) + warnings, ok: true);
                    App.Current.Logger?.Info(
                        $"Backup restored into {r.DestinationProfile}", "UI");
                }
                else
                {
                    ShowBackupStatus(ArchiveFailureText(r.Failure, r.Error), ok: false);
                    App.Current.Logger?.Warn(
                        $"Backup restore failed: {r.Failure}", "UI");
                }
            }
            catch (OperationCanceledException)
            {
                ShowBackupStatus(Bak("L_Bak_Cancelled"), ok: false);
            }
            catch (Exception ex)
            {
                ShowBackupStatus(ArchiveFailureText(ArchiveFailureKind.Io, ex.Message), ok: false);
                App.Current.Logger?.Error("Backup restore failed", ex, "UI");
            }
            finally
            {
                EndBackupOp();
            }
        }
        finally
        {
            BackupRestorePasswordBox.Clear();
        }
    }

    // ------------------------------------------------------------------
    // Диалоги и guard'ы
    // ------------------------------------------------------------------

    private string? PickArchiveFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Bak("L_Bak_RestoreTitle"),
            Filter = Bak("L_Bak_Filter"),
            CheckFileExists = true,
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private string? PickRestoreDestination()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Bak("L_Bak_PickDestTitle"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        // Стартуем в родителе профиля: удобнее всего выбрать соседнюю
        // пустую папку (внутрь самого профиля restore не пустит guard).
        var parent = Path.GetDirectoryName(
            Path.GetFullPath(App.Current.AppDataDir));
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            dlg.SelectedPath = parent;
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dlg.SelectedPath
            : null;
    }

    /// <summary>
    /// Restore идёт ТОЛЬКО в отдельный каталог, не пересекающийся с живым
    /// профилем: destination не может быть самим AppDataDir (swap при живых
    /// stores опасен), его предком (переименует каталог, внутри которого
    /// лежит профиль) или потомком (новые файлы внутри данных, которые
    /// используются прямо сейчас).
    /// </summary>
    private static bool IsRestoreDestAllowed(string dest)
    {
        var full = Path.GetFullPath(dest)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var profile = Path.GetFullPath(App.Current.AppDataDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, profile, StringComparison.OrdinalIgnoreCase))
            return false;
        if (profile.StartsWith(full + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (full.StartsWith(profile + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // ------------------------------------------------------------------
    // Внешние пути архива (живут вне %APPDATA%\BrainstormBuddy)
    // ------------------------------------------------------------------

    /// <summary>
    /// Собирает ExternalPaths для инвентаризации: сконфигурированные модели
    /// и каталог экспорта, расположенные вне профиля. Core remap'ит эти
    /// ссылки в конфиге/SQLite при restore. Всё, что уже внутри профиля,
    /// сканер покрывает сам — здесь только внешнее.
    /// </summary>
    private static List<ArchiveExternalPath> BuildBackupExternalPaths(bool includeModels)
    {
        var list = new List<ArchiveExternalPath>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = Path.GetFullPath(App.Current.AppDataDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cfg = App.Current.Config;

        // Каталог с моделью целиком: рядом с onnx лежат vocab/labels/frontend.
        if (includeModels)
        {
            AddModelDir(cfg.Audio.SttModelPath, "external/stt-model");
            AddModelDir(cfg.Audio.SttV3ModelPath, "external/stt-v3-model");
            AddModelDir(cfg.SpeakerDiarization.NemotronModelPath, "external/nemotron-model");

            // Локальная E5-модель памяти: Memory.EmbeddingsModel в режиме local может
            // хранить явный каталог вне профиля (см. EmbeddingFactory.FindLocalModelDir).
            // Без него после переноса остаётся путь старой машины и смысловой recall
            // молча деградирует до FTS — чекбокс обещает «STT и эмбеддинги».
            var embeddingModel = cfg.Memory?.EmbeddingsModel?.Trim() ?? "";
            if (!string.IsNullOrEmpty(embeddingModel) &&
                (Path.IsPathRooted(embeddingModel) ||
                 embeddingModel.Contains('\\') || embeddingModel.Contains('/')))
                AddModelDir(embeddingModel, "external/e5-model");

            // Модели рядом с exe (вариант C авто-поиска): в профиль не входят,
            // при переносе на другую машину без них STT не заведётся.
            var exeModels = Path.Combine(AppContext.BaseDirectory, "models");
            Add(exeModels, "external/models");
        }

        // Каталог сохранённых записей/экспортов, если он вне профиля.
        if (!string.IsNullOrWhiteSpace(cfg.Ui.SavePath))
            Add(cfg.Ui.SavePath, "external/save-path");

        // Исходящие MCP stdio-серверы: exe с абсолютным путём вне профиля —
        // без него сервер не стартует на новом устройстве.
        foreach (var srv in cfg.Mcp?.OutgoingServers ?? new List<McpOutgoingServer>())
        {
            if (!string.Equals(srv.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
                continue;
            var cmd = srv.Command?.Trim() ?? "";
            if (cmd.Length < 3 || !(char.IsLetter(cmd[0]) && cmd[1] == ':' &&
                (cmd[2] == '\\' || cmd[2] == '/')) && !cmd.StartsWith("\\\\"))
                continue;
            if (!File.Exists(cmd)) continue;
            Add(cmd, $"external/mcp-servers/{srv.Id}/{Path.GetFileName(cmd)}");
        }

        return list;

        void AddModelDir(string? configured, string category)
        {
            if (string.IsNullOrWhiteSpace(configured)) return;
            var path = configured!;
            // Путь может быть файлом .onnx — тогда берём его каталог.
            if (!Directory.Exists(path) && File.Exists(path))
                path = Path.GetDirectoryName(path)!;
            Add(path, category);
        }

        void Add(string sourcePath, string category)
        {
            string full;
            try { full = Path.GetFullPath(sourcePath); }
            catch { return; }
            if (full.StartsWith(profile + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, profile, StringComparison.OrdinalIgnoreCase))
                return; // внутри профиля — сканер и так заберёт
            if (!Directory.Exists(full) && !File.Exists(full))
                return; // отсутствующий внешний источник — ни к чему
            if (!seen.Add(full)) return;
            // Required=false: модели/экспорты опциональны, отсутствие не
            // должно ломать создание архива.
            list.Add(new ArchiveExternalPath(full, category, Required: false));
        }
    }

    // ------------------------------------------------------------------
    // Форматирование результатов
    // ------------------------------------------------------------------

    /// <summary>Человекочитаемая причина ArchiveFailureKind + деталь (без секретов).</summary>
    private string ArchiveFailureText(ArchiveFailureKind kind, string? detail)
    {
        var key = kind switch
        {
            ArchiveFailureKind.WrongPassword => "L_Bak_Fail_WrongPassword",
            ArchiveFailureKind.Corrupt => "L_Bak_Fail_Corrupt",
            ArchiveFailureKind.InvalidFormat => "L_Bak_Fail_InvalidFormat",
            ArchiveFailureKind.Traversal => "L_Bak_Fail_Traversal",
            ArchiveFailureKind.MissingRequiredSource => "L_Bak_Fail_MissingSource",
            ArchiveFailureKind.LifecycleNotClosed => "L_Bak_Fail_Lifecycle",
            _ => "L_Bak_Fail_Io",
        };
        var text = Bak(key);
        if (!string.IsNullOrWhiteSpace(detail)) text += " " + detail;
        return text;
    }

    /// <summary>Заметки по компонентам: только нетипичные состояния (Remapped/RequiresLogin/Missing).</summary>
    private string ComponentNotes(System.Collections.Generic.IReadOnlyList<ArchiveComponentStatus> components)
    {
        var sb = new StringBuilder();
        foreach (var c in components)
        {
            if (c.State == ArchiveComponentState.Included) continue;
            var stateText = c.State switch
            {
                ArchiveComponentState.RequiresLogin => Bak("L_Bak_Comp_RequiresLogin"),
                ArchiveComponentState.Remapped => Bak("L_Bak_Comp_Remapped"),
                ArchiveComponentState.Missing => Bak("L_Bak_Comp_Missing"),
                _ => Bak("L_Bak_Comp_Included"),
            };
            sb.AppendLine($"{c.Component}: {stateText} — {c.Detail}");
        }
        return sb.Length == 0 ? string.Empty : Environment.NewLine + sb.ToString().TrimEnd();
    }

    /// <summary>Краткий итог ValidateAsync: состав + дата + заметки компонентов.</summary>
    private string BackupValidationSummary(ArchiveValidationResult v)
    {
        if (!v.IsValid || v.Manifest == null)
            return ArchiveFailureText(v.Failure, v.Error);
        var created = v.Manifest.CreatedUtc == default
            ? DateTime.Now
            : v.Manifest.CreatedUtc.ToLocalTime();
        return string.Format(Bak("L_Bak_ValidFmt"),
            v.Manifest.Entries.Count,
            FormatSize(v.Manifest.Entries.Sum(e2 => Math.Max(0, e2.Length))),
            created,
            ComponentNotes(v.Components));
    }
}
