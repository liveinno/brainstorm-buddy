using BrainstormBuddy.Audio;
using Xunit;

namespace BrainstormBuddy.Tests;

public class AudioBufferTests
{
    [Fact]
    public void AddSamples_ThenGetChunk_ReturnsValidWav()
    {
        var buffer = new AudioBuffer(16000, 8, 0.5, 0.02);
        var samples = new float[16000]; // 1 sec silence
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 16000) * 0.3f;

        buffer.AddSamples(samples);
        var wav = buffer.GetChunkForTranscription();

        Assert.NotEmpty(wav);
        // WAV header: "RIFF"....."WAVE"....."fmt "....."data"
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var buffer = new AudioBuffer(16000, 8, 0.5, 0.02);
        var samples = new float[8000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.1f;

        buffer.AddSamples(samples);
        Assert.True(buffer.CurrentSampleCount > 0);

        buffer.Reset();
        // после Reset в буфере остаётся только overlap (1 сек = 16000 сэмплов),
        // либо 0 если изначально было < 16000
        Assert.True(buffer.CurrentSampleCount <= 16000);
    }

    [Fact]
    public void OverlapLogic_PreservesContext()
    {
        var buffer = new AudioBuffer(16000, 2, 0.5, 0.02);
        // Добавляем 3 секунды данных (больше, чем chunk max)
        var bigChunk = new float[48000];
        for (int i = 0; i < bigChunk.Length; i++)
            bigChunk[i] = (float)(Math.Sin(2 * Math.PI * 200 * i / 16000) * 0.3);

        buffer.AddSamples(bigChunk);
        // Буфер не должен расти бесконечно
        Assert.True(buffer.CurrentSampleCount <= 48000 + 16000);
    }

    [Fact]
    public void CurrentRms_OfSilenceIsZero()
    {
        var buffer = new AudioBuffer(16000, 8, 0.5, 0.02);
        buffer.AddSamples(new float[16000]);
        Assert.Equal(0f, buffer.CurrentRms, 3);
    }

    [Fact]
    public void CurrentRms_OfSignalIsPositive()
    {
        var buffer = new AudioBuffer(16000, 8, 0.5, 0.02);
        var samples = new float[16000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.5f;
        buffer.AddSamples(samples);
        Assert.True(buffer.CurrentRms > 0.4f);
    }

    // --- Авто-калибровка порога: тихий источник (видео/loopback ~0.005 RMS) ---
    // Регрессия «превратилось в тыкву»: тихий loopback-звук не дотягивал до фиксированного
    // порога (для mode 3 эффективный пол = 0.006) → речь ловилась урывками/не ловилась.
    // Проверяем оба режима: авто ловит речь; фиксированный 0.01 — теряет (репро бага).

    private const int SpeechHz = 400;     // zcr ~5 попадает в окно mode 3 [3..40]
    private const double QuietSpeechRms = 0.005; // ниже фикс-пола 0.006, выше авто-порога
    private const double SilenceRms = 0.0006;    // тихий фон (loopback без речи)

    private static float[] Sine(int samples, double rms, int hz)
    {
        double amp = rms * Math.Sqrt(2);
        var buf = new float[samples];
        for (int i = 0; i < samples; i++)
            buf[i] = (float)(Math.Sin(2 * Math.PI * hz * i / 16000.0) * amp);
        return buf;
    }

    // Прогоняет тихий сигнал (warmup-тишина → 3× [речь 2с + пауза 1.2с]) через буфер,
    // синхронно двигая фейковое время (иначе minSpeech по стенным часам глушит синтетику).
    private static int RunQuietSource(bool autoCalibrate)
    {
        var fakeTime = new FakeTime();
        var buffer = new AudioBuffer(16000, 60, 0.8, 0.01, vadMode: 3,
            preRollMs: 400, postRollMs: 500, overlapMs: 800, minSpeechMs: 400, timeProvider: fakeTime);
        buffer.UpdateParameters(0.01, 0.8, 400, 500, 400, autoCalibrate);

        int emitted = 0;
        long fed = 0;
        void Feed(float[] block)
        {
            const int frame = 480;
            for (int i = 0; i + frame <= block.Length; i += frame)
            {
                var f = new float[frame];
                Array.Copy(block, i, f, 0, frame);
                fakeTime.SetUtcNow(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(fed / 16000.0));
                buffer.AddSamples(f);
                fed += frame;
                if (buffer.TryGetReadyChunk(out var wav) && wav.Length > 0) emitted++;
            }
        }

        Feed(Sine(16000 * 3, SilenceRms, 100));        // 3с прогрев тишиной → шумовой пол оседает
        for (int k = 0; k < 3; k++)
        {
            Feed(Sine((int)(16000 * 2.0), QuietSpeechRms, SpeechHz)); // 2с речи
            Feed(Sine((int)(16000 * 1.2), SilenceRms, 100));          // 1.2с паузы → эмит
        }
        return emitted;
    }

    [Fact]
    public void AutoCalibration_QuietSource_DetectsSpeech()
    {
        int emitted = RunQuietSource(autoCalibrate: true);
        Assert.True(emitted >= 2, $"Авто-калибровка должна ловить тихую речь, эмитов={emitted}");
    }

    [Fact]
    public void FixedThreshold_QuietSource_MissesSpeech_ReproducesBug()
    {
        int emitted = RunQuietSource(autoCalibrate: false);
        Assert.Equal(0, emitted); // фикс-пол 0.006 > речь 0.005 → «тыква» (речь не набирается)
    }

    // Регрессия «фантомной речи» на рваном микрофонном шуме (пары кадров 0.006 / пары 0.001,
    // среднее ~0.0035): старый пол-минимум сползал к 0.001 → порог 3.5×0.001 ≈ 0.0035 НИЖЕ
    // спайков → вечная ложная «речь» (красные столбики без конца). Робастный пол (EMA-сглаживание
    // + заморозка во время речи) держит порог выше спайков → ни одной ложной фразы за минуту.
    [Fact]
    public void AutoCalibration_SpikyNoise_NoPhantomSpeech()
    {
        var fakeTime = new FakeTime();
        var buffer = new AudioBuffer(16000, 60, 0.8, 0.01, vadMode: 3,
            preRollMs: 400, postRollMs: 500, overlapMs: 800, minSpeechMs: 400, timeProvider: fakeTime);
        buffer.UpdateParameters(0.01, 0.8, 400, 500, 400, autoCalibrate: true);

        long fed = 0;
        const int frame = 480;
        for (int i = 0; i < 2000; i++) // 60 секунд шума 30мс-кадрами
        {
            double rms = (i / 2) % 2 == 0 ? 0.006 : 0.001;
            var f = Sine(frame, rms, SpeechHz);
            fakeTime.SetUtcNow(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(fed / 16000.0));
            buffer.AddSamples(f);
            fed += frame;
            buffer.TryGetReadyChunk(out _);
        }
        Assert.Equal(0, buffer.UtterancesEmitted);
    }

    // Регрессия «защёлки замороженного пола» (баг 2.5.5, живой тест: 30с работает → красная
    // стена и ноль текста): калибровка в тихой комнате прижимает порог к низу; затем непрерывный
    // звук (видео) держит буфер вечно «в речи», пол заморожен, тишина не наступает → до фикса
    // эмитов НЕТ до ChunkMax=60с. Латч-брейкер обязан резать сплошной звук каждые ~10с.
    [Fact]
    public void ContinuousSound_LatchBreaker_EmitsRollingChunks()
    {
        var fakeTime = new FakeTime();
        var buffer = new AudioBuffer(16000, 60, 1.8, 0.01, vadMode: 3,
            preRollMs: 400, postRollMs: 500, overlapMs: 800, minSpeechMs: 400, timeProvider: fakeTime);
        buffer.UpdateParameters(0.01, 1.8, 400, 500, 400, autoCalibrate: true);

        long fed = 0;
        const int frame = 480;
        int emitted = 0;
        void Feed(double rms, double seconds)
        {
            int frames = (int)(seconds * 1000 / 30);
            for (int i = 0; i < frames; i++)
            {
                fakeTime.SetUtcNow(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(fed / 16000.0));
                buffer.AddSamples(Sine(frame, rms, SpeechHz));
                fed += frame;
                if (buffer.TryGetReadyChunk(out var w) && w.Length > 0) emitted++;
            }
        }

        Feed(0.0006, 3);   // калибровка в тихой комнате → порог у нижнего клампа
        Feed(0.02, 35);    // непрерывный звук 35с без единой паузы (видео/музыка)
        Assert.True(emitted >= 2,
            $"Латч-брейкер: за 35с сплошного звука должно быть ≥2 катящихся чанков, было {emitted}");
    }

    // «Протухание» уровня: WASAPI loopback при паузе рендера перестаёт слать кадры — CurrentRms
    // не должен вечно возвращать последнее речевое значение (баг «бесконечные красные столбики»).
    [Fact]
    public void CurrentRms_GoesStale_WhenNoSamplesArrive()
    {
        var buffer = new AudioBuffer(16000, 8, 0.5, 0.02);
        var samples = new float[16000];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f;
        buffer.AddSamples(samples);
        Assert.True(buffer.CurrentRms > 0.4f);   // живой сигнал виден сразу

        System.Threading.Thread.Sleep(500);       // кадры перестали приходить (пауза рендера)
        Assert.Equal(0f, buffer.CurrentRms);      // уровень протух → 0, а не замёрзшее значение
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }
}
