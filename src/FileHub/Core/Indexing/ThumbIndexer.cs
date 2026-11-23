using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using FileHub.Core.Database;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.Platform;

namespace FileHub.Core.Indexing;

/// <summary>
/// Makes thumbnails ahead of time, in the background, so icon views show them at once instead of when a folder is first
/// opened. Covers pictures, videos and PDFs everywhere except Windows, program and code-library folders (their images
/// are made when such a folder is opened, then kept). A pass runs at start and every 30 minutes, alongside the content
/// indexer (on a hard drive only once that is done: both read every file), in the same drive order: NVMe, SSD, removable,
/// hard drive, A to Z, a hard drive one file at a time. Files saved or changed are done right away.
/// </summary>
public sealed class ThumbIndexer : IDisposable
{
    private readonly IndexService _index;
    private readonly ThumbDb _db;
    private readonly AppSettings _settings;
    private readonly ContentIndexer _content;
    private readonly Channel<string> _changed = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private volatile bool _paused, _fullPassRequested;

    public bool IsRunning { get; private set; }
    public int Candidates { get; private set; }
    public int Processed { get; private set; }
    public string StatusText { get; private set; } = "Thumbnails idle";
    public event Action? StatusChanged;
    /// <summary>True while the next batch is on a hard drive that waits for content indexing.</summary>
    public bool WaitingForContent { get; private set; }
    private (char drive, int total, int start, int end)[] _driveRanges = [];

    /// <summary>Per drive, from the latest pass: pictures, videos and PDFs to cover (done or not) and how many are left.</summary>
    public IReadOnlyList<(char drive, int total, int left)> DriveProgress() => ContentIndexer.Progress(_driveRanges, Processed);

    public ThumbIndexer(IndexService index, ThumbDb db, AppSettings settings, ContentIndexer content)
    {
        _index = index; _db = db; _settings = settings; _content = content;
        _index.IndexChanged += () => _fullPassRequested = true;
        _index.FileChanged += (path, isDir) => { if (!isDir) _changed.Writer.TryWrite(path); };
    }

    public bool Paused { get => _paused; set { _paused = value; Status(value ? "Thumbnails paused" : IsRunning ? "Thumbnails resuming…" : "Thumbnails idle"); if (!value) _fullPassRequested = true; } }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FileHub.ThumbIndexer", Priority = ThreadPriority.Lowest };
        _thread.Start();
    }

    private void Status(string s) { StatusText = s; StatusChanged?.Invoke(); }

    /// <summary>A thumbnail is made ahead of time for this file: a picture, video or PDF outside Windows and program folders.</summary>
    private static bool Wanted(string path) => FileTypes.HasThumbnail(FileTypes.ExtensionOf(path)) && SearchEngine.PlaceTier(path) < 2;

    private void Loop()
    {
        var ct = _cts.Token;
        var lastPass = DateTime.UtcNow.AddMinutes(-29.5);   // first pass ~30 s after start: the content pass goes first
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_paused || !_settings.Index.ThumbnailCaching) { Thread.Sleep(2000); continue; }
                if (DrainChanged(ct)) continue;
                if ((_fullPassRequested || (DateTime.UtcNow - lastPass).TotalMinutes > 30) && _index.State == IndexState.Ready)
                {
                    _fullPassRequested = false;
                    try { FullPass(ct); } finally { IsRunning = false; }
                    lastPass = DateTime.UtcNow;
                    _db.Maintain();
                    continue;
                }
                Thread.Sleep(1000);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Error("Thumbnail indexer error", ex); Thread.Sleep(5000); }
        }
    }

    /// <summary>Files saved, changed or deleted since: up to 100 per call; false when nothing was queued.</summary>
    private bool DrainChanged(CancellationToken ct)
    {
        var paths = new List<string>();
        while (paths.Count < 100 && _changed.Reader.TryRead(out var p)) paths.Add(p);
        if (paths.Count == 0) return false;
        var todo = new List<(string path, long modified)>();
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(p)) { if (!Directory.Exists(p)) _db.Remove(p); continue; }   // gone: drop it (a folder: all inside)
            if (Wanted(p)) todo.Add((p, File.GetLastWriteTimeUtc(p).Ticks));
        }
        _db.Put(Make(todo, ct));
        return true;
    }

    private void FullPass(CancellationToken ct)
    {
        IsRunning = true;
        Status("Scanning for thumbnails to make…");
        var index = _index.Index;
        var have = _db.LoadStamps();
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // every picture/video/PDF still there, for pruning
        var todo = new List<(string path, long modified, int drive)>();
        var totals = new Dictionary<char, int>();   // per drive: files to cover, done or not
        var entries = index.Entries; int count = index.Count;
        for (int i = 0; i < count; i++)
        {
            ref var e = ref entries[i];
            if (e.Name is null || e.IsDirectory || !FileTypes.HasThumbnail(FileTypes.ExtensionOf(e.Name))) continue;
            string path;
            try { path = index.PathOf(i); } catch { continue; }
            exists.Add(path);
            if (SearchEngine.PlaceTier(path) == 2) continue;
            char letter = ContentIndexer.DriveLetter(path);
            totals[letter] = totals.GetValueOrDefault(letter) + 1;
            if (have.Contains(path + "|" + e.Modified)) continue;
            todo.Add((path, e.Modified, ContentIndexer.DriveOf(path).rank * 32 + (path.Length > 1 && path[1] == ':' ? char.ToUpperInvariant(path[0]) - 'A' : 31)));
        }
        // folders keep their thumbnail while they exist; files only while they're in the index
        int pruned = _db.Prune(p => exists.Contains(p) || index.FindDirectory(p) >= 0);
        if (pruned > 0) Log.Info($"Thumbnails: dropped {pruned:N0} of files that are gone");
        todo.Sort((a, b) => a.drive != b.drive ? a.drive.CompareTo(b.drive) : string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase));
        Processed = 0;
        _driveRanges = ContentIndexer.DriveRanges(totals, todo.Count, k => todo[k].path);
        Candidates = todo.Count;
        if (todo.Count > 0)
            Log.Info($"Thumbnails: making {todo.Count:N0} ({string.Join(", ", todo.GroupBy(t => Where(t.path)).Select(g => $"{g.Key} {g.Count():N0}"))})");
        string hardDrives = string.Join(", ", todo.Where(t => ContentIndexer.DriveOf(t.path).oneAtATime).Select(t => Letter(t.path)).Distinct());
        const int chunk = 200;
        for (int from = 0; from < todo.Count; from += chunk)
        {
            // runs alongside content indexing, except on a hard drive: two passes reading one at once would send its head
            // back and forth and slow both, so its thumbnails wait for the content pass. A pause or a new pass stops this
            // one (done thumbnails are kept)
            var files = todo.GetRange(from, Math.Min(chunk, todo.Count - from));
            while (_content.IsRunning && files.Any(f => ContentIndexer.DriveOf(f.path).oneAtATime) && !_paused && !_fullPassRequested && !ct.IsCancellationRequested)
            { WaitingForContent = true; Status($"Thumbnails {Processed:N0} / {Candidates:N0}: hard drives {hardDrives} wait for content indexing ({_content.Processed:N0} / {_content.Candidates:N0})"); Thread.Sleep(2000); }
            WaitingForContent = false;
            if (_paused || _fullPassRequested) break;
            ct.ThrowIfCancellationRequested();
            DrainChanged(ct);
            Status($"Thumbnails {Processed:N0} / {Candidates:N0} ({Where(files[0].path)})");
            _db.Put(Make(files.Select(t => (t.path, t.modified)).ToList(), ct));
            Processed = Math.Min(todo.Count, from + chunk);
        }
        if (_fullPassRequested && !_paused) return;   // cut short for a new pass, which sets its own status: not up to date yet
        Status(_paused ? $"Thumbnails paused at {Processed:N0} / {Candidates:N0}" : $"Thumbnails up to date ({_db.Count():N0})");
    }

    private static string Letter(string path) => path.Length > 1 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + ":" : "network";

    /// <summary>Drive and kind for status lines: "G: NVMe", "E: hard drive".</summary>
    private static string Where(string path) => Letter(path) is "network" ? "network" : Letter(path) + " " + ContentIndexer.KindName(path);

    /// <summary>
    /// Makes the thumbnails on STA threads (the shell's thumbnailers want one): SSD files on several at once, each hard
    /// drive's files one at a time in the order given. A file with no thumbnail is stored empty, so it isn't tried again.
    /// </summary>
    private static List<(string path, long modified, byte[] img)> Make(List<(string path, long modified)> files, CancellationToken ct)
    {
        var done = new ConcurrentBag<(string, long, byte[])>();
        void One((string path, long modified) f)
        {
            try { var b = ShellImaging.MakeThumb(f.path, false); done.Add((f.path, f.modified, b == null ? Array.Empty<byte>() : ShellImaging.Encode(b))); }
            catch (Exception ex) { Log.Warn("Thumbnail failed for " + f.path + ": " + ex.Message); }
        }
        var threads = new List<Thread>();
        void Lane(Func<(string, long)?> next)
        {
            var t = new Thread(() => { while (!ct.IsCancellationRequested && next() is { } f) One(f); }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            threads.Add(t);
        }
        var fast = new ConcurrentQueue<(string, long)>(files.Where(f => !ContentIndexer.DriveOf(f.path).oneAtATime));
        for (int i = 0, n = Math.Min(fast.Count, Math.Clamp(Environment.ProcessorCount / 2, 2, 8)); i < n; i++)
            Lane(() => fast.TryDequeue(out var f) ? f : null);
        foreach (var drive in files.Where(f => ContentIndexer.DriveOf(f.path).oneAtATime).GroupBy(f => char.ToUpperInvariant(f.path[0])))
        {
            var q = new Queue<(string, long)>(drive);
            Lane(() => q.Count > 0 ? q.Dequeue() : null);
        }
        foreach (var t in threads) t.Join();
        ct.ThrowIfCancellationRequested();
        return done.ToList();
    }

    public void Dispose() => _cts.Cancel();
}
