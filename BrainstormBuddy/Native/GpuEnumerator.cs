using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace BrainstormBuddy.Native;

/// <summary>
/// Список видеокарт (WMI). Индекс соответствует порядку адаптеров, который обычно совпадает
/// с DXGI-индексом DirectML (0 — встройка, 1+ — дискретные). Для точности юзер выбирает GPU
/// по имени в настройках и проверяет кнопкой «Замерить скорость».
/// </summary>
public static class GpuEnumerator
{
    public record GpuInfo(int Index, string Name, long VramMb);

    public static List<GpuInfo> List()
    {
        var res = new List<GpuInfo>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            int i = 0;
            foreach (ManagementObject o in s.Get())
            {
                string name = o["Name"]?.ToString() ?? $"GPU {i}";
                long vram = 0;
                try { vram = System.Convert.ToInt64(o["AdapterRAM"] ?? 0L) / (1024 * 1024); } catch { }
                res.Add(new GpuInfo(i, name, vram));
                i++;
            }
        }
        catch { }
        return res;
    }

    /// <summary>Индекс дискретной видеокарты (не Intel/встройка), с наибольшим VRAM; иначе -1.</summary>
    public static int BestDiscreteIndex()
    {
        var disc = List()
            .Where(g => !IsIntegrated(g.Name))
            .OrderByDescending(g => g.VramMb)
            .FirstOrDefault();
        return disc?.Index ?? -1;
    }

    private static bool IsIntegrated(string name) =>
        name.Contains("Intel", System.StringComparison.OrdinalIgnoreCase) ||
        name.Contains("UHD", System.StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Iris", System.StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Radeon(TM) Graphics", System.StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Microsoft Basic", System.StringComparison.OrdinalIgnoreCase);
}
