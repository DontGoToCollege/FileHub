using System.Runtime.InteropServices;

namespace FileHub.Platform;

/// <summary>Win32 / shell interop used across the app. Everything here is x64 Windows only.</summary>
internal static class Native
{
    // ---------------------------------------------------------------- structs

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx; public int cy; public SIZE(int w, int h) { cx = w; cy = h; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize; public int fMask; public IntPtr hwnd; public IntPtr lpVerb; public IntPtr lpParameters; public IntPtr lpDirectory;
        public int nShow; public int dwHotKey; public IntPtr hIcon; public IntPtr lpTitle; public IntPtr lpVerbW; public IntPtr lpParametersW;
        public IntPtr lpDirectoryW; public IntPtr lpTitleW; public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public string? lpVerb; public string? lpFile; public string? lpParameters; public string? lpDirectory;
        public int nShow; public IntPtr hInstApp; public IntPtr lpIDList; public string? lpClass; public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon; public IntPtr hProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY { public Guid fmtid; public uint pid; public PROPERTYKEY(Guid g, uint p) { fmtid = g; pid = p; } }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public long l;
        [FieldOffset(8)] public int i;
        [FieldOffset(8)] public ulong ul;
        [FieldOffset(8)] public double d;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    // ---------------------------------------------------------------- constants

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_HOTKEY = 0x0312, WM_INITMENUPOPUP = 0x0117;
    public const uint LLKHF_INJECTED = 0x10, LLKHF_EXTENDED = 0x01;
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint WM_TIMER = 0x0113, WM_QUIT = 0x0012;
    public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B;

    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public const uint MIIM_STATE = 1, MIIM_ID = 2, MIIM_SUBMENU = 4, MIIM_STRING = 0x40, MIIM_FTYPE = 0x100;
    public const uint MFT_SEPARATOR = 0x800, MFT_OWNERDRAW = 0x100, MFS_DISABLED = 3, MFS_CHECKED = 8, MFS_DEFAULT = 0x1000;

    public const uint CMF_NORMAL = 0, CMF_EXPLORE = 4, CMF_EXTENDEDVERBS = 0x100, CMF_ITEMMENU = 0x80, CMF_CANRENAME = 0x10;
    public const int CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000, CMIC_MASK_FLAG_NO_UI = 0x400;
    public const uint GCS_VERBW = 4;
    public const int SW_SHOWNORMAL = 1;

    public const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3, FO_RENAME = 4;
    public const ushort FOF_MULTIDESTFILES = 1, FOF_SILENT = 4, FOF_RENAMEONCOLLISION = 8, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200, FOF_NOERRORUI = 0x400;

    public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C, SEE_MASK_NOASYNC = 0x100, SEE_MASK_FLAG_NO_UI = 0x400;

    public const uint SHGFI_ICON = 0x100, SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_DISPLAYNAME = 0x200;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;

    public const int SIIGBF_RESIZETOFIT = 0, SIIGBF_BIGGERSIZEOK = 1, SIIGBF_MEMORYONLY = 2, SIIGBF_ICONONLY = 4, SIIGBF_THUMBNAILONLY = 8, SIIGBF_INCACHEONLY = 0x10, SIIGBF_ICONBACKGROUND = 0x80;

    public const int GPS_DEFAULT = 0, GPS_BESTEFFORT = 0x40, GPS_FASTPROPERTIESONLY = 0x80, GPS_OPENSLOWITEM = 0x10;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    public const uint DIB_RGB_COLORS = 0, BI_RGB = 0;

    public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    // ---------------------------------------------------------------- delegates

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ---------------------------------------------------------------- user32

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetMenuItemInfoW(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFO lpmii);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    // ---------------------------------------------------------------- kernel32

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    // ---------------------------------------------------------------- powrprof

    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // ---------------------------------------------------------------- gdi32

    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, IntPtr lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    // ---------------------------------------------------------------- dwmapi

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ---------------------------------------------------------------- ole32 / propsys

    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr pv);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pvar);
    [DllImport("propsys.dll")] public static extern int PropVariantToStringAlloc(ref PROPVARIANT propvar, out IntPtr ppszOut);

    // ---------------------------------------------------------------- shell32

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport("shell32.dll")] public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHGetPropertyStoreFromParsingName(string pszPath, IntPtr pbc, int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
    [DllImport("shell32.dll")] public static extern void ILFree(IntPtr pidl);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);
    [DllImport("shell32.dll")] public static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[]? apidl, uint dwFlags);

    // ---------------------------------------------------------------- COM interfaces

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, uint cch);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, uint cch);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, uint cch);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr plResult);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        [PreserveSig] int Commit();
    }

    // ---------------------------------------------------------------- helpers

    public static void SendKey(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD; inputs[0].u.ki.wVk = vk;
        inputs[1].type = INPUT_KEYBOARD; inputs[1].u.ki.wVk = vk; inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Injects key-down events for the given keys in order (used to replay a Win+key chord the hook held back).</summary>
    public static void SendKeysDown(params (ushort vk, ushort scan, bool extended)[] keys)
    {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i].type = INPUT_KEYBOARD;
            inputs[i].u.ki.wVk = keys[i].vk;
            inputs[i].u.ki.wScan = keys[i].scan;
            inputs[i].u.ki.dwFlags = keys[i].extended ? KEYEVENTF_EXTENDEDKEY : 0;
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static string? GetKnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); } finally { CoTaskMemFree(p); }
    }

    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)); } catch { }
    }

    public static void SetRoundedCorners(IntPtr hwnd)
    {
        int v = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int)); } catch { }
    }
}
