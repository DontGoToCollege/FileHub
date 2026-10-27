using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FileHub.Platform;

/// <summary>
/// Low-level keyboard hook that lets FileHub take over the Start menu (Win key tap) and Win+E.
/// Technique: on a Win key press we inject a harmless no-op key (VK 0xE8) so Windows treats the
/// press as a chord and does NOT open its own Start menu; on release with no other key pressed we
/// show ours. Win+(other key) shortcuts keep working normally.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const ushort VK_NOOP = 0xE8;
    private const uint VK_E = 0x45;
    private IntPtr _hook;
    private Native.LowLevelKeyboardProc? _proc;   // keep delegate alive
    private readonly Dispatcher _dispatcher;
    private bool _winDown, _otherKeyWhileWin;

    public bool ReplaceStartMenu { get; set; }
    public bool ReplaceWinE { get; set; }
    public event Action? StartMenuRequested;
    public event Action? ExplorerRequested;
    public bool IsInstalled => _hook != IntPtr.Zero;

    public KeyboardHook(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        using var mod = Process.GetCurrentProcess().MainModule;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(mod?.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Warn("Keyboard hook install failed: " + Marshal.GetLastWin32Error());
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || (!ReplaceStartMenu && !ReplaceWinE)) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & Native.LLKHF_INJECTED) != 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            int msg = (int)wParam;
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
            bool isWin = info.vkCode == Native.VK_LWIN || info.vkCode == Native.VK_RWIN;

            if (isWin)
            {
                if (down)
                {
                    if (!_winDown)
                    {
                        _winDown = true; _otherKeyWhileWin = false;
                        if (ReplaceStartMenu) Native.SendKey(VK_NOOP);   // defeat Windows Start
                    }
                }
                else if (up)
                {
                    bool tap = _winDown && !_otherKeyWhileWin;
                    _winDown = false;
                    if (tap && ReplaceStartMenu)
                        _dispatcher.BeginInvoke(() => StartMenuRequested?.Invoke());
                }
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (_winDown && down)
            {
                _otherKeyWhileWin = true;
                if (ReplaceWinE && info.vkCode == VK_E && !Native.IsKeyDown(Native.VK_SHIFT) && !Native.IsKeyDown(Native.VK_CONTROL))
                {
                    _dispatcher.BeginInvoke(() => ExplorerRequested?.Invoke());
                    return (IntPtr)1;   // swallow so Windows Explorer does not open
                }
            }
            if (_winDown && up && ReplaceWinE && info.vkCode == VK_E) return (IntPtr)1;
        }
        catch { }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
