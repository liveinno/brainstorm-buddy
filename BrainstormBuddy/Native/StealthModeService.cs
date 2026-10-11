using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Native;

/// <summary>
/// Стелс-режим процесса (Ui.StealthProcessName): при заданном alias первый запуск
/// делается не из оригинального exe, а из копии «&lt;alias&gt;.exe» в
/// %APPDATA%\BrainstormBuddy\stealth — тогда в Диспетчере задач колонка «Имя процесса»
/// показывает alias, а не BrainstormBuddy.
///
/// Механика (вызывается в OnStartup ДО создания single-instance mutex — иначе
/// relaunch-цикл: копия стартует, пока оригинал ещё держит mutex):
///   1) лёгкое чтение config.json (только Ui.StealthProcessName — полный ConfigLoader
///      требует логгер, который ещё не создан);
///   2) если имя текущего exe уже совпадает с alias — это и есть стелс-копия: ничего
///      не делаем (guard #1, сравнение пути/имени);
///   3) иначе зеркалируем каталог установки в stealth\, кладём туда пропатченную копию
///      exe и запускаем её с теми же аргументами; оригинал завершается.
///
/// Почему каталог, а не одиночный exe: publish — self-contained НЕ single-file
/// (BrainstormBuddy.dll + deps.json + runtimes\ обязаны лежать рядом с exe).
/// Файлы линкуются hardlink'ом когда stealth и установка на одном томе (это быстро и
/// бесплатно по месту), иначе копируются.
///
/// ЛИМИТ (см. задачу): патч StringFileInfo/FileDescription через UpdateResource меняет
/// колонку «Описание» и подсказки, но НЕ «Имя процесса» — Task Manager берёт его из
/// имени файла образа. Решает именно запуск &lt;alias&gt;.exe; ресурсы патчим для
/// консистентности (Properties/«Описание»), не ради колонки.
/// </summary>
internal static class StealthModeService
{
    /// <summary>Guard-переменная окружения: выставляется копии при relaunch (guard #2).</summary>
    public const string GuardEnvVar = "BUDDY_STEALTH";

    private const string AppFolderName = "BrainstormBuddy";
    private const string StealthDirName = "stealth";
    private const string AliasMarkerExt = ".buddyalias"; // маркер <alias>.buddyalias рядом с exe
    private const string DebugLogFile = "stealth.log";    // лог до инициализации Logger

    /// <summary>Alias текущего процесса (null = запущен оригинальный exe / стелс выкл).</summary>
    public static string? ActiveAlias { get; private set; }

    /// <summary>Имя для окон/трея: alias если стелс-режим активен, иначе «BrainstormBuddy».</summary>
    public static string DisplayName => ActiveAlias ?? "BrainstormBuddy";

    /// <summary>
    /// Точка входа из App.OnStartup. Возвращает true, если процесс уже перезапущен
    /// под alias и текущий экземпляр обязан немедленно завершиться.
    /// Никогда не бросает — при любой ошибке возвращаем false (обычный запуск).
    /// </summary>
    public static bool TryRelaunchAsAlias(IReadOnlyList<string> args)
    {
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
            var stealthDir = Path.Combine(appData, StealthDirName);
            var configPath = Path.Combine(appData, "config.json");

            var alias = ReadAliasFromConfig(configPath);
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return false;

            var stealthLaunched =
                string.Equals(Environment.GetEnvironmentVariable(GuardEnvVar), "1", StringComparison.Ordinal);
            var exeStem = Path.GetFileNameWithoutExtension(exePath);

            if (alias.Length == 0)
            {
                // Стелс выключен. Если при этом мы — ранее запущенная копия (или юзер
                // переименовал оригинал под alias), титулы показываем фактическим именем,
                // но relaunch обратно НЕ делаем: продукт должен просто работать.
                if (stealthLaunched && !string.Equals(exeStem, AppFolderName, StringComparison.OrdinalIgnoreCase))
                    ActiveAlias = exeStem;
                else
                    TryDeleteStealthDir(stealthDir); // старые копии при выключении режима
                return false;
            }

            // Уже стелс-копия: имя образа совпадает с alias (сравнение пути/имени —
            // основной guard). Погашение переменной-наследника тоже стопорит цикл.
            if (stealthLaunched || string.Equals(exeStem, alias, StringComparison.OrdinalIgnoreCase))
            {
                ActiveAlias = alias;
                if (Path.GetDirectoryName(exePath)?.Equals(stealthDir, StringComparison.OrdinalIgnoreCase) == true)
                    CleanupStaleAliases(stealthDir, alias); // смена alias: гасим предыдущие exe
                return false;
            }

            return PrepareAndLaunch(exePath, stealthDir, alias, args);
        }
        catch (Exception ex)
        {
            // Стелс-режим — бонус, не боевая нагрузка: любой сбой = обычный запуск.
            LogStealth(ex.Message);
            return false;
        }
    }

    // ===== подготовка копии и relaunch =====

    private static bool PrepareAndLaunch(string exePath, string stealthDir, string alias,
        IReadOnlyList<string> args)
    {
        var sourceDir = Path.GetDirectoryName(exePath)!;
        var targetExe = Path.Combine(stealthDir, alias + ".exe");
        // Маркер вида <alias>.buddyalias — отличает наши копии от зеркалированных
        // помощников (createdump.exe и т.п.) при чистке сменённых alias.
        var aliasMarker = Path.Combine(stealthDir, alias + AliasMarkerExt);

        if (string.Equals(sourceDir, stealthDir, StringComparison.OrdinalIgnoreCase))
            return false; // уже в stealth, но имя ≠ alias (переименованный?) — не уходим в цикл

        // Часть файлов может быть залочена ЖИВОЙ стелс-копией (rollback alias на работающий
        // экземпляр): существующий <alias>.exe — та самая версия, перезапускаем его,
        // не считая подготовку ошибкой (имя файла уже совпадает с требуемым alias).
        try
        {
            Directory.CreateDirectory(stealthDir);
            MirrorDirectory(sourceDir, stealthDir, exePath);
            File.Copy(exePath, targetExe, overwrite: true);
            PatchVersionStrings(targetExe, alias);
            File.WriteAllText(aliasMarker, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            LogStealth($"prepare failed: {ex}");
            if (!File.Exists(targetExe))
                return false;
        }

        var psi = new ProcessStartInfo(targetExe)
        {
            WorkingDirectory = stealthDir,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[GuardEnvVar] = "1";

        Process.Start(psi);
        LogStealth($"relaunched as '{alias}' pid-image {targetExe}");
        return true;
    }

    /// <summary>
    /// Инкрементальное зеркало каталога установки в stealth\: подменяются только
    /// изменившиеся файлы (hardlink при общем томе — мгновенно и бесплатно по месту,
    /// иначе копия). Wipe+rebuild не используем: при живой стелс-копии удаление
    /// залоченных файлов оставило бы половинчатый каталог и сломанный relaunch.
    /// Сбой одного файла — не фатален: лог и дальше (апдейт поверх живой копии — редкость).
    /// </summary>
    private static void MirrorDirectory(string sourceDir, string targetDir, string skipExePath)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            // Оригинальный exe не зеркалим: вместо него кладётся пропатченный <alias>.exe.
            if (string.Equals(file, skipExePath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var dest = Path.Combine(targetDir, Path.GetFileName(file));
                if (!SameFile(file, dest))
                    HardlinkOrCopy(file, dest);
            }
            catch (Exception ex) { LogStealth($"mirror '{file}' failed: {ex.Message}"); }
        }
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            try { MirrorDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)), skipExePath); }
            catch (Exception ex) { LogStealth($"mirror dir '{dir}' failed: {ex.Message}"); }
        }
    }

    /// <summary>Файл назначения идентичен источнику (копия/hardlink сохраняют метки времени).</summary>
    private static bool SameFile(string source, string dest)
    {
        var s = new FileInfo(source);
        var d = new FileInfo(dest);
        return d.Exists && s.Length == d.Length && s.LastWriteTimeUtc == d.LastWriteTimeUtc;
    }

    /// <summary>На одном томе — CreateHardLink (мгновенно, 0 байт), иначе полная копия.</summary>
    private static void HardlinkOrCopy(string source, string dest)
    {
        if (File.Exists(dest))
        {
            File.SetAttributes(dest, FileAttributes.Normal);
            File.Delete(dest);
        }
        try
        {
            if (!Win32.CreateHardLinkW(dest, source, IntPtr.Zero))
                File.Copy(source, dest);
        }
        catch (IOException) { File.Copy(source, dest); }        // cross-volume / FAT → копия
        catch (UnauthorizedAccessException) { File.Copy(source, dest); }
    }

    /// <summary>
    /// Смена alias: удаляем exe старых копий. Наши копии отличаем от зеркалированных
    /// помощников (createdump.exe и т.п.) по маркеру &lt;stem&gt;.buddyalias.
    /// </summary>
    private static void CleanupStaleAliases(string stealthDir, string currentAlias)
    {
        try
        {
            foreach (var marker in Directory.EnumerateFiles(stealthDir, "*" + AliasMarkerExt))
            {
                var stem = Path.GetFileNameWithoutExtension(marker);
                if (string.Equals(stem, currentAlias, StringComparison.OrdinalIgnoreCase))
                    continue;
                TryDelete(marker);
                TryDelete(Path.Combine(stealthDir, stem + ".exe"));
            }
        }
        catch { /* лочка живой копии — уберётся при следующем старте оригинала */ }
    }

    private static void TryDeleteStealthDir(string stealthDir)
    {
        try
        {
            if (!Directory.Exists(stealthDir))
                return;
            // Каталог может быть залочен пока живёт старая копия — тихий отказ,
            // следующий старт добьёт. Два быстрых ретрая на случай «почти вышла».
            for (var i = 0; i < 2; i++)
            {
                try { Directory.Delete(stealthDir, recursive: true); return; }
                catch { Thread.Sleep(300); }
            }
        }
        catch { /* best-effort */ }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { /* залочено живой копией/AV — не наша проблема */ }
    }

    // ===== чтение alias из config.json без полного ConfigLoader =====

    /// <summary>
    /// Лёгкий разбор config.json до появления логгера: нужен только Ui.StealthProcessName.
    /// Битый JSON / отсутствие файла → пустой alias (обычный запуск — максимально безопасно).
    /// </summary>
    private static string ReadAliasFromConfig(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
                return string.Empty;
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("Ui", out var ui) &&
                ui.ValueKind == JsonValueKind.Object &&
                ui.TryGetProperty("StealthProcessName", out var el) &&
                el.ValueKind == JsonValueKind.String &&
                StealthAlias.TryNormalize(el.GetString(), out var alias, out _))
            {
                return alias;
            }
        }
        catch (Exception ex) { LogStealth($"config read failed: {ex.Message}"); }
        return string.Empty;
    }

    // ===== патч VERSIONINFO =====

    /// <summary>
    /// Заменяет строки StringFileInfo в ресурсе RT_VERSION копии exe:
    /// FileDescription/ProductName/InternalName → alias, OriginalFilename → «alias.exe».
    /// Сохраняет структуру дерева: парсим VERSIONINFO целиком и переписываем значения
    /// на месте, пересобирая бинарь (wLength/wValueLength/padding).
    /// ЛИМИТ: колонка «Имя процесса» в Task Manager — имя файла образа, НЕ этот ресурс;
    /// патч нужен для «Описания» и консистентности Properties (см. заголовок класса).
    /// </summary>
    private static void PatchVersionStrings(string exePath, string alias)
    {
        try
        {
            var languages = EnumResourceLanguages(exePath);
            if (languages.Count == 0)
                languages.Add(0); // обычный .NET-apphost: нейтральный язык

            var hUpdate = Win32.BeginUpdateResourceW(exePath, bDeleteExistingResources: false);
            if (hUpdate == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BeginUpdateResource failed");

            var ok = false;
            foreach (var lang in languages)
            {
                if (!TryReadVersionResource(exePath, lang, out var blob))
                    continue;
                if (TryPatchVersionBlob(blob, alias, out var patched) &&
                    Win32.UpdateResourceW(hUpdate, (IntPtr)Win32.RT_VERSION, (IntPtr)1, lang,
                        patched, patched.Length))
                {
                    ok = true;
                }
            }

            if (!Win32.EndUpdateResourceW(hUpdate, fDiscard: !ok))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EndUpdateResource failed");
            LogStealth(ok
                ? $"VERSIONINFO patched: FileDescription/ProductName='{alias}'"
                : "VERSIONINFO patch skipped (no matching resource found)");
        }
        catch (Exception ex)
        {
            // Патч опционален — имя процесса решает запуск <alias>.exe.
            LogStealth($"version patch failed: {ex.Message}");
        }
    }

    /// <summary>Языки ресурса VS_VERSION_INFO/1 в exe (обычно один, нейтральный).</summary>
    private static List<ushort> EnumResourceLanguages(string exePath)
    {
        var langs = new List<ushort>();
        var hModule = Win32.LoadLibraryExW(exePath, IntPtr.Zero,
            Win32.LOAD_LIBRARY_AS_DATAFILE | Win32.LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (hModule == IntPtr.Zero)
            hModule = Win32.LoadLibraryExW(exePath, IntPtr.Zero, Win32.LOAD_LIBRARY_AS_DATAFILE);
        if (hModule == IntPtr.Zero)
            return langs;
        try
        {
            Win32.EnumResourceLanguagesW(hModule, (IntPtr)Win32.RT_VERSION, (IntPtr)1,
                (_, _, _, lang, _) =>
                {
                    langs.Add(lang);
                    return true;
                }, IntPtr.Zero);
        }
        finally { Win32.FreeLibrary(hModule); }
        return langs;
    }

    private static bool TryReadVersionResource(string exePath, ushort lang, out byte[] blob)
    {
        blob = Array.Empty<byte>();
        var hModule = Win32.LoadLibraryExW(exePath, IntPtr.Zero,
            Win32.LOAD_LIBRARY_AS_DATAFILE | Win32.LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (hModule == IntPtr.Zero)
            return false;
        try
        {
            var hRes = Win32.FindResourceExW(hModule, (IntPtr)Win32.RT_VERSION, (IntPtr)1, lang);
            if (hRes == IntPtr.Zero) return false;
            var size = Win32.SizeofResource(hModule, hRes);
            var ptr = Win32.LockResource(Win32.LoadResource(hModule, hRes));
            if (ptr == IntPtr.Zero || size == 0) return false;
            blob = new byte[size];
            Marshal.Copy(ptr, blob, 0, checked((int)size));
            return true;
        }
        finally { Win32.FreeLibrary(hModule); }
    }

    // ---- разбор/пересборка дерева VERSIONINFO ----

    /// <summary>Узел дерева VERSIONINFO (String/StringTable/Var/корень) в недеструктивном виде.</summary>
    private sealed class VerNode
    {
        public string Key = "";
        public byte[] Value = Array.Empty<byte>();
        public bool IsTextValue;
        public List<VerNode> Children = new();
    }

    /// <summary>
    /// Переписывает значения строк StringFileInfo: FileDescription/ProductName/InternalName →
    /// alias, OriginalFilename → alias.exe. Возвращает false, если дерево не распарсилось.
    /// </summary>
    private static bool TryPatchVersionBlob(byte[] blob, string alias, out byte[] patched)
    {
        patched = blob;
        try
        {
            var root = ParseNode(blob, 0, out _);
            if (root == null || !root.Key.Equals("VS_VERSION_INFO", StringComparison.Ordinal))
                return false;

            var changed = false;
            foreach (var node in EnumerateStrings(root))
            {
                var repl = node.Key switch
                {
                    "FileDescription" or "ProductName" or "InternalName" => alias,
                    "OriginalFilename" => alias + ".exe",
                    _ => null,
                };
                if (repl == null)
                    continue;
                node.Value = Encoding.Unicode.GetBytes(repl + '\0');
                node.IsTextValue = true;
                changed = true;
            }
            if (!changed)
                return false;

            using var ms = new MemoryStream(blob.Length + 256);
            WriteNode(ms, root);
            patched = ms.ToArray();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Все String-узлы всех StringTable: листья с текстовым значением (wType=1).</summary>
    private static IEnumerable<VerNode> EnumerateStrings(VerNode node)
    {
        if (node.Children.Count == 0)
        {
            if (node.IsTextValue)
                yield return node;
            yield break;
        }
        foreach (var c in node.Children)
            foreach (var s in EnumerateStrings(c))
                yield return s;
    }

    private static VerNode? ParseNode(byte[] buf, int offset, out int nodeEnd)
    {
        nodeEnd = offset;
        if (offset + 6 > buf.Length)
            return null;

        var wLength = BitConverter.ToUInt16(buf, offset);
        var wValueLength = BitConverter.ToUInt16(buf, offset + 2);
        var wType = BitConverter.ToUInt16(buf, offset + 4);
        if (wLength < 6 || offset + wLength > buf.Length)
            return null;

        var node = new VerNode();
        var pos = offset + 6;
        var keyEnd = pos;
        while (keyEnd + 1 < offset + wLength && !(buf[keyEnd] == 0 && buf[keyEnd + 1] == 0))
            keyEnd += 2;
        if (keyEnd + 1 >= offset + wLength)
            return null; // ключ без NUL — битый ресурс
        node.Key = Encoding.Unicode.GetString(buf, pos, keyEnd - pos);
        pos = Align4(keyEnd + 2);

        if (wValueLength > 0)
        {
            var byteLen = wType == 0 ? wValueLength : wValueLength * 2; // текст — в WCHAR'ах
            if (pos + byteLen > offset + wLength)
                return null;
            node.Value = new byte[byteLen];
            Array.Copy(buf, pos, node.Value, 0, byteLen);
            node.IsTextValue = wType != 0;
            pos = Align4(pos + byteLen);
        }

        while (pos + 6 <= offset + wLength)
        {
            var child = ParseNode(buf, pos, out var end);
            if (child == null || end <= pos)
                break;
            node.Children.Add(child);
            pos = Align4(end);
        }
        nodeEnd = offset + wLength;
        return node;
    }

    /// <summary>
    /// Сериализация узла: wLength вычисляется по факту (считаем тело, потом патчим
    /// заголовок возвратом позиции). Padding считается от начала БЛОБА
    /// (s.Position) — у VERSIONINFO все выравнивания на 4 байта от базы ресурса.
    /// </summary>
    private static void WriteNode(Stream s, VerNode node)
    {
        var headerPos = s.Position;
        s.Position += 6; // место под wLength/wValueLength/wType — заполним после тела

        var keyBytes = Encoding.Unicode.GetBytes(node.Key + '\0');
        s.Write(keyBytes, 0, keyBytes.Length);
        WritePadTo4(s);

        if (node.Value.Length > 0)
        {
            s.Write(node.Value, 0, node.Value.Length);
            WritePadTo4(s);
        }
        foreach (var child in node.Children)
        {
            WritePadTo4(s);
            WriteNode(s, child);
        }

        var endPos = s.Position;
        var wLength = (ushort)(endPos - headerPos);
        // wValueLength: для текстовых узлов (wType=1) — длина в WCHAR'ах с NUL,
        // для бинарных (wType=0: FIXEDFILEINFO, Translation-Var) — в байтах.
        var wValueLength = node.Value.Length == 0 ? (ushort)0
            : node.IsTextValue ? (ushort)(node.Value.Length / 2)
            : (ushort)node.Value.Length;
        var wType = (ushort)(node.IsTextValue ? 1 : 0);

        s.Position = headerPos;
        s.Write(BitConverter.GetBytes(wLength), 0, 2);
        s.Write(BitConverter.GetBytes(wValueLength), 0, 2);
        s.Write(BitConverter.GetBytes(wType), 0, 2);
        s.Position = endPos;
    }

    private static int Align4(int pos) => (pos + 3) & ~3;

    private static void WritePadTo4(Stream s)
    {
        var pad = (int)((4 - (s.Position & 3)) & 3);
        for (var i = 0; i < pad; i++) s.WriteByte(0);
    }

    // ===== диагностика до появления логгера =====

    private static void LogStealth(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName, StealthDirName);
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, DebugLogFile);
            if (File.Exists(log) && new FileInfo(log).Length > 262_144) File.Delete(log);
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch { /* лог сам не должен ломать старт */ }
    }

    // ===== Win32 =====

    private static class Win32
    {
        public const int RT_VERSION = 16;
        public const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
        public const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName,
            IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr BeginUpdateResourceW(string pFileName, bool bDeleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType, IntPtr lpName,
            ushort wLanguage, byte[]? lpData, int cbData);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool EndUpdateResourceW(IntPtr hUpdate, bool fDiscard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll")]
        public static extern bool FreeLibrary(IntPtr hLibModule);

        public delegate bool EnumResLangProc(IntPtr hModule, IntPtr lpType, IntPtr lpName,
            ushort wIDLanguage, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumResourceLanguagesW(IntPtr hModule, IntPtr lpType,
            IntPtr lpName, EnumResLangProc lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindResourceExW(IntPtr hModule, IntPtr lpType, IntPtr lpName,
            ushort wLanguage);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("kernel32.dll")]
        public static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);
    }
}
