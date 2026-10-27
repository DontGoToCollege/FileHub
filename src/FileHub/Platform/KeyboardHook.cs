using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FileHub.Platform;

/// <summary>
/// Low-level keyboard hook that lets FileHub take over the Start menu (Win key tap), Win+E and the
/// Hub hotkey with priority over other programs (Windows Start, StartIsBack, apps that registered
/// the same hotkey such as Claude desktop).
///
/// Win key: the physical Win press is swallowed so neither Windows nor any other start-menu tool
/// ever sees a bare Win tap. If another key is pressed while Win is held, FileHub replays
/// "Win down + that key" through SendInput so every Win+X shortcut keeps working normally.
/// Win+L cannot be replayed (secure shortcut) so it calls LockWorkStation directly.
///
/// The hook runs on its own thread with its own message loop, so a busy UI thread can never delay
/// keyboard input. It re-installs itself periodically so it stays first in the hook chain even if
/// another tool (e.g. StartIsBack after an Explorer restart) installs its own hook later.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const ushort VK_NOOP = 0xE8;           // unassigned VK; breaks "bare modifier tap" detection
    private const uint VK_E = 0x45, VK_L = 0x4C;
    private const uint ReinstallMs = 30_000;

    private readonly Dispatcher _dispatcher;
    private Native.LowLevelKeyboardProc? _proc;    // keep delegate alive
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;

    // settings (written by UI thread, read by hook thread)
    private volatile bool _replaceStart, _replaceWinE;
    private volatile uint _hubMods, _hubVk;

    // key state (hook thread only)
    private bool _winDown;          // physical Win is held
    private bool _winSwallowed;     // the system has not seen this Win press
    private bool _winInjected;      // we replayed a Win down for a chord
    private bool _chord;            // another key was pressed while Win was held
    private ushort _winVk, _winScan;
    private uint _lastWinTime;
    private uint _swallowUpVk;      // key-up to swallow because we consumed its key-down

    public bool ReplaceStartMenu { get => _replaceStart; set => _replaceStart = value; }
    public bool ReplaceWinE { get => _replaceWinE; set => _replaceWinE = value; }
    public event Action? StartMenuRequested;
    public event Action? ExplorerRequested;
    public event Action? HubRequested;
    public bool IsInstalled => _hook != IntPtr.Zero;

    public KeyboardHook(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Hotkey captured by the hook (RegisterHotKey-style modifiers). vk 0 disables it.</summary>
    public void SetHubHotkey(uint mods, uint vk) { _hubMods = mods; _hubVk = vk; }

    public void Install()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "FileHub.KeyboardHook", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait(3000);
    }

    public void Uninstall()
    {
        var t = _thread;
        if (t == null) return;
        Native.PostThreadMessageW(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(2000);
        _thread = null;
    }

    private void HookThread(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn("Keyboard hook install failed: " + Marshal.GetLastWin32Error());
        Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, ReinstallMs, IntPtr.Zero);
        ready.Set();
        try
        {
            while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == Native.WM_TIMER && msg.hwnd == IntPtr.Zero) { Reinstall(); continue; }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>Installs a fresh hook (which goes to the front of the chain) before removing the old one, so there is no gap.</summary>
    private void Reinstall()
    {
        var fresh = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc!, Native.GetModuleHandleW(null), 0);
        if (fresh == IntPtr.Zero) return;
        var old = _hook;
        _hook = fresh;
        if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
    }

    private void Raise(Action? handler)
    {
        if (handler != null) _dispatcher.BeginInvoke(handler);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & Native.LLKHF_INJECTED) != 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            int msg = (int)wParam;
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
            uint vk = info.vkCode;
            bool isWin = vk == Native.VK_LWIN || vk == Native.VK_RWIN;

            // ---------------------------------------------------------- Win key itself
            if (isWin)
            {
                if (down)
                {
                    // new press (or a lost key-up: no auto-repeat for 1.5 s means it is a fresh press)
                    if (!_winDown || info.time - _lastWinTime > 1500)
                    {
                        _winDown = true; _chord = false; _winInjected = false;
                        _winSwallowed = _replaceStart;
                        _winVk = (ushort)vk; _winScan = (ushort)info.scanCode;
                    }
                    _lastWinTime = info.time;
                    if (_winSwallowed && !_winInjected) return (IntPtr)1;
                    return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                }
                if (up)
                {
                    bool wasDown = _winDown;
                    _winDown = false;
                    if (_winSwallowed)
                    {
                        _winSwallowed = false;
                        if (_winInjected) { _winInjected = false; return Native.CallNextHookEx(_hook, nCode, wParam, lParam); }
                        if (wasDown && !_chord) Raise(StartMenuRequested);
                        return (IntPtr)1;   // the system never saw the press, so it must not see the release
                    }
                }
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // ---------------------------------------------------------- key-up of a key we consumed
            if (up && _swallowUpVk != 0 && vk == _swallowUpVk)
            {
                _swallowUpVk = 0;
                return (IntPtr)1;
            }

            if (down)
            {
                bool winHeld = _winDown || Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN);

                // ------------------------------------------------------ Hub hotkey (beats RegisterHotKey owners)
                uint hubVk = _hubVk;
                if (hubVk != 0 && vk == hubVk && ModifiersMatch(_hubMods, winHeld))
                {
                    if (_winDown) _chord = true;
                    _swallowUpVk = vk;
                    Native.SendKey(VK_NOOP);
                    Raise(HubRequested);
                    return (IntPtr)1;
                }

                if (_winDown)
                {
                    _chord = true;

                    // Win+E -> FileHub Explorer
                    if (_replaceWinE && vk == VK_E && !Native.IsKeyDown(Native.VK_SHIFT) && !Native.IsKeyDown(Native.VK_CONTROL))
                    {
                        _swallowUpVk = vk;
                        if (!_winSwallowed) Native.SendKey(VK_NOOP);   // Win reached the system: stop Start from opening on release
                        Raise(ExplorerRequested);
                        return (IntPtr)1;
                    }

                    // Win was held back: replay Win + this key so the shortcut still works
                    if (_winSwallowed && !_winInjected)
                    {
                        if (vk == VK_L)
                        {
                            _swallowUpVk = vk;
                            Native.LockWorkStation();
                            return (IntPtr)1;
                        }
                        _winInjected = true;
                        Native.SendKeysDown((_winVk, _winScan, true), ((ushort)vk, (ushort)info.scanCode, (info.flags & Native.LLKHF_EXTENDED) != 0));
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch { }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool ModifiersMatch(uint mods, bool winHeld)
    {
        bool ctrl = Native.IsKeyDown(Native.VK_CONTROL), alt = Native.IsKeyDown(Native.VK_MENU), shift = Native.IsKeyDown(Native.VK_SHIFT);
        return ctrl == ((mods & Native.MOD_CONTROL) != 0)
            && alt == ((mods & Native.MOD_ALT) != 0)
            && shift == ((mods & Native.MOD_SHIFT) != 0)
            && winHeld == ((mods & Native.MOD_WIN) != 0);
    }

    public void Dispose() => Uninstall();
}
