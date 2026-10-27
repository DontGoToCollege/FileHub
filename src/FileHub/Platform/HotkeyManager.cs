using System.Windows.Input;
using System.Windows.Interop;

namespace FileHub.Platform;

/// <summary>Global hotkeys via RegisterHotKey on a hidden message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("FileHubHotkeys")
        {
            Width = 0, Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ParentWindow = new IntPtr(-3)              // HWND_MESSAGE
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>Registers a hotkey from a string like "Ctrl+Alt+Space". Returns false if unavailable.</summary>
    public bool Register(string gesture, Action action, out int id)
    {
        id = 0;
        if (!TryParse(gesture, out uint mods, out uint vk)) return false;
        id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk)) { id = 0; return false; }
        _actions[id] = action;
        return true;
    }

    public void Unregister(int id)
    {
        if (id == 0) return;
        Native.UnregisterHotKey(_source.Handle, id);
        _actions.Remove(id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys.ToList()) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": case "windows": mods |= Native.MOD_WIN; break;
                default:
                    if (Enum.TryParse<Key>(raw, true, out var key)) vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    else if (raw.Length == 1) vk = char.ToUpperInvariant(raw[0]);
                    else return false;
                    break;
            }
        }
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue((int)wParam, out var a))
        {
            handled = true;
            try { a(); } catch (Exception ex) { Log.Error("Hotkey action failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
