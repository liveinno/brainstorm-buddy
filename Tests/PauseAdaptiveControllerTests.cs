using BrainstormBuddy.Audio;
using Xunit;

namespace BrainstormBuddy.Tests;

/// <summary>
/// Тесты адаптивного оценщика порога эндпойнтинга. Ключевые свойства: сходимость к темпу
/// говорящего, отсутствие коллапса в пол на «плохой» цензурированной подаче, стабильность
/// (без осцилляций), удержание в коридоре.
/// </summary>
public class PauseAdaptiveControllerTests
{
    // Управляемые часы: продвигаем на нужное число секунд между вызовами (для dwell).
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(double sec) => _now = _now.AddSeconds(sec);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static PauseAdaptiveController Make(FakeClock clock, AdaptiveEndpointConfig? cfg = null)
        => new(cfg ?? new AdaptiveEndpointConfig(), coldStartSeconds: 1.2, time: clock);

    // Кормим оценщик паузами (мс) с продвижением часов, чтобы dwell пропускал изменения.
    private static void Feed(PauseAdaptiveController c, FakeClock clock, int gapMs, int times, int frameMs = 30)
    {
        for (int i = 0; i < times; i++)
        {
            c.RecordGap(gapMs / frameMs);
            clock.Advance(5); // >dwell(3с), чтобы применение не блокировалось
        }
    }

    [Fact]
    public void ColdStart_BeforeMinSamples_StaysAtColdStart()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        Assert.Equal(1.2, c.AppliedSeconds, 2);
        c.RecordGap(700 / 30); // 1 наблюдение < MinSamples(8)
        Assert.False(c.IsWarm);
        Assert.Equal(1.2, c.AppliedSeconds, 2); // не двигается, пока мало данных
    }

    [Fact]
    public void FastSpeaker_ShortPauses_ConvergesLow()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        // Быстрый оратор: паузы ~300 мс.
        Feed(c, clock, 300, 40);
        Assert.True(c.IsWarm);
        // Порог должен опуститься заметно ниже cold-start 1.2с, но не ниже пола 0.6с.
        Assert.InRange(c.AppliedSeconds, 0.6, 1.0);
    }

    [Fact]
    public void SlowSpeaker_LongPauses_ConvergesHigher()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        // Вдумчивый оратор: паузы ~1500 мс.
        Feed(c, clock, 1500, 40);
        Assert.True(c.AppliedSeconds > 1.4);
        Assert.True(c.AppliedSeconds <= 2.2); // держится в коридоре
    }

    [Fact]
    public void StaysWithinCorridor_Always()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        // Экстремально длинные паузы (но ≤ MaxGap 4000) — порог не должен вылезти за MaxSeconds.
        Feed(c, clock, 3500, 50);
        Assert.True(c.AppliedSeconds <= 2.2 + 1e-9);
        // Экстремально короткие — не ниже MinSeconds.
        var c2 = Make(clock);
        Feed(c2, clock, 150, 50);
        Assert.True(c2.AppliedSeconds >= 0.6 - 1e-9);
    }

    [Fact]
    public void GapsAboveMaxGap_AreIgnored()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        // Межходовые тишины 6с (> MaxGap 4с) не должны попадать в выборку.
        Feed(c, clock, 6000, 30);
        Assert.False(c.IsWarm); // ни одного валидного наблюдения
        Assert.Equal(1.2, c.AppliedSeconds, 2);
    }

    [Fact]
    public void NoCollapse_UnderCensoredPressure()
    {
        // Анти-коллапс: даже если бы подача была смещена в короткие паузы, независимая выборка
        // не должна утаскивать порог в пол при наличии длинных пауз в потоке.
        var clock = new FakeClock();
        var c = Make(clock);
        // Бимодально: много коротких (400мс) + регулярные длинные (1400мс) — как реальный оратор.
        for (int i = 0; i < 60; i++)
        {
            c.RecordGap((i % 4 == 0 ? 1400 : 400) / 30);
            clock.Advance(5);
        }
        // Порог должен учитывать длинный хвост (p80), а не сидеть на полу 0.6.
        Assert.True(c.AppliedSeconds > 0.7, $"порог схлопнулся в {c.AppliedSeconds:F2}");
    }

    [Fact]
    public void Stable_NoOscillation_OnSteadyInput()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        Feed(c, clock, 600, 30); // прогрев на стабильном темпе
        double settled = c.AppliedSeconds;
        // Ещё одинаковые паузы — deadband/dwell не должны дёргать значение.
        int changes = 0;
        for (int i = 0; i < 20; i++)
        {
            c.RecordGap(600 / 30);
            clock.Advance(5);
            if (c.TryConsumeChange(out _)) changes++;
        }
        Assert.True(Math.Abs(c.AppliedSeconds - settled) < 0.15, "порог дрейфует на стабильном входе");
        Assert.True(changes <= 2, $"слишком много изменений ({changes}) на стабильном входе — осцилляция");
    }

    [Fact]
    public void SeedWarmStart_SetsInitialThreshold()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        c.SeedWarmStart(0.9);
        Assert.Equal(0.9, c.AppliedSeconds, 2);
    }
}
