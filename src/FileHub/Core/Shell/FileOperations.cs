using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using FileHub.Platform;

namespace FileHub.Core.Shell;

/// <summary>Copy / move / delete / rename through the Windows shell (progress UI, undo, recycle bin) plus clipboard helpers.</summary>
public static class FileOperations
{
    private static string Join(IEnumerable<string> paths) => string.Join("\0", paths) + "\0\0";

    public static bool Delete(IntPtr hwnd, IReadOnlyList<string> paths, bool permanent = false)
    {
        if (paths.Count == 0) return false;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = hwnd, wFunc = Native.FO_DELETE, pFrom = Join(paths),
            fFlags = (ushort)(permanent ? 0 : Native.FOF_ALLOWUNDO),
        };
        int r = Native.SHFileOperationW(ref op);
        return r == 0 && !op.fAnyOperationsAborted;
    }

    public static bool Copy(IntPtr hwnd, IReadOnlyList<string> sources, string destinationFolder)
    {
        if (sources.Count == 0) return false;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = hwnd, wFunc = Native.FO_COPY, pFrom = Join(sources), pTo = destinationFolder + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR),
        };
        // copying into the same folder: let the shell auto-rename ("file - Copy")
        if (sources.All(s => string.Equals(Path.GetDirectoryName(s), destinationFolder, StringComparison.OrdinalIgnoreCase)))
            op.fFlags |= Native.FOF_RENAMEONCOLLISION;
        int r = Native.SHFileOperationW(ref op);
        return r == 0 && !op.fAnyOperationsAborted;
    }

    public static bool Move(IntPtr hwnd, IReadOnlyList<string> sources, string destinationFolder)
    {
        if (sources.Count == 0) return false;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = hwnd, wFunc = Native.FO_MOVE, pFrom = Join(sources), pTo = destinationFolder + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR),
        };
        int r = Native.SHFileOperationW(ref op);
        return r == 0 && !op.fAnyOperationsAborted;
    }

    public static bool Rename(IntPtr hwnd, string path, string newName)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir == null || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        string dest = Path.Combine(dir, newName);
        if (string.Equals(dest, path, StringComparison.Ordinal)) return true;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = hwnd, wFunc = Native.FO_RENAME, pFrom = path + "\0\0", pTo = dest + "\0\0", fFlags = Native.FOF_ALLOWUNDO,
        };
        int r = Native.SHFileOperationW(ref op);
        return r == 0 && !op.fAnyOperationsAborted;
    }

    public static string? NewFolder(string parent, string baseName = "New folder")
    {
        string name = baseName; int n = 2;
        string path = Path.Combine(parent, name);
        while (Directory.Exists(path) || File.Exists(path)) { name = $"{baseName} ({n++})"; path = Path.Combine(parent, name); }
        try { Directory.CreateDirectory(path); return path; }
        catch (Exception ex) { Log.Warn("NewFolder failed: " + ex.Message); return null; }
    }

    public static string? NewFile(string parent, string baseName = "New Text Document.txt")
    {
        string stem = Path.GetFileNameWithoutExtension(baseName), ext = Path.GetExtension(baseName);
        string path = Path.Combine(parent, baseName); int n = 2;
        while (File.Exists(path) || Directory.Exists(path)) path = Path.Combine(parent, $"{stem} ({n++}){ext}");
        try { File.WriteAllBytes(path, Array.Empty<byte>()); return path; }
        catch (Exception ex) { Log.Warn("NewFile failed: " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------ clipboard

    public static void CopyToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        var data = new DataObject();
        var coll = new StringCollection();
        foreach (var p in paths) coll.Add(p);
        data.SetFileDropList(coll);
        var effect = new MemoryStream(BitConverter.GetBytes(cut ? (int)DragDropEffects.Move : (int)(DragDropEffects.Copy | DragDropEffects.Link)));
        data.SetData("Preferred DropEffect", effect);
        try { Clipboard.SetDataObject(data, true); } catch (Exception ex) { Log.Warn("Clipboard set failed: " + ex.Message); }
    }

    public static void CopyText(string text)
    {
        try { Clipboard.SetDataObject(text, true); } catch (Exception ex) { Log.Warn("Clipboard text failed: " + ex.Message); }
    }

    public static bool ClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch { return false; }
    }

    /// <summary>Pastes files from the clipboard into the folder (moves when the source was "cut").</summary>
    public static bool Paste(IntPtr hwnd, string destinationFolder)
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return false;
            var list = Clipboard.GetFileDropList().Cast<string>().ToList();
            bool move = false;
            var data = Clipboard.GetDataObject();
            if (data?.GetData("Preferred DropEffect") is MemoryStream ms)
            {
                var b = new byte[4]; ms.Position = 0; if (ms.Read(b, 0, 4) == 4) move = (BitConverter.ToInt32(b, 0) & (int)DragDropEffects.Move) != 0;
            }
            bool ok = move ? Move(hwnd, list, destinationFolder) : Copy(hwnd, list, destinationFolder);
            if (move && ok) Clipboard.Clear();
            return ok;
        }
        catch (Exception ex) { Log.Warn("Paste failed: " + ex.Message); return false; }
    }

    public static bool SameVolume(string a, string b)
        => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
}
