using System.Text;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Загруженный образец речи для честного замера скорости STT: байты WAV,
/// длительность в секундах (по заголовку RIFF) и путь, откуда файл взят.
/// </summary>
public sealed record SttSpeedSample(byte[] Wav, double DurationSeconds, string SourcePath);

/// <summary>
/// Поиск и разбор эталонного образца речи (16 кГц mono 16 бит, русская фраза
/// ~2.7 с из testaudios/slices/short/a1_p1.wav). Ищется рядом с бинарником
/// (Assets), затем в каталоге данных и, в dev-режиме, в исходниках репозитория
/// (testaudios).
/// </summary>
public static class SttSampleAudio
{
    /// <summary>Имя файла-образца (кладётся в Assets рядом с exe).</summary>
    public const string SampleFileName = "stt-speed-sample.wav";

    /// <summary>Прочитать первый найденный образец; null, если ничего не нашлось.</summary>
    public static SttSpeedSample? TryLoad()
    {
        foreach (var path in CandidatePaths())
        {
            byte[] wav;
            try
            {
                if (!File.Exists(path)) continue;
                wav = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Файл есть, но не читается — пробуем следующего кандидата.
                continue;
            }

            return new SttSpeedSample(wav, GetWavDurationSeconds(wav), path);
        }

        return null;
    }

    /// <summary>
    /// Длительность WAV в секундах: dataBytes / (sampleRate * channels * bytesPerSample).
    /// Чанки ищутся перебором (data не обязан быть на смещении 44); 0, если разбор не удался.
    /// </summary>
    public static double GetWavDurationSeconds(byte[] wav)
    {
        if (wav is null || wav.Length < 12) return 0;
        if (!HasTag(wav, 0, "RIFF") || !HasTag(wav, 8, "WAVE")) return 0;

        int channels = 0, sampleRate = 0, bitsPerSample = 0;
        long dataBytes = -1;

        // Идём по чанкам: [4 байта id][4 байта size LE][size байт данных][паддинг до чётного].
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var size = ReadInt32Le(wav, offset + 4);
            if (size < 0) break; // мусорный размер — дальше идти бессмысленно

            var header = offset + 8;
            var chunk = Math.Min((long)size, wav.Length - header); // обрезаем по фактической длине файла
            if (id == "fmt " && chunk >= 16)
            {
                channels = ReadUInt16Le(wav, header + 2);
                sampleRate = ReadInt32Le(wav, header + 4);
                bitsPerSample = ReadUInt16Le(wav, header + 14);
            }
            else if (id == "data" && dataBytes < 0)
            {
                dataBytes = chunk;
            }

            if (dataBytes >= 0 && channels > 0 && sampleRate > 0 && bitsPerSample > 0) break;
            offset = header + (int)chunk + (size & 1);
        }

        if (dataBytes < 0 || channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0) return 0;
        return dataBytes / ((double)sampleRate * channels * bitsPerSample / 8.0);
    }

    /// <summary>Кандидаты пути к образцу, по очереди.</summary>
    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", SampleFileName);
        yield return Path.Combine(AppContext.BaseDirectory, SampleFileName);
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BrainstormBuddy", "samples", SampleFileName);

        // Dev-режим: поднимаемся от bin\...\ от 0 до 8 уровней в поисках репозитория
        // с исходным образцом testaudios/slices/short/a1_p1.wav.
        var dir = AppContext.BaseDirectory;
        for (var level = 0; level <= 8 && dir is not null; level++)
        {
            yield return Path.Combine(dir, "testaudios", "slices", "short", "a1_p1.wav");
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static bool HasTag(byte[] wav, int offset, string tag) =>
        wav[offset] == tag[0] && wav[offset + 1] == tag[1] &&
        wav[offset + 2] == tag[2] && wav[offset + 3] == tag[3];

    private static int ReadInt32Le(byte[] wav, int offset) =>
        wav[offset] | wav[offset + 1] << 8 | wav[offset + 2] << 16 | wav[offset + 3] << 24;

    private static int ReadUInt16Le(byte[] wav, int offset) => wav[offset] | wav[offset + 1] << 8;
}
