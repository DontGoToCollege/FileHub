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
/// (thumbs.db, one 256 px image per file shown at every view size) so thumbnails survive restarts like Windows' thumbcache.
/// </summary>
public sealed class ShellImaging
{
    private readonly ThumbDb _thumbs;
    private readonly Dictionary<string, BitmapSource> _mem = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly object _lock = new();
    private const int MemLimit = 4000;
    private static readonly Dictionary<string, string> _typeNames = new(StringComparer.OrdinalIgnoreCase);
    public bool DiskCacheEnabled { get; set; } = true;

    public ShellImaging(ThumbDb thumbs) => _thumbs = thumbs;

    /// <summary>Cache key for an item: generic per-extension for ordinary files, per-path for folders, executables, shortcuts and thumbnails.</summary>
    public static string KeyFor(string path, bool isDirectory, int size, bool thumbnail)
    {
        // folders in icon views get Windows' content-preview thumbnail (pictures peeking out), like Explorer
        if (isDirectory) return (thumbnail ? "t|" : "d|") + size + "|" + path;
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
        if (isThumbKey && DiskCacheEnabled && _thumbs.Get(path, modifiedTicks) is { } stored)
        {
            // stored at 256 px and scaled to this view's size while decoding; empty = the file has no thumbnail: its icon
            if (stored.Length == 0) isThumbKey = false;
            else if (FromStream(new MemoryStream(stored), size, upright: false) is { } bmp) { Put(key, bmp); return bmp; }
        }

        BitmapSource? result = null;
        if (isThumbKey)
        {
            var big = MakeThumb(path, isDirectory);
            if (big != null) result = Fit(big, size);
            if (DiskCacheEnabled)
            {
                // stored after the image is handed back, so the encode (~3 ms) doesn't delay it showing
                long modified = modifiedTicks;
                Task.Run(() =>
                {
                    try { _thumbs.Put(new[] { (path, modified, big == null ? Array.Empty<byte>() : Encode(big)) }); }
                    catch (Exception ex) { Log.Warn("Thumbnail save failed: " + ex.Message); }
                });
            }
        }
        result ??= FromShell(path, size, Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK);
        result ??= FromShell(path, size, Native.SIIGBF_RESIZETOFIT);
        if (result == null) return null;
        Put(key, result);
        return result;
    }

    /// <summary>
    /// A file's or folder's thumbnail at up to 256 px (what thumbs.db stores), or null when it has none. The shell part
    /// wants an STA thread.
    /// </summary>
    public static BitmapSource? MakeThumb(string path, bool isDirectory)
    {
        BitmapSource? b = null;
        if (!isDirectory && WicExts.Contains(FileTypes.ExtensionOf(path))) b = FromWic(path, ThumbDb.Size);
        b ??= FromShell(path, ThumbDb.Size, Native.SIIGBF_THUMBNAILONLY | Native.SIIGBF_BIGGERSIZEOK);
        return b == null ? null : Fit(b, ThumbDb.Size);
    }

    /// <summary>Scaled down to fit a <paramref name="size"/> square (never up).</summary>
    private static BitmapSource Fit(BitmapSource b, int size)
    {
        int max = Math.Max(b.PixelWidth, b.PixelHeight);
        if (max <= size) return b;
        double s = size / (double)max;
        var t = new TransformedBitmap(b, new ScaleTransform(s, s));
        t.Freeze();
        return t;
    }

    /// <summary>For thumbs.db: JPEG (photos: 14 KB against 88 KB as PNG), or PNG when the image has see-through pixels.</summary>
    public static byte[] Encode(BitmapSource b)
    {
        var bgra = b.Format == PixelFormats.Bgra32 || b.Format == PixelFormats.Pbgra32 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);
        bool clear = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 255) { clear = true; break; }
        BitmapEncoder enc = clear ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(clear ? b : new FormatConvertedBitmap(b, PixelFormats.Bgr24, null, 0)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static readonly HashSet<string> WicExts = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "jpe", "jfif", "png", "bmp", "gif", "tif", "tiff", "webp" };

    /// <summary>
    /// Common picture formats decoded straight with WIC. The shell's thumbnailer does one image at a time (~23 ms each
    /// however many threads ask); WIC runs on every loader thread at once and scales JPEGs while decoding: a folder of
    /// 210 new photos 4.9 s → 0.7 s. Null on any failure, so the shell still handles the odd file.
    /// </summary>
    private static BitmapSource? FromWic(string path, int size)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FromStream(fs, size, upright: true);
        }
        catch { return null; }
    }

    /// <summary>An image decoded to fit a <paramref name="size"/> square (JPEGs scale while decoding), optionally turned upright from its camera tag.</summary>
    private static BitmapSource? FromStream(Stream s, int size, bool upright)
    {
        try
        {
            var frame = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            ushort orientation = 1;
            if (upright) try { if (frame.Metadata is BitmapMetadata m && m.GetQuery("System.Photo.Orientation") is ushort o) orientation = o; } catch { }
            s.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = s;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (w >= h) bi.DecodePixelWidth = Math.Min(size, w); else bi.DecodePixelHeight = Math.Min(size, h);
            // shortcut: mirrored EXIF orientations (2, 4, 5, 7, rare outside scanners) show unmirrored; add a flip if one turns up
            bi.Rotation = orientation switch { 6 => Rotation.Rotate90, 3 => Rotation.Rotate180, 8 => Rotation.Rotate270, _ => Rotation.Rotate0 };
            bi.EndInit();
            bi.Freeze();
            return bi;
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

    public ImageLoader(ShellImaging imaging, int workers = 0)
    {
        _imaging = imaging;
        if (workers <= 0) workers = Math.Clamp(Environment.ProcessorCount / 2, 3, 8);   // picture thumbnails decode in parallel
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
