using System.Runtime.InteropServices;

namespace BrainstormBuddy.Core.Speaker;

internal enum NemoStatus
{
    Ok = 0,
    InvalidArgument = 1,
    OutOfMemory = 2,
    Runtime = 3,
    Cancelled = 4,
}

// nemo_speech_diar_model_config (diar.h). Sequential: после int Gpu идёт 4 байта
// паддинга — Preset выровнен на 8; Marshal.SizeOf = 56 на x64, совпадает с C.
[StructLayout(LayoutKind.Sequential)]
internal struct NemoDiarModelConfig
{
    internal nuint Size;
    internal IntPtr ModelPath;   // UTF-8, NUL-terminated
    internal int Gpu;            // -1 = CPU, >=0 = device index
    internal IntPtr Preset;      // UTF-8 или Zero = дефолт
    internal int ChunkFrames;
    internal int RightContextFrames;
    internal int LeftContextFrames; // <0 = preset-дефолт (0 — валидное явное значение)
    internal int FifoFrames;
    internal int SpkcacheFrames;
    internal int UpdatePeriodFrames;
}

// nemo_speech_diar_segmentation_config — передаём только как Zero (дефолты),
// структура объявлена для полноты ABI. Sequential: 48 байт.
[StructLayout(LayoutKind.Sequential)]
internal struct NemoDiarSegmentationConfig
{
    internal nuint Size;
    internal float Onset;
    internal float Offset;
    internal double PadOnsetSec;
    internal double PadOffsetSec;
    internal double MinGapSec;
    internal double MinDurationSec;
}

// nemo_speech_diar_segment: double+double+int32(+pad) = 24 байта.
[StructLayout(LayoutKind.Sequential)]
internal struct NemoDiarSegment
{
    internal double StartTime;
    internal double EndTime;
    internal int Speaker;        // 1-based
}

/// <summary>
/// P/Invoke поверх nemo_speech_asr_c.dll (C-ABI nemo-speech 0.2.0, diar.h/asr.h).
/// diar_* экспортируются только из тонкого C-wrapper'а nemo_speech_asr_c.dll —
/// в nemo_speech_asr.dll их нет (проверено по экспортам). Хэндлы — opaque IntPtr,
/// size_t -> nuint. Поиск dll: имя без пути, резолвит NemotronBundle через
/// SetDllImportResolver этой сборки.
/// </summary>
internal static class NemoSpeechNative
{
    internal const string LibName = "nemo_speech_asr_c";

    private const CallingConvention Cc = CallingConvention.Cdecl;

    // ---- Model ----
    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern NemoStatus nemo_speech_diar_create(in NemoDiarModelConfig cfg, out IntPtr model);

    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern void nemo_speech_diar_destroy(IntPtr model);

    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern int nemo_speech_diar_num_speakers(IntPtr model);

    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern double nemo_speech_diar_seconds_per_frame(IntPtr model);

    // ---- Streaming ----
    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern NemoStatus nemo_speech_diar_stream_open(IntPtr model, out IntPtr stream);

    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern NemoStatus nemo_speech_diar_stream_push_f32(
        IntPtr stream, [In] float[] samples, nuint nSamples, int sampleRate);

    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern NemoStatus nemo_speech_diar_stream_finish(IntPtr stream);

    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern void nemo_speech_diar_stream_close(IntPtr stream);

    // ---- Offline (stateless) ----
    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern NemoStatus nemo_speech_diar_offline_f32(
        IntPtr model, [In] float[] samples, nuint nSamples, int sampleRate, out IntPtr stream);

    // ---- Results ----
    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern long nemo_speech_diar_frame_count(IntPtr stream);

    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern long nemo_speech_diar_frame_probs_start(IntPtr stream);

    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern NemoStatus nemo_speech_diar_frame_probs(
        IntPtr stream, [Out] float[] outBuf, nuint capacityFloats);

    // seg cfg всегда Zero (дефолты); outBuf = null -> вернётся только count (two-call).
    [DllImport(LibName, CallingConvention = Cc)]
    internal static extern NemoStatus nemo_speech_diar_segments(
        IntPtr stream, IntPtr cfg, [Out] NemoDiarSegment[]? outBuf, nuint capacity, out nuint count);

    // ---- Misc (asr.h, та же библиотека) ----
    [DllImport(LibName, CallingConvention = Cc)]
    private static extern IntPtr nemo_speech_asr_last_error();

    [DllImport(LibName, CallingConvention = Cc)]
    private static extern IntPtr nemo_speech_asr_version();

    // Thread-local: читать только сразу после упавшего вызова на том же потоке.
    internal static string? LastErrorString() => Marshal.PtrToStringUTF8(nemo_speech_asr_last_error());

    internal static string? VersionString() => Marshal.PtrToStringUTF8(nemo_speech_asr_version());
}
