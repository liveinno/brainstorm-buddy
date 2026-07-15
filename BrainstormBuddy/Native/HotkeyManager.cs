using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BrainstormBuddy.Native;

public class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyDefinition> _registered = new();
    private int _nextId = 1;

    public event EventHandler<string>? HotkeyPressed;

    public HotkeyManager(Window window)
    {
        _window = window;
        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.Handle)!;
        _source.AddHook(WndProc);
    }

    public bool Register(string combination, string actionId)
    {
        if (!TryParse(combination, out var modifiers, out var vk))
            return false;

        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, vk))
        {
            _nextId--;
            return false;
        }
        _registered[id] = new HotkeyDefinition(actionId, combination);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
            UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue((int)wParam, out var def))
        {
            HotkeyPressed?.Invoke(this, def.ActionId);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static bool TryParse(string combination, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combination)) return false;

        var parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries);
        string? keyPart = null;
        foreach (var p in parts)
        {
            var part = p.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL; break;
                case "shift":
                    modifiers |= MOD_SHIFT; break;
                case "alt":
                    modifiers |= MOD_ALT; break;
                case "win":
                case "meta":
                    modifiers |= MOD_WIN; break;
                default:
                    keyPart = part; break;
            }
        }
        if (keyPart == null) return false;
        return TryParseKey(keyPart, out vk);
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c >= 'A' && c <= 'Z') { vk = (uint)c; return true; }
            if (c >= '0' && c <= '9') { vk = (uint)c; return true; }
        }
        return key.ToLowerInvariant() switch
        {
            "f1" => Assign(out vk, 0x70),
            "f2" => Assign(out vk, 0x71),
            "f3" => Assign(out vk, 0x72),
            "f4" => Assign(out vk, 0x73),
            "f5" => Assign(out vk, 0x74),
            "f6" => Assign(out vk, 0x75),
            "f7" => Assign(out vk, 0x76),
            "f8" => Assign(out vk, 0x77),
            "f9" => Assign(out vk, 0x78),
            "f10" => Assign(out vk, 0x79),
            "f11" => Assign(out vk, 0x7A),
            "f12" => Assign(out vk, 0x7B),
            "space" => Assign(out vk, 0x20),
            "esc" or "escape" => Assign(out vk, 0x1B),
            "tab" => Assign(out vk, 0x09),
            _ => false
        };
    }

    private static bool Assign(out uint target, uint value)
    {
        target = value;
        return true;
    }

    public void Dispose() => UnregisterAll();

    private record HotkeyDefinition(string ActionId, string Combination);
}
