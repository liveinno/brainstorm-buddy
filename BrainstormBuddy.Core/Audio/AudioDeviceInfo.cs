namespace BrainstormBuddy.Audio;

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsLoopback { get; set; }

    public override string ToString() => IsLoopback ? $"🔊 {Name}" : $"🎤 {Name}";
}

public static class AudioDeviceEnumerator
{
    public static IReadOnlyList<AudioDeviceInfo> GetAvailableDevices()
    {
        // Платформо-зависимое поведение:
        // - Windows: реальный список MMDevice-устройств через NAudio
        // - Прочие ОС: пустой список (приложение работает только на Windows)
        if (!OperatingSystem.IsWindows()) return Array.Empty<AudioDeviceInfo>();
        return GetWindowsDevices();
    }

    private static IReadOnlyList<AudioDeviceInfo> GetWindowsDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.DeviceState.Active);
            foreach (var ep in endpoints)
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = ep.ID,
                    Name = FriendlyName(ep),
                    IsLoopback = false
                });
                ep.Dispose();
            }
            // Устройства ВЫВОДА (Render) — кандидаты для loopback-захвата. Раньше не
            // перечислялись вовсе, из-за чего комбо «Динамик (loopback)» в настройках
            // всегда было пустым и выбор устройства был невозможен.
            var renderEndpoints = enumerator.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.DeviceState.Active);
            foreach (var ep in renderEndpoints)
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = ep.ID,
                    Name = FriendlyName(ep),
                    IsLoopback = true
                });
                ep.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetWindowsDevices: {ex.Message}");
        }
        return devices;
    }

    private static string FriendlyName(NAudio.CoreAudioApi.MMDevice ep)
    {
        try
        {
            return string.IsNullOrEmpty(ep.DeviceFriendlyName)
                ? ep.ID
                : ep.DeviceFriendlyName;
        }
        catch
        {
            return ep.ID;
        }
    }
}
