using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BrainstormBuddy.Stt;

/// <summary>
/// WAV (любой формат: sr/каналы/битность) → 16 кГц mono float [-1..1] —
/// единый входной преобразователь встроенных движков GigaAM v2/v3.
/// </summary>
internal static class WavPcm
{
    public static float[] WavTo16kMono(byte[] wav)
    {
        using var ms = new MemoryStream(wav);
        using var reader = new WaveFileReader(ms);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sp.WaveFormat.SampleRate != MelFrontend.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, MelFrontend.SampleRate);

        var outp = new List<float>();
        var buf = new float[16000];
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) outp.Add(buf[i]);
        return outp.ToArray();
    }
}
