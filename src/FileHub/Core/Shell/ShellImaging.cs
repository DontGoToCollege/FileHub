using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileHub.Core.Database;
using FileHub.Core.Search;
using FileHub.Platform;

namespace FileHub.Core.Shell;

/// <summary>
/// Icons and thumbnails via IShellItemImageFactory, with an in-memory LRU and a persistent PNG cache
/// (thumbs table in meta.db) so thumbnails survive restarts like Windows' thumbcache.
/// </summary>
public sealed class ShellImaging
{
    private readonly MetaDb _db;
    private readonly Dictionary<string, BitmapSource> _mem = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly object _lock = new();
    private const int MemLimit = 4000;
    private static readonly Dictionary<string, string> _typeNames = new(StringComparer.OrdinalIgnoreCase);
    public bool DiskCacheEnabled { get; set; } = true;

    public ShellImaging(MetaDb db) => _db = db;

    /// <summary>Cache key for an item: generic per-extension for ordinary files, per-path for folders, executables, shortcuts and thumbnails.</summary>
    public static string KeyFor(string path, bool isDirectory, int size, bool thumbnail)
    {
        if (isDirectory) return "d|" + size + "|" + path;
        string ext = FileTypes.ExtensionOf(path);
        if (thumbnail && FileTypes.HasThumbnail(ext)) return "t|" + size + "|" + path;
        if (ext.Equals("exe", StringComparison.OrdinalIgnoreCase) || ext.Equals("lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals("ico", StringComparison.OrdinalIgnoreCase)
            || ext.Equals("url", StringComparison.OrdinalIgnoreCase) || ext.Equals("cur", StringComparison.OrdinalIgnoreCase) || ext.Equals("dll", StringComparison.OrdinalIgnoreCase))
            return "p|" + size + "|" + path;
        return "e|" + size + "|" + ext;
    }

    public BitmapSource? TryGetCached(string key)
    {
        lock (_lock)
        {
            if (_mem.TryGetValue(key, out var bmp)) { _lru.Remove(key); _lru.AddFirst(key); return bmp; }
        }
        return null;
    }

    private void Put(string key, BitmapSource bmp)
    {
        lock (_lock)
        {
            if (_mem.ContainsKey(key)) { _lru.Remove(key); }
            _mem[key] = bmp; _lru.AddFirst(key);
            while (_lru.Count > MemLimit) { var last = _lru.Last!.Value; _lru.RemoveLast(); _mem.Remove(last); }
        }
    }

    /// <summary>Blocking: gets an icon or thumbnail. Call from a background thread.</summary>
    public BitmapSource? GetImage(string path, bool isDirectory, int size, bool thumbnail, long modifiedTicks)
    {
        string key = KeyFor(path, isDirectory, size, thumbnail);
        var cached = TryGetCached(key);
        if (cached != null) return cached;

        bool isThumbKey = key[0] == 't';
        if (isThumbKey && DiskCacheEnabled)
        {
            try
            {
                var png = _db.GetThumb(key, modifiedTicks);
                if (png != null)
                {
                    var bmp = Decode(png);
                    if (bmp != null) { Put(key, bmp); return bmp; }
                }
            }
            catch (Exception ex) { Log.Warn("Thumb cache read failed: " + ex.Message); }
        }

        BitmapSource? result = null;
        if (isThumbKey) result = FromShell(path, size, Native.SIIGBF_THUMBNAILONLY | Native.SIIGBF_BIGGERSIZEOK);
        result ??= FromShell(path, size, Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK);
        result ??= FromShell(path, size, Native.SIIGBF_RESIZETOFIT);
        if (result == null) return null;

        Put(key, result);
        if (isThumbKey && DiskCacheEnabled)
        {
            try
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(result));
                using var ms = new MemoryStream();
                enc.Save(ms);
                _db.PutThumb(key, modifiedTicks, ms.ToArray());
            }
            catch (Exception ex) { Log.Warn("Thumb cache write failed: " + ex.Message); }
        }
        return result;
    }

    private static BitmapSource? Decode(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var f = dec.Frames[0];
            f.Freeze();
            return f;
        }
        catch { return null; }
    }

    public static BitmapSource? FromShell(string path, int size, int flags)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = Native.IID_IShellItemImageFactory;
            int hr = Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            if (hr != 0 || factory == null) return null;
            try
            {
                hr = factory.GetImage(new Native.SIZE(size, size), flags, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero) return null;
                return HBitmapToSource(hbmp);
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception ex) { Log.Warn("Shell image failed for " + path + ": " + ex.Message); return null; }
        finally { if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp); }
    }

    /// <summary>Converts a 32bpp HBITMAP (premultiplied BGRA) to a frozen BitmapSource keeping alpha.</summary>
    private static BitmapSource? HBitmapToSource(IntPtr hbmp)
    {
        if (Native.GetObject(hbmp, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = Native.BI_RGB,
        };
        int stride = w * 4;
        var buffer = Marshal.AllocHGlobal(stride * h);
        IntPtr hdc = Native.GetDC(IntPtr.Zero);
        try
        {
            int lines = Native.GetDIBits(hdc, hbmp, 0, (uint)h, buffer, ref bi, Native.DIB_RGB_COLORS);
            if (lines == 0) return null;
            // if the source had no alpha channel at all (all zero), treat as opaque
            bool anyAlpha = false;
            unsafe
            {
                byte* p = (byte*)buffer;
                for (int i = 3; i < stride * h; i += 4) if (p[i] != 0) { anyAlpha = true; break; }
                if (!anyAlpha) for (int i = 3; i < stride * h; i += 4) p[i] = 255;
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, stride * h, stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, hdc);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Friendly type name ("Text Document") from the shell, cached per extension.</summary>
    public static string GetTypeName(string nameOrPath, bool isDirectory)
    {
        if (isDirectory) return "File folder";
        string ext = FileTypes.ExtensionOf(nameOrPath);
        lock (_typeNames)
        {
            if (_typeNames.TryGetValue(ext, out var cached)) return cached;
        }
        string result;
        try
        {
            var info = new Native.SHFILEINFO();
            var r = Native.SHGetFileInfoW("x." + ext, Native.FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
            result = r != IntPtr.Zero && !string.IsNullOrWhiteSpace(info.szTypeName) ? info.szTypeName : (ext.Length > 0 ? ext.ToUpperInvariant() + " File" : "File");
        }
        catch { result = ext.Length > 0 ? ext.ToUpperInvariant() + " File" : "File"; }
        lock (_typeNames) _typeNames[ext] = result;
        return result;
    }

    public void ClearMemory()
    {
        lock (_lock) { _mem.Clear(); _lru.Clear(); }
    }
}

/// <summary>
/// Background icon/thumbnail loader. LIFO so the most recently requested (= currently visible) items
/// load first. Completed images are delivered on the UI dispatcher.
/// </summary>
public sealed class ImageLoader : IDisposable
{
    private readonly ShellImaging _imaging;
    private readonly Stack<LoadRequest> _stack = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = new();

    private sealed record LoadRequest(string Key, string Path, bool IsDirectory, int Size, bool Thumbnail, long Modified, Action<BitmapSource?> Callback);

    public ImageLoader(ShellImaging imaging, int workers = 3)
    {
        _imaging = imaging;
        for (int i = 0; i < workers; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "FileHub.ImageLoader" + i, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _threads.Add(t);
        }
    }

    /// <summary>Returns a cached image immediately, otherwise queues a load and returns null.</summary>
    public BitmapSource? Request(string path, bool isDirectory, int size, bool thumbnail, long modified, Action<BitmapSource?> callback)
    {
        string key = ShellImaging.KeyFor(path, isDirectory, size, thumbnail);
        var cached = _imaging.TryGetCached(key);
        if (cached != null) return cached;
        lock (_lock)
        {
            if (_pending.Add(key + "|" + path))
            {
                _stack.Push(new LoadRequest(key, path, isDirectory, size, thumbnail, modified, callback));
                if (_stack.Count > 2000)
                {
                    // drop oldest requests (they scrolled out of view long ago)
                    var keep = _stack.Take(1000).ToArray();
                    _stack.Clear();
                    _pending.Clear();
                    foreach (var r in Enumerable.Reverse(keep)) { _stack.Push(r); _pending.Add(r.Key + "|" + r.Path); }
                }
                _signal.Release();
            }
        }
        return null;
    }

    private void Worker()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { _signal.Wait(ct); } catch (OperationCanceledException) { return; }
            LoadRequest? req = null;
            lock (_lock)
            {
                if (_stack.Count > 0) { req = _stack.Pop(); _pending.Remove(req.Key + "|" + req.Path); }
            }
            if (req == null) continue;
            BitmapSource? img = null;
            try { img = _imaging.GetImage(req.Path, req.IsDirectory, req.Size, req.Thumbnail, req.Modified); }
            catch (Exception ex) { Log.Warn("Image load failed: " + ex.Message); }
            var cb = req.Callback;
            try { Application.Current?.Dispatcher.BeginInvoke(() => cb(img), System.Windows.Threading.DispatcherPriority.Background); } catch { }
        }
    }

    public void Dispose() => _cts.Cancel();
}
