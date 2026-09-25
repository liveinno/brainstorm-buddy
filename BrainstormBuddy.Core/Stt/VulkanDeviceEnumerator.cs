using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Порядок устройств vkEnumeratePhysicalDevices (Vulkan) — именно его использует whisper.cpp
/// в параметре gpu_device. НЕ совпадает с DXGI/WMI-порядком (GpuEnumerator): интеграшки,
/// Microsoft Basic Render и фильтрация драйвером сдвигают нумерацию. Маппинг делаем по
/// нормализованному имени устройства; если не сошлось — возвращаем исходный индекс
/// (best-effort, историческое поведение env GGML_VK_VISIBLE_DEVICES).
/// </summary>
public static class VulkanDeviceEnumerator
{
    // Смещение deviceName внутри VkPhysicalDeviceProperties (x64/x86 одинаково):
    // apiVersion(4) + driverVersion(4) + vendorID(4) + deviceID(4) + deviceType(4) = 20.
    private const int DeviceNameOffset = 20;
    private const int VkMaxPhysicalDeviceNameSize = 256;
    // Полный VkPhysicalDeviceProperties (limits+features ~750 байт) — с запасом.
    private const int PropsBufferSize = 4096;
    private const int VkStructureTypeInstanceCreateInfo = 1;
    private const int VkSuccess = 0;

    public record VkDevice(int Index, string Name);

    /// <summary>Имена GPU в порядке vkEnumeratePhysicalDevices. Пусто, если vulkan-1.dll нет
    /// (система без Vulkan-драйверов) — тогда маппинг прозрачно пропускает индекс.</summary>
    public static List<VkDevice> List()
    {
        var res = new List<VkDevice>();
        if (!OperatingSystem.IsWindows()) return res; // whisper.cpp-Vulkan у нас только под Win
        IntPtr instance = IntPtr.Zero;
        IntPtr props = IntPtr.Zero;
        try
        {
            var ci = new VkInstanceCreateInfo { sType = VkStructureTypeInstanceCreateInfo };
            if (vkCreateInstance(ref ci, IntPtr.Zero, out instance) != VkSuccess || instance == IntPtr.Zero)
                return res;

            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero) != VkSuccess || count == 0)
                return res;
            IntPtr devicesBuf = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            try
            {
                if (vkEnumeratePhysicalDevices(instance, ref count, devicesBuf) != VkSuccess)
                    return res;
                props = Marshal.AllocHGlobal(PropsBufferSize);
                for (int i = 0; i < (int)count; i++)
                {
                    IntPtr dev = Marshal.ReadIntPtr(devicesBuf, i * IntPtr.Size);
                    vkGetPhysicalDeviceProperties(dev, props);
                    string name = Marshal.PtrToStringAnsi(props + DeviceNameOffset, VkMaxPhysicalDeviceNameSize)
                                  ?? $"GPU {i}";
                    res.Add(new VkDevice(i, name));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(devicesBuf);
            }
        }
        catch (Exception)
        {
            // DllNotFound / EntryPointNotFound / мусорный драйвер — Vulkan не доступен.
            res.Clear();
        }
        finally
        {
            if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
            if (instance != IntPtr.Zero) vkDestroyInstance(instance, IntPtr.Zero);
        }
        return res;
    }

    /// <summary>
    /// Системный (DXGI/WMI) индекс адаптера → индекс в порядке vkEnumeratePhysicalDevices.
    /// wmiName — имя адаптера из GpuEnumerator.List() (WMI Win32_VideoController.Name).
    /// Совпадение по нормализованному имени; не нашли — возвращаем wmiIndex (best-effort;
    /// whisper.cpp сам отфутболит невалидный индекс на устройство 0).
    /// </summary>
    public static int MapSystemIndex(int wmiIndex, string? wmiName)
    {
        try
        {
            var vk = List();
            if (vk.Count == 0) return wmiIndex;
            var needle = NormalizeName(wmiName);
            if (needle.Length > 0)
            {
                var hit = vk.FirstOrDefault(d => NormalizeName(d.Name) == needle);
                // Слабый вариант: одно имя содержит другое ("RTX 4070" vs "GeForce RTX 4070").
                if (hit == null)
                    hit = vk.FirstOrDefault(d =>
                    {
                        var v = NormalizeName(d.Name);
                        return v.Length > 0 && (v.Contains(needle) || needle.Contains(v));
                    });
                if (hit != null) return hit.Index;
            }
            // Имя не совпало: passthrough исходного индекса (best-effort; порядки DXGI и
            // Vulkan часто совпадают, а вне-диапазонный gpu_device whisper.cpp сам
            // отфутболит на устройство 0).
            return wmiIndex;
        }
        catch { return wmiIndex; }
    }

    // Нормализация имён из разных стеков: "NVIDIA GeForce RTX 4070" (WMI) vs
    // "GeForce RTX 4070" (драйвер/Vulkan), "(TM)"/"(R)", вендор-префиксы, пробелы.
    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"\((r|tm|c)\)|[®™©]", " ");
        s = Regex.Replace(s, @"\b(nvidia|intel|amd|advanced micro devices|corporation|inc)\b", " ");
        s = Regex.Replace(s, @"[^a-z0-9]+", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // ---- минимальный P/Invoke vulkan-1.dll ----

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;        // VkApplicationInfo* — NULL допустим
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int vkCreateInstance(ref VkInstanceCreateInfo pCreateInfo,
        IntPtr pAllocator, out IntPtr pInstance);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance,
        ref uint pPhysicalDeviceCount, IntPtr pPhysicalDevices);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr physicalDevice,
        IntPtr pProperties);
}
