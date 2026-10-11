using System.Reflection;
using System.Runtime.InteropServices;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Speaker;

/// Одноразовый резолвер нативного бандла nemo-speech: находит каталог с
/// nemo_speech_asr_c.dll, добавляет его в user-DLL-search-dirs (AddDllDirectory)
/// и грузит LoadLibraryEx(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | USER_DIRS |
/// DLL_LOAD_DIR) — сателлиты ggml-*.dll / vc-runtime лежат рядом. Держим
/// DLL_DIRECTORY_COOKIE на весь процесс: зависимости могут догружаться лениво.
/// NativeLibrary.Load+DllImportSearchPath на части машин не ищет зависимости
/// в каталоге целевой dll (0x8007007E) — поэтому прямой WinAPI.
/// DllImport на "nemo_speech_asr_c" доходит до уже загруженного хэндла
/// через SetDllImportResolver этой сборки — глобальный SetDllDirectory не трогаем.
internal static class NemotronBundle
{
    private const string LogCategory = "Speaker";
    private const string LibFile = "nemo_speech_asr_c.dll";
    private const string ImplFile = "nemo_speech_asr.dll"; // реализация, нужна как зависимость
    private const string BundleDirName = "nemo-speech";
    private const string VersionedDir = "nemo-speech-0.2.0-windows-x86_64";

    private static readonly object LoadLock = new();
    private static IntPtr _handle;
    private static string? _loadedDir;
    private static string? _error;    // последний провал; валиден только для _errorKey
    private static string? _errorKey; // набор кандидат-каталогов, на котором случился _error
    private static int _resolverInstalled; // 0/1 — SetDllImportResolver ставим один раз

    /// <summary>
    /// backend: "auto" (cuda → vulkan → cpu) | "cpu" | "vulkan" | "cuda".
    /// ok=true: Dir — каталог бандла; ok=false: Error — последняя причина.
    /// Успех кэшируется навсегда (dll одна на процесс); ошибка — только для
    /// того же набора кандидатов: смена backend в настройках перепроверяет
    /// заново (бандл мог появиться или лежать в другом каталоге).
    /// </summary>
    internal static (bool Ok, string? Dir, string? Error) TryLoad(string backend, LoggingService? logger)
    {
        lock (LoadLock)
        {
            if (_handle != IntPtr.Zero)
                return (true, _loadedDir, null);

            // Кэш провала действует, пока набор каталогов-кандидатов тот же;
            // другой backend → другие каталоги → проверяем заново.
            var dirs = CandidateDirs(backend).ToList();
            var key = string.Join('|', dirs);
            if (_error != null && _errorKey == key)
                return (false, null, _error);

            string? lastError = null;
            foreach (var dir in dirs)
            {
                if (!IsBundleDir(dir))
                    continue;

                var path = Path.Combine(dir, LibFile);
                // AddDllDirectory ставит каталог бандла в user-search-dirs;
                // LoadLibraryEx грузит с DLL_LOAD_DIR (свои) + USER_DIRS (транзитивные
                // зависимости сателлитов). Cookie живёт весь процесс — зависимости
                // dll могут подтягиваться лениво (delay-load, LoadLibrary изнутри).
                var cookie = AddDllDirectory(dir);
                if (cookie == IntPtr.Zero)
                {
                    lastError = $"{dir}: AddDllDirectory failed 0x{Marshal.GetLastWin32Error():X}";
                    logger?.Warn($"nemo-speech bundle load failed: {lastError}", LogCategory);
                    continue;
                }
                var h = LoadLibraryEx(path, IntPtr.Zero,
                    LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRECTORIES
                    | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
                if (h == IntPtr.Zero)
                {
                    lastError = $"{path}: LoadLibraryEx failed 0x{Marshal.GetLastWin32Error():X}";
                    logger?.Warn($"nemo-speech bundle load failed: {lastError}", LogCategory);
                    continue;
                }

                try
                {
                    EnsureResolver();
                }
                catch (Exception ex)
                {
                    lastError = $"SetDllImportResolver: {ex.Message}";
                    logger?.Warn($"nemo-speech resolver failed: {lastError}", LogCategory);
                    NativeLibrary.Free(h);
                    continue;
                }

                _handle = h;
                _loadedDir = dir;
                _error = null;
                _errorKey = null;
                logger?.Info($"nemo-speech bundle loaded: {dir} (backend={backend}, lib={NemoSpeechNative.VersionString() ?? "?"})", LogCategory);
                return (true, dir, null);
            }

            _error = lastError ?? $"nemo-speech bundle dir not found for backend '{backend}'";
            _errorKey = key;
            return (false, null, _error);
        }
    }

    /// <summary>"auto"/пусто → GPU-first: cuda → vulkan → cpu (cpu — последний фолбэк).
    /// Vulkan-бандл умеет cpu-режим (gpu=-1); cuda-бандл подцепляется в dev-layout
    /// из .work/nemo-dist или из {app}/nemo-speech, если юзер подложил cuda-бинари.</summary>
    private static IEnumerable<string> BackendCandidates(string? backend)
        => backend?.Trim().ToLowerInvariant() switch
        {
            "cpu" => new[] { "cpu" },
            "vulkan" => new[] { "vulkan" },
            "cuda" => new[] { "cuda" },
            _ => new[] { "cuda", "vulkan", "cpu" },
        };

    private static IEnumerable<string> CandidateDirs(string backend)
    {
        var baseDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(baseDir, BundleDirName);
        var publishSeen = false;
        foreach (var be in BackendCandidates(backend))
        {
            // publish-layout единый (независимо от backend-тега) — проверяем один раз.
            if (!publishSeen)
            {
                publishSeen = true;
                yield return bundled;
            }
            // dev-layout: .work лежит в корне репо; идём вверх от bin\… до корня.
            for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
                yield return Path.Combine(dir.FullName, ".work", "nemo-dist",
                    $"x-{be}", $"{VersionedDir}-{be}", "bin");
        }
    }

    private static bool IsBundleDir(string dir)
        => File.Exists(Path.Combine(dir, LibFile))
           && File.Exists(Path.Combine(dir, ImplFile))
           && Directory.GetFiles(dir, "ggml*.dll").Length > 0;

    private static void EnsureResolver()
    {
        if (Interlocked.CompareExchange(ref _resolverInstalled, 1, 0) == 0)
            NativeLibrary.SetDllImportResolver(typeof(NemoSpeechNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var name = Path.GetFileNameWithoutExtension(libraryName);
        if (string.Equals(name, NemoSpeechNative.LibName, StringComparison.OrdinalIgnoreCase))
            return _handle; // Zero -> обычный поиск до первого TryLoad
        return IntPtr.Zero;
    }

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_USER_DIRECTORIES = 0x00000400;
    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string dir);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
}
