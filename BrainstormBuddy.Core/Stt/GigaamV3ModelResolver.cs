namespace BrainstormBuddy.Stt;

/// <summary>
/// Поиск файлов модели GigaAM v3 (v3_e2e_ctc.int8.onnx + v3_e2e_ctc_vocab.txt в одной папке).
/// Вынесено из App в Core — чистая функция, покрывается юнит-тестами без WPF.
/// Приоритет каталогов задаёт вызывающий (config → %APPDATA%\models → {app}\models → artifacts).
/// </summary>
public static class GigaamV3ModelResolver
{
    /// <summary>
    /// Возвращает (onnx, vocab), если пара найдена: сначала configPath как файл или каталог,
    /// затем searchDirs по порядку. Иначе (null, null).
    /// </summary>
    public static (string? onnx, string? vocab) Resolve(string? configPath, IEnumerable<string> searchDirs)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var p = configPath.Trim();
            // Путь может указывать на файл модели или на каталог с ней.
            if (File.Exists(p)) candidates.Add(p);
            else if (Directory.Exists(p)) candidates.Add(Path.Combine(p, GigaamV3CtcDecoder.ModelFileName));
            else candidates.Add(p); // несуществующий файл — File.Exists отсечёт ниже
        }
        foreach (var d in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var direct = Path.Combine(d, GigaamV3CtcDecoder.ModelFileName);
            candidates.Add(direct);
            // модель может лежать во вложенной папке (напр. artifacts/gigaam-v3)
            candidates.Add(Path.Combine(d, "gigaam-v3", GigaamV3CtcDecoder.ModelFileName));
        }

        foreach (var m in candidates)
        {
            if (!File.Exists(m)) continue;
            var vocab = Path.Combine(Path.GetDirectoryName(m)!, GigaamV3CtcDecoder.VocabFileName);
            if (File.Exists(vocab)) return (Path.GetFullPath(m), Path.GetFullPath(vocab));
        }
        return (null, null);
    }
}
