using System.Runtime.InteropServices;
using System.Windows.Controls;
using FileHub.Platform;

namespace FileHub.Core.Shell;

/// <summary>One entry of the Windows shell context menu, translated into a WPF-friendly tree.</summary>
public sealed class ShellMenuEntry
{
    public string Label { get; init; } = "";
    public string? Verb { get; init; }
    public uint CommandId { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsDefault { get; init; }
    public IntPtr SubMenu { get; init; }
    public int Position { get; init; }
    public bool HasSubMenu => SubMenu != IntPtr.Zero;
}

/// <summary>
/// Hosts the real Windows IContextMenu for one or more items (or a folder background) and exposes
/// its items so they can be rendered inside a WPF ContextMenu. Dispose after the menu closes.
/// </summary>
public sealed class ShellContextMenu : IDisposable
{
    private const uint IdFirst = 1, IdLast = 0x7FFF;
    private Native.IContextMenu? _menu;
    private Native.IContextMenu2? _menu2;
    private Native.IContextMenu3? _menu3;
    private IntPtr _hMenu;
    private readonly List<IntPtr> _pidls = new();
    private object? _folderObj;
    private readonly IntPtr _hwnd;
    private bool _disposed;

    public ShellContextMenu(IntPtr ownerHwnd) => _hwnd = ownerHwnd;

    /// <summary>Builds the shell menu for the given file system paths (all must share a parent).</summary>
    public static ShellContextMenu? ForItems(IntPtr hwnd, IReadOnlyList<string> paths, bool extended)
    {
        if (paths.Count == 0) return null;
        var scm = new ShellContextMenu(hwnd);
        try
        {
            IntPtr firstPidl = IntPtr.Zero;
            var childPidls = new List<IntPtr>();
            Native.IShellFolder? parentFolder = null;
            foreach (var p in paths)
            {
                if (Native.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) continue;
                scm._pidls.Add(pidl);
                if (parentFolder == null)
                {
                    var iid = Native.IID_IShellFolder;
                    if (Native.SHBindToParent(pidl, ref iid, out var ppv, out var child) != 0) continue;
                    parentFolder = (Native.IShellFolder)Marshal.GetObjectForIUnknown(ppv);
                    Marshal.Release(ppv);
                    scm._folderObj = parentFolder;
                    firstPidl = pidl;
                    childPidls.Add(child);
                }
                else
                {
                    var iid = Native.IID_IShellFolder;
                    if (Native.SHBindToParent(pidl, ref iid, out var ppv, out var child) != 0) continue;
                    Marshal.Release(ppv);
                    childPidls.Add(child);
                }
            }
            if (parentFolder == null || childPidls.Count == 0) { scm.Dispose(); return null; }
            var iidCm = Native.IID_IContextMenu;
            int hr = parentFolder.GetUIObjectOf(hwnd, (uint)childPidls.Count, childPidls.ToArray(), ref iidCm, IntPtr.Zero, out var pcm);
            if (hr != 0 || pcm == IntPtr.Zero) { scm.Dispose(); return null; }
            scm.Attach(pcm, extended, true);
            return scm;
        }
        catch (Exception ex)
        {
            Log.Warn("Shell context menu failed: " + ex.Message);
            scm.Dispose();
            return null;
        }
    }

    /// <summary>Builds the folder background menu (right-click on empty space).</summary>
    public static ShellContextMenu? ForBackground(IntPtr hwnd, string folderPath, bool extended)
    {
        var scm = new ShellContextMenu(hwnd);
        try
        {
            if (Native.SHParseDisplayName(folderPath, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) { scm.Dispose(); return null; }
            scm._pidls.Add(pidl);
            var iid = Native.IID_IShellFolder;
            if (Native.SHBindToParent(pidl, ref iid, out var ppv, out var child) != 0) { scm.Dispose(); return null; }
            var parent = (Native.IShellFolder)Marshal.GetObjectForIUnknown(ppv);
            Marshal.Release(ppv);
            int hr = parent.BindToObject(child, IntPtr.Zero, ref iid, out var pFolder);
            Marshal.ReleaseComObject(parent);
            if (hr != 0 || pFolder == IntPtr.Zero) { scm.Dispose(); return null; }
            var folder = (Native.IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
            Marshal.Release(pFolder);
            scm._folderObj = folder;
            var iidCm = Native.IID_IContextMenu;
            hr = folder.CreateViewObject(hwnd, ref iidCm, out var pcm);
            if (hr != 0 || pcm == IntPtr.Zero) { scm.Dispose(); return null; }
            scm.Attach(pcm, extended, false);
            return scm;
        }
        catch (Exception ex)
        {
            Log.Warn("Shell background menu failed: " + ex.Message);
            scm.Dispose();
            return null;
        }
    }

    private void Attach(IntPtr pcm, bool extended, bool items)
    {
        _menu = (Native.IContextMenu)Marshal.GetObjectForIUnknown(pcm);
        Marshal.Release(pcm);
        try { _menu2 = _menu as Native.IContextMenu2; } catch { }
        try { _menu3 = _menu as Native.IContextMenu3; } catch { }
        _hMenu = Native.CreatePopupMenu();
        uint flags = Native.CMF_NORMAL | Native.CMF_EXPLORE | (extended ? Native.CMF_EXTENDEDVERBS : 0) | (items ? Native.CMF_ITEMMENU : 0);
        int hr = _menu.QueryContextMenu(_hMenu, 0, IdFirst, IdLast, flags);
        if (hr < 0) throw new COMException("QueryContextMenu failed", hr);
    }

    public IReadOnlyList<ShellMenuEntry> RootEntries() => Enumerate(_hMenu);

    /// <summary>Enumerates a submenu, first letting the shell populate it (WM_INITMENUPOPUP).</summary>
    public IReadOnlyList<ShellMenuEntry> SubEntries(ShellMenuEntry entry)
    {
        if (!entry.HasSubMenu) return Array.Empty<ShellMenuEntry>();
        try
        {
            IntPtr lParam = (IntPtr)(entry.Position & 0xFFFF);
            if (_menu3 != null) _menu3.HandleMenuMsg2(Native.WM_INITMENUPOPUP, entry.SubMenu, lParam, IntPtr.Zero);
            else _menu2?.HandleMenuMsg(Native.WM_INITMENUPOPUP, entry.SubMenu, lParam);
        }
        catch (Exception ex) { Log.Warn("HandleMenuMsg failed: " + ex.Message); }
        return Enumerate(entry.SubMenu);
    }

    private List<ShellMenuEntry> Enumerate(IntPtr hMenu)
    {
        var list = new List<ShellMenuEntry>();
        if (hMenu == IntPtr.Zero) return list;
        int count = Native.GetMenuItemCount(hMenu);
        IntPtr buf = Marshal.AllocHGlobal(1024 * 2);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var mii = new Native.MENUITEMINFO
                {
                    cbSize = (uint)Marshal.SizeOf<Native.MENUITEMINFO>(),
                    fMask = Native.MIIM_STRING | Native.MIIM_ID | Native.MIIM_SUBMENU | Native.MIIM_FTYPE | Native.MIIM_STATE,
                    dwTypeData = buf, cch = 1023,
                };
                if (!Native.GetMenuItemInfoW(hMenu, (uint)i, true, ref mii)) continue;
                if ((mii.fType & Native.MFT_SEPARATOR) != 0) { list.Add(new ShellMenuEntry { IsSeparator = true, Position = i }); continue; }
                string label = mii.cch > 0 && (mii.fType & Native.MFT_OWNERDRAW) == 0 ? Marshal.PtrToStringUni(buf, (int)mii.cch) : string.Empty;
                string? verb = mii.hSubMenu == IntPtr.Zero ? GetVerb(mii.wID) : null;
                if (string.IsNullOrWhiteSpace(label)) label = verb ?? "(unnamed)";
                list.Add(new ShellMenuEntry
                {
                    Label = label,
                    Verb = verb,
                    CommandId = mii.wID,
                    IsEnabled = (mii.fState & Native.MFS_DISABLED) == 0,
                    IsDefault = (mii.fState & Native.MFS_DEFAULT) != 0,
                    SubMenu = mii.hSubMenu,
                    Position = i,
                });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private string? GetVerb(uint id)
    {
        if (_menu == null || id < IdFirst || id > IdLast) return null;
        IntPtr buf = Marshal.AllocHGlobal(512);
        try
        {
            int hr = _menu.GetCommandString((UIntPtr)(id - IdFirst), Native.GCS_VERBW, IntPtr.Zero, buf, 255);
            return hr == 0 ? Marshal.PtrToStringUni(buf) : null;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Invoke(ShellMenuEntry entry)
    {
        if (_menu == null || entry.CommandId < IdFirst) return;
        var info = new Native.CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<Native.CMINVOKECOMMANDINFOEX>(),
            fMask = Native.CMIC_MASK_UNICODE,
            hwnd = _hwnd,
            lpVerb = (IntPtr)(entry.CommandId - IdFirst),
            lpVerbW = (IntPtr)(entry.CommandId - IdFirst),
            nShow = Native.SW_SHOWNORMAL,
        };
        try
        {
            int hr = _menu.InvokeCommand(ref info);
            if (hr != 0) Log.Warn($"InvokeCommand '{entry.Label}' returned 0x{hr:X8}");
        }
        catch (Exception ex) { Log.Warn("InvokeCommand failed: " + ex.Message); }
    }

    /// <summary>Invokes a verb by name (e.g. "properties", "openas") when it exists in the shell menu.</summary>
    public bool InvokeVerb(string verb)
    {
        foreach (var e in RootEntries())
            if (string.Equals(e.Verb, verb, StringComparison.OrdinalIgnoreCase)) { Invoke(e); return true; }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_hMenu != IntPtr.Zero) Native.DestroyMenu(_hMenu); } catch { }
        _hMenu = IntPtr.Zero;
        try { if (_menu != null) Marshal.ReleaseComObject(_menu); } catch { }
        _menu = null; _menu2 = null; _menu3 = null;
        try { if (_folderObj != null) Marshal.ReleaseComObject(_folderObj); } catch { }
        _folderObj = null;
        foreach (var p in _pidls) { try { Native.ILFree(p); } catch { } }
        _pidls.Clear();
    }
}
