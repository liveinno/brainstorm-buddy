using System;
using System.Linq;
using System.Management;

namespace BrainstormBuddy.Native;

/// <summary>
/// Экспресс-проверка оборудования (вариант А): CPU / ОЗУ / GPU по спекам (WMI) →
/// вердикт «потянет / слабое» + рекомендация движка STT. Без реального бенча (мгновенно).
/// </summary>
public static class HardwareInfo
{
    // Tier: 0 = слабое, 1 = среднее, 2 = мощное.
    public record Report(
        string CpuName, int PhysicalCores, int LogicalCores, double RamGb,
        string GpuName, bool HasDiscreteGpu, double GpuVramGb,
        int Tier, string Verdict, string Recommendation);

    public static Report Gather()
    {
        int logical = Environment.ProcessorCount;
        string cpu = "неизвестно";
        int cores = 0;
        double ramGb = 0;

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                cpu = o["Name"]?.ToString()?.Trim() ?? cpu;
                cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
            }
        }
        catch { /* WMI недоступен */ }
        if (cores == 0) cores = Math.Max(1, logical / 2);

        try
        {
            using var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject o in s.Get())
                ramGb = Convert.ToInt64(o["TotalPhysicalMemory"] ?? 0L) / (1024.0 * 1024 * 1024);
        }
        catch { /* WMI недоступен */ }

        var gpus = GpuEnumerator.List();
        int discIdx = GpuEnumerator.BestDiscreteIndex();
        bool discrete = discIdx >= 0;
        var best = discrete ? gpus.FirstOrDefault(g => g.Index == discIdx) : gpus.FirstOrDefault();
        string gpuName = best?.Name ?? "—";
        double vramGb = (best?.VramMb ?? 0) / 1024.0;

        int tier;
        string verdict, rec;
        if (discrete)
        {
            tier = 2;
            verdict = "🟢 Отличное железо — есть дискретная видеокарта.";
            rec = "Whisper turbo пойдёт на GPU (быстро + качество: пунктуация, английский). " +
                  "GigaAM — быстрый для живого ассистента. Для файлов рекомендуем Whisper.";
        }
        else if (cores >= 8 && ramGb >= 15)
        {
            tier = 1;
            verdict = "🟡 Среднее железо — мощный CPU, но без дискретного GPU.";
            rec = "GigaAM работает отлично. Whisper turbo на CPU тоже пойдёт, но небыстро " +
                  "(~реальное время: файл на 30 мин ≈ 30–40 мин). Для длинных записей запаситесь временем.";
        }
        else
        {
            tier = 0;
            verdict = "🔴 Слабое железо (мало ядер/ОЗУ или только встроенная графика).";
            rec = "Используйте GigaAM (быстрый, из коробки). Whisper turbo — только для коротких " +
                  "файлов, иначе будет очень медленно.";
        }

        return new Report(cpu, cores, logical, Math.Round(ramGb, 1), gpuName, discrete,
            Math.Round(vramGb, 1), tier, verdict, rec);
    }
}
