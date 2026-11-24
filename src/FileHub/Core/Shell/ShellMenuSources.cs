using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using FileHub.Platform;

namespace FileHub.Core.Shell;

/// <summary>
/// Which program put each entry into the Windows right-click menu ("Extract Here" → WinRAR), for the menu editor.
/// Static verbs are read from the registry; handler-made entries come from asking each non-Windows handler
/// (shellex\ContextMenuHandlers) on its own which entries it adds. Anything not found is Windows' own.
/// </summary>
public static class ShellMenuSources
{
    public const string Windows = "Windows";
    private static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Dictionary<string, string> ProgramCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The form labels are compared in: no access-key marks, trimmed, any case.</summary>
    public static string Norm(string label) => label.Replace("&", "").Replace("_", "").Trim().ToLowerInvariant();

    /// <summary>Program per "verb:&lt;verb&gt;" and per normalised label, for the menu of these items (or a folder's empty space).</summary>
    public static Dictionary<string, string> Resolve(IReadOnlyList<string> paths, string? backgroundFolder)
    {
        var map = new Dictionary<string, string>();
        var sw = Stopwatch.StartNew();
        var keys = ClassKeys(paths, backgroundFolder != null);
        foreach (var key in Enumerable.Reverse(keys)) StaticVerbs(key, map);   // a verb on the file type wins over the same verb on all files

        IntPtr dataObj = IntPtr.Zero, pidlFolder = IntPtr.Zero;
        var pidls = new List<IntPtr>();
        object? parent = null;
        try
        {
            if (backgroundFolder != null)
            {
                if (Native.SHParseDisplayName(backgroundFolder, IntPtr.Zero, out pidlFolder, 0, out _) != 0) pidlFolder = IntPtr.Zero;
            }
            else
            {
                var children = new List<IntPtr>();
                foreach (var p in paths)
                {
                    if (Native.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) continue;
                    pidls.Add(pidl);
                    var iid = Native.IID_IShellFolder;
                    if (Native.SHBindToParent(pidl, ref iid, out var ppv, out var child) != 0) continue;
                    if (parent == null) parent = Marshal.GetObjectForIUnknown(ppv);
                    Marshal.Release(ppv);
                    children.Add(child);
                }
                if (parent is Native.IShellFolder sf && children.Count > 0)
                {
                    var iidData = IID_IDataObject;
                    if (sf.GetUIObjectOf(IntPtr.Zero, (uint)children.Count, children.ToArray(), ref iidData, IntPtr.Zero, out var pdo) != 0) pdo = IntPtr.Zero;
                    dataObj = pdo;
                }
            }
            if (dataObj != IntPtr.Zero || pidlFolder != IntPtr.Zero)
            {
                var seen = new HashSet<Guid>();
                foreach (var key in keys) Handlers(key, pidlFolder, dataObj, seen, map);
            }
        }
        catch (Exception ex) { Log.Warn("Menu sources: " + ex.Message); }
        finally
        {
            if (dataObj != IntPtr.Zero) Marshal.Release(dataObj);
            if (parent != null) try { Marshal.ReleaseComObject(parent); } catch { }
            if (pidlFolder != IntPtr.Zero) Native.ILFree(pidlFolder);
            foreach (var p in pidls) Native.ILFree(p);
        }
        Log.Info($"Menu sources: {map.Count} entries from other programs in {sw.ElapsedMilliseconds} ms");
        return map;
    }

    /// <summary>The registry classes whose verbs and handlers make up this menu.</summary>
    private static List<string> ClassKeys(IReadOnlyList<string> paths, bool background)
    {
        if (background) return new() { @"Directory\Background" };
        var keys = new List<string> { "AllFilesystemObjects" };
        if (paths.All(Directory.Exists)) { keys.Add("Directory"); keys.Add("Folder"); return keys; }
        keys.Add("*");
        string ext = Path.GetExtension(paths[0]);
        if (ext.Length == 0 || !paths.All(p => string.Equals(Path.GetExtension(p), ext, StringComparison.OrdinalIgnoreCase))) return keys;
        keys.Add(@"SystemFileAssociations\" + ext);
        using (var ek = Registry.ClassesRoot.OpenSubKey(ext))
        {
            if (ek?.GetValue("") is string progId && progId.Length > 0) keys.Add(progId);
            if (ek?.GetValue("PerceivedType") is string perceived && perceived.Length > 0) keys.Add(@"SystemFileAssociations\" + perceived);
        }
        var sb = new System.Text.StringBuilder(260);
        uint n = 260;
        if (Native.AssocQueryStringW(0, Native.ASSOCSTR_PROGID, ext, null, sb, ref n) == 0 && sb.Length > 0 && !keys.Contains(sb.ToString(), StringComparer.OrdinalIgnoreCase))
            keys.Add(sb.ToString());   // the user's chosen default program
        return keys;
    }

    private static void StaticVerbs(string key, Dictionary<string, string> map)
    {
        try
        {
            using var shell = Registry.ClassesRoot.OpenSubKey(key + @"\shell");
            if (shell == null) return;
            foreach (var verb in shell.GetSubKeyNames())
            {
                using var cmd = shell.OpenSubKey(verb + @"\command");
                if (cmd?.GetValue("") is not string line || ExeOf(line) is not { } exe) continue;
                string prog = ProgramOf(exe);
                if (prog == Windows && Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() is "cmd" or "powershell" or "pwsh" or "wscript" or "cscript" or "mshta" or "reg")
                    prog = "Command (" + Path.GetFileName(exe).ToLowerInvariant() + ")";   // a registry tweak like Take Ownership, or Windows' own console verbs
                map.TryAdd("verb:" + verb.ToLowerInvariant(), prog);
            }
        }
        catch (Exception ex) { Log.Warn($"Menu sources: verbs of {key}: {ex.Message}"); }
    }

    private static void Handlers(string key, IntPtr pidlFolder, IntPtr dataObj, HashSet<Guid> seen, Dictionary<string, string> map)
    {
        try
        {
            using var hk = Registry.ClassesRoot.OpenSubKey(key + @"\shellex\ContextMenuHandlers");
            if (hk == null) return;
            foreach (var name in hk.GetSubKeyNames())
            {
                using var sub = hk.OpenSubKey(name);
                if (!Guid.TryParse(sub?.GetValue("") as string, out var clsid) && !Guid.TryParse(name, out clsid)) continue;
                if (!seen.Add(clsid)) continue;
                using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
                if (server?.GetValue("") is not string dll) continue;
                string prog = ProgramOf(dll);
                if (prog == Windows) continue;   // unmatched labels count as Windows' anyway, and this skips loading them
                foreach (var label in HandlerLabels(clsid, key, pidlFolder, dataObj)) map.TryAdd(Norm(label), prog);
            }
        }
        catch (Exception ex) { Log.Warn($"Menu sources: handlers of {key}: {ex.Message}"); }
    }

    /// <summary>The top-level entries one handler adds on its own.</summary>
    private static List<string> HandlerLabels(Guid clsid, string key, IntPtr pidlFolder, IntPtr dataObj)
    {
        var labels = new List<string>();
        object? o = null;
        IntPtr hMenu = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            o = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true)!);
            if (o is not IShellExtInit init || o is not Native.IContextMenu cm) return labels;
            using var rk = Registry.ClassesRoot.OpenSubKey(key);
            if (init.Initialize(dataObj != IntPtr.Zero ? IntPtr.Zero : pidlFolder, dataObj, rk?.Handle.DangerousGetHandle() ?? IntPtr.Zero) != 0) return labels;
            hMenu = Native.CreatePopupMenu();
            uint flags = Native.CMF_NORMAL | Native.CMF_EXPLORE | Native.CMF_EXTENDEDVERBS | (dataObj != IntPtr.Zero ? Native.CMF_ITEMMENU : 0);
            if (cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, flags) < 0) return labels;
            buf = Marshal.AllocHGlobal(1024 * 2);
            int count = Native.GetMenuItemCount(hMenu);
            for (int i = 0; i < count; i++)
            {
                var mii = new Native.MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<Native.MENUITEMINFO>(), fMask = Native.MIIM_STRING | Native.MIIM_FTYPE, dwTypeData = buf, cch = 1023 };
                if (!Native.GetMenuItemInfoW(hMenu, (uint)i, true, ref mii) || (mii.fType & (Native.MFT_SEPARATOR | Native.MFT_OWNERDRAW)) != 0 || mii.cch == 0) continue;
                labels.Add(Marshal.PtrToStringUni(buf, (int)mii.cch));
            }
        }
        catch (Exception ex) { Log.Warn($"Menu sources: handler {clsid}: {ex.Message}"); }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (hMenu != IntPtr.Zero) Native.DestroyMenu(hMenu);
            if (o != null) try { Marshal.ReleaseComObject(o); } catch { }
        }
        return labels;
    }

    /// <summary>The program file a registry command line starts.</summary>
    private static string? ExeOf(string commandLine)
    {
        string c = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (c.StartsWith('"')) { int q = c.IndexOf('"', 1); return q > 1 ? c[1..q] : null; }
        int e = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (e > 0) return c[..(e + 4)];
        int sp = c.IndexOf(' ');
        return sp > 0 ? c[..sp] : c.Length > 0 ? c : null;
    }

    /// <summary>"WinRAR", "VLC media player", "7-Zip"… from the file's version info; "Windows" for Windows' own files.</summary>
    public static string ProgramOf(string file)
    {
        lock (ProgramCache)
            if (ProgramCache.TryGetValue(file, out var cached)) return cached;
        string path = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        if (!Path.IsPathRooted(path)) path = Path.Combine(Environment.SystemDirectory, path);
        string name;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) name = Windows;
        else
        {
            name = "";
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                name = Clean(v.ProductName);
                if ((v.CompanyName ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) && (name.Length == 0 || name.Contains("Windows", StringComparison.OrdinalIgnoreCase))) name = Windows;
                else if (name.Length == 0) name = Clean(v.FileDescription);
            }
            catch { }
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);
        }
        lock (ProgramCache) ProgramCache[file] = name;
        return name;
    }

    private static string Clean(string? s) => (s ?? "").Replace("®", "").Replace("™", "").Replace("(R)", "").Replace("(TM)", "").Trim();

    [ComImport, Guid("000214E8-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellExtInit
    {
        [PreserveSig] int Initialize(IntPtr pidlFolder, IntPtr pdtobj, IntPtr hkeyProgID);
    }
}
