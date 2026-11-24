using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml;
using FileHub.Core.Database;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.Platform;
using ToRead = (int id, string path, string ext, long modified, long size, bool content, bool meta, int drive);
using ContentRow = (string path, long modified, long size, string body);
using PropsRow = (string path, long modified, string json, string text);

namespace FileHub.Core.Indexing;

/// <summary>
/// Background full-text and metadata indexer. Walks the name index, extracts text from files with
/// whitelisted extensions (plain text, code, Office XML) and shell properties from media/documents,
/// and stores them in SQLite FTS5 tables. Re-indexes individual files when the watcher reports changes.
/// </summary>
public sealed class ContentIndexer : IDisposable
{
    private readonly IndexService _index;
    private readonly MetaDb _db;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _changed = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Thread? _thread;
    private volatile bool _paused;

    public string StatusText { get; private set; } = "Content indexing idle";
    public long Processed { get; private set; }
    public long Candidates { get; private set; }
    public bool IsRunning { get; private set; }
    public event Action? StatusChanged;
    private (char drive, int total, int start, int end)[] _driveRanges = [];

    /// <summary>Per drive, from the latest pass: files to read (done or not) and files still left. Empty before the first pass.</summary>
    public IReadOnlyList<(char drive, int total, int left)> DriveProgress() => Progress(_driveRanges, Processed);

    /// <summary>A drive's letter, or '\' for a network path.</summary>
    internal static char DriveLetter(string path) => path.Length > 1 && path[1] == ':' ? char.ToUpperInvariant(path[0]) : '\\';

    /// <summary>"NVMe", "SSD", "removable" or "hard drive" (also a network path or a drive Windows can't describe).</summary>
    internal static string KindName(string path) => DriveOf(path).rank switch { 0 => "NVMe", 1 => "SSD", 2 => "removable", _ => "hard drive" };

    /// <summary>Per drive: files wanted, and the first and last place of its files in a pass's drive-sorted to-do list (-1: none left).</summary>
    internal static (char drive, int total, int start, int end)[] DriveRanges(Dictionary<char, int> totals, int count, Func<int, string> pathAt)
    {
        var r = totals.ToDictionary(kv => kv.Key, kv => (total: kv.Value, start: -1, end: -1));
        for (int k = 0; k < count; k++)
        {
            char d = DriveLetter(pathAt(k));
            if (r.TryGetValue(d, out var x)) r[d] = (x.total, x.start < 0 ? k : x.start, k);
        }
        // in the pass's order (drives with nothing left last), so the first one with files left is the one being worked on
        return r.OrderBy(kv => kv.Value.start < 0 ? int.MaxValue : kv.Value.start).Select(kv => (kv.Key, kv.Value.total, kv.Value.start, kv.Value.end)).ToArray();
    }

    /// <summary>Files left per drive once a pass has finished the first <paramref name="processed"/> places of its list.</summary>
    internal static IReadOnlyList<(char drive, int total, int left)> Progress((char drive, int total, int start, int end)[] ranges, long processed) =>
        ranges.Select(r => (r.drive, r.total, r.start < 0 ? 0 : (int)Math.Max(0, r.end + 1 - Math.Max(processed, r.start)))).ToList();

    public ContentIndexer(IndexService index, MetaDb db, AppSettings settings)
    {
        _index = index; _db = db; _settings = settings;
        _index.IndexChanged += () => RequestFullPass();
        // folders too: a folder created, moved or renamed brings files whose contents aren't indexed under that path yet
        _index.FileChanged += (path, isDir) => _changed.Writer.TryWrite(path);
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            // a pass still finishing the chunk it was reading (minutes on a hard drive) is not idle
            Status(value ? "Content indexing paused" : IsRunning ? "Content indexing resuming…" : "Content indexing idle");
            if (!value) RequestFullPass();   // pick up where the interrupted pass left off (already-indexed files are skipped)
        }
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FileHub.ContentIndexer", Priority = ThreadPriority.Lowest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private volatile bool _fullPassRequested;
    public void RequestFullPass() => _fullPassRequested = true;

    /// <summary>Empties the content index (seconds to minutes on a big one, so it says so) and starts reading every file again.</summary>
    public void ClearAndRebuild()
    {
        Status("Clearing the content index…");
        _db.ClearContent();   // also ends a schema-2 upgrade in progress: it checks the version every batch
        Status("Content index cleared; rebuilding…");
        RequestFullPass();
    }

    private void Status(string s) { StatusText = s; StatusChanged?.Invoke(); }

    private void Loop()
    {
        var ct = _cts.Token;
        var lastPass = DateTime.MinValue;
        bool upgraded = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // a damaged meta.db (seen by a search or a write here) is replaced, then every file is read again
                if (_db.RecoverRequested) { Status("Repairing the content index…"); _db.Recover(); RequestFullPass(); Status("Content indexing idle"); }
                // one try per run: if it fails, indexing carries on with the old table (writes work on both) and it resumes next start
                if (!upgraded) { upgraded = true; _db.UpgradeContentFts(ct, (done, total) => Status($"Upgrading the content index (one time) {done:N0} / {total:N0}")); Status("Content indexing idle"); }
                if (_paused) { Thread.Sleep(1000); continue; }
                if (!_settings.Index.ContentIndexing && !_settings.Index.MetadataIndexing) { Thread.Sleep(2000); continue; }

                // individual changed files first
                if (DrainChanged(ct).Count > 0) continue;

                if (_fullPassRequested || (DateTime.UtcNow - lastPass).TotalMinutes > 30)
                {
                    if (_index.State != IndexState.Ready) { Thread.Sleep(2000); continue; }
                    _fullPassRequested = false;
                    try { FullPass(ct); } finally { IsRunning = false; }   // a pass that fails partway must not look busy forever
                    lastPass = DateTime.UtcNow;
                    try { _db.Maintain(ct); } catch (Exception ex) { Log.Warn("meta.db upkeep failed: " + ex.Message); if (MetaDb.IsCorrupt(ex)) _db.RecoverRequested = true; }
                    continue;
                }
                Thread.Sleep(1000);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (MetaDb.IsCorrupt(ex)) { Log.Warn("Content index damaged: " + ex.Message); _db.RecoverRequested = true; }
            catch (Exception ex) { Log.Error("Content indexer loop error", ex); Thread.Sleep(5000); }
        }
    }

    /// <summary>Indexes up to 200 queued changed files/folders; returns the files looked at (empty when nothing was queued).</summary>
    private HashSet<string> DrainChanged(CancellationToken ct)
    {
        var changedBatch = new List<string>();
        while (changedBatch.Count < 200 && _changed.Reader.TryRead(out var p)) changedBatch.Add(p);
        if (changedBatch.Count == 0) return new HashSet<string>();
        return ProcessPaths(changedBatch.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
    }

    private static readonly Dictionary<char, (int rank, bool oneAtATime)> _drives = new();

    /// <summary>Drive letters changed hands: look each drive's kind up again.</summary>
    internal static void ForgetDrives() { lock (_drives) _drives.Clear(); }

    /// <summary>
    /// A file's drive: its place in the content pass (0 NVMe, 1 other SSD, 2 removable, 3 hard drive or unknown) and
    /// whether to read its files one at a time (a spinning disk, or one Windows can't describe: a network path…).
    /// </summary>
    internal static (int rank, bool oneAtATime) DriveOf(string path)
    {
        if (path.Length < 2 || path[1] != ':') return (3, true);
        char d = char.ToUpperInvariant(path[0]);
        lock (_drives)
        {
            if (_drives.TryGetValue(d, out var kind)) return kind;
            (int? bus, bool? seeks) k = (null, null);
            bool removable = false;
            try { k = Native.DriveKind(d); removable = new DriveInfo(d.ToString()).DriveType == DriveType.Removable; } catch { }
            removable |= k.bus is 7 or 12 or 13;   // USB, SD, MMC
            int rank = removable ? 2 : k.bus == 17 ? 0 : k.seeks == false ? 1 : 3;
            _drives[d] = kind = (rank, k.seeks != false);
            Log.Info($"Drive {d}: {(rank switch { 0 => "NVMe", 1 => "SSD", 2 => "removable", _ => k.seeks == true ? "hard drive" : "type unknown" })}" +
                     $" (bus {k.bus?.ToString() ?? "?"}); content indexed {(rank + 1) switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => "4th" }}, read {(kind.oneAtATime ? "one file at a time" : "in parallel")}");
            return kind;
        }
    }

    private void FullPass(CancellationToken ct)
    {
        IsRunning = true;
        Status("Scanning for files to index…");
        var index = _index.Index;
        var contentExts = new HashSet<string>(_settings.Index.ContentExtensions, StringComparer.OrdinalIgnoreCase);
        var metaExts = new HashSet<string>(_settings.Index.MetadataExtensions, StringComparer.OrdinalIgnoreCase);
        long maxBytes = Math.Max(1, _settings.Index.ContentMaxMB) * 1024L * 1024L;
        var haveContent = _settings.Index.ContentIndexing ? _db.LoadContentIndexedPaths() : new HashSet<string>();
        var haveProps = _settings.Index.MetadataIndexing ? _db.LoadPropsIndexedPaths() : new HashSet<string>();

        // only what still needs reading
        var todo = new List<ToRead>();
        var totals = new Dictionary<char, int>();   // per drive: files to read, done or not
        var entries = index.Entries; int count = index.Count;
        for (int i = 0; i < count; i++)
        {
            ref var e = ref entries[i];
            if (e.Name is null || e.IsDirectory) continue;
            string ext = FileTypes.ExtensionOf(e.Name);
            if (ext.Length == 0) continue;
            bool wantContent = _settings.Index.ContentIndexing && contentExts.Contains(ext) && e.Size <= maxBytes;
            bool wantMeta = _settings.Index.MetadataIndexing && metaExts.Contains(ext);
            if (!wantContent && !wantMeta) continue;
            string path;
            try { path = index.PathOf(i); } catch { continue; }
            string stamp = path + "|" + e.Modified;
            char letter = DriveLetter(path);
            totals[letter] = totals.GetValueOrDefault(letter) + 1;
            wantContent &= !haveContent.Contains(stamp);
            wantMeta &= !haveProps.Contains(stamp);
            // drive order: NVMe, then other SSDs, removable drives, hard drives; same kind by letter
            int drive = DriveOf(path).rank * 32 + (path.Length > 1 && path[1] == ':' ? char.ToUpperInvariant(path[0]) - 'A' : 31);
            if (wantContent || wantMeta) todo.Add((i, path, ext, e.Modified, e.Size, wantContent, wantMeta, drive));
        }
        // on each drive: small files first, then under 1 MB, the largest last (storing costs about what the text weighs, so
        // most files are searchable soonest), each group A to Z by path (folder by folder, so a hard drive reads neighbours).
        // A photo read only for its details costs the same at any size, so it counts as small.
        static int SizeGroup(long size, bool content) => !content || size < 64 * 1024 ? 0 : size < 1024 * 1024 ? 1 : 2;
        todo.Sort((a, b) => a.drive != b.drive ? a.drive.CompareTo(b.drive)
            : SizeGroup(a.size, a.content) != SizeGroup(b.size, b.content) ? SizeGroup(a.size, a.content).CompareTo(SizeGroup(b.size, b.content))
            : string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase));
        Processed = 0;
        _driveRanges = DriveRanges(totals, todo.Count, k => todo[k].path);
        Candidates = todo.Count;
        Status($"Indexing content 0 / {Candidates:N0}");

        // the next chunk is read while the previous one is written (the write is the slow part on an SSD: ~500 files/s)
        const int chunk = 500;
        Task<(List<ContentRow> content, List<PropsRow> props)> Read(int from) =>
            Task.Run(() => ReadFiles(todo, from, Math.Min(todo.Count, from + chunk) - from, maxBytes, ct), ct);

        // one "done" line per drive (last todo index, file count), so each drive's speed shows in the log
        var driveRuns = new List<(int key, int end, int files)>();
        for (int k = 0; k < todo.Count; k++)
            if (driveRuns.Count > 0 && driveRuns[^1].key == todo[k].drive) driveRuns[^1] = (todo[k].drive, k, driveRuns[^1].files + 1);
            else driveRuns.Add((todo[k].drive, k, 1));
        int nextDone = 0;
        var driveClock = System.Diagnostics.Stopwatch.StartNew();

        var next = todo.Count > 0 ? Read(0) : null;
        var drained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // changed files indexed while `next` was being read
        for (int from = 0; next != null; from += chunk)
        {
            var (content, props) = next.GetAwaiter().GetResult();
            // the chunk may hold an older copy of a file saved meanwhile: the copy read for the change is the current one
            if (drained.Count > 0) { content.RemoveAll(r => drained.Contains(r.path)); props.RemoveAll(r => drained.Contains(r.path)); drained.Clear(); }
            bool more = from + chunk < todo.Count && !_paused && !_fullPassRequested && !ct.IsCancellationRequested;
            next = more ? Read(from + chunk) : null;
            if (content.Count > 0) _db.UpsertContentBatch(content);
            if (props.Count > 0) _db.UpsertPropsBatch(props);
            Processed = Math.Min(todo.Count, from + chunk);
            Status($"Indexing content {Processed:N0} / {Candidates:N0}");
            for (; nextDone < driveRuns.Count && driveRuns[nextDone].end < Processed; nextDone++, driveClock.Restart())
            {
                var (_, end, files) = driveRuns[nextDone];
                double s = driveClock.Elapsed.TotalSeconds;
                Log.Info($"Drive {todo[end].path[0]}: contents done, {files:N0} files in {(s < 120 ? $"{s:N1} s" : $"{s / 60:N0} min")} ({files / Math.Max(0.1, s):N0} files/s)");
            }
            // files saved meanwhile go first, while the next chunk keeps reading; one batch per chunk, so a noisy disk can't stall the pass
            if (_changed.Reader.TryPeek(out _)) drained.UnionWith(DrainChanged(ct));
        }
        IsRunning = false;
        if (_paused) { Status($"Content indexing paused at {Processed:N0} / {Candidates:N0}"); return; }
        if (_fullPassRequested) return;   // cut short for a new pass, which sets its own status: not up to date yet
        var stats = _db.Stats();
        Status($"Content index up to date ({stats.contentRows:N0} documents, {stats.propsRows:N0} with metadata)");
    }

    /// <summary>Indexes changed files (a folder: the files inside it); returns the files looked at.</summary>
    private HashSet<string> ProcessPaths(List<string> paths, CancellationToken ct)
    {
        var contentExts = new HashSet<string>(_settings.Index.ContentExtensions, StringComparer.OrdinalIgnoreCase);
        var metaExts = new HashSet<string>(_settings.Index.MetadataExtensions, StringComparer.OrdinalIgnoreCase);
        long maxBytes = Math.Max(1, _settings.Index.ContentMaxMB) * 1024L * 1024L;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ToRead>();
        foreach (var path in ExpandFolders(paths, contentExts, metaExts))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) { if (!Directory.Exists(path)) _db.RemovePath(path); continue; }   // gone: drop it and, for a folder, its children
                var fi = new FileInfo(path);
                string ext = FileTypes.ExtensionOf(fi.Name);
                bool content = _settings.Index.ContentIndexing && contentExts.Contains(ext) && fi.Length <= maxBytes;
                bool meta = _settings.Index.MetadataIndexing && metaExts.Contains(ext);
                if (!content && !meta || !seen.Add(path)) continue;
                files.Add((-1, path, ext, fi.LastWriteTimeUtc.Ticks, fi.Length, content, meta, 0));
            }
            catch (Exception ex) { Log.Warn("Re-index failed for " + path + ": " + ex.Message); }
        }
        // a repeated event or a folder reported again (e.g. after a rebuild) is already indexed as it is now: nothing to read
        var (haveContent, haveProps) = _db.IndexedStamps(seen);
        files = files.Select(f => f with { content = f.content && !haveContent.Contains(f.path + "|" + f.modified), meta = f.meta && !haveProps.Contains(f.path + "|" + f.modified) })
                     .Where(f => f.content || f.meta).ToList();
        // read like the full pass and written 500 files at a time: never a big moved folder's text all in memory, and the
        // write lock (which saving a folder view or closing a window waits on) only ever held briefly
        for (int from = 0; from < files.Count; from += 500)
        {
            var (content, props) = ReadFiles(files, from, Math.Min(500, files.Count - from), maxBytes, ct);
            if (content.Count > 0) _db.UpsertContentBatch(content);
            if (props.Count > 0) _db.UpsertPropsBatch(props);
        }
        return seen;
    }

    private static readonly int Readers = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// Reads files [from, from+count) of the list. SSD files on several threads; a hard drive's one at a time in list order,
    /// one reader per drive: parallel readers sent its head between folders and halved its speed (0.3.25).
    /// </summary>
    private static (List<ContentRow> content, List<PropsRow> props) ReadFiles(List<ToRead> todo, int from, int count, long maxBytes, CancellationToken ct)
    {
        var content = new System.Collections.Concurrent.ConcurrentBag<ContentRow>();
        var props = new System.Collections.Concurrent.ConcurrentBag<PropsRow>();
        var range = Enumerable.Range(from, count).ToList();
        var spinning = range.Where(k => DriveOf(todo[k].path).oneAtATime).GroupBy(k => char.ToUpperInvariant(todo[k].path[0]))
            .Select(g => Task.Run(() => { foreach (int k in g) { ct.ThrowIfCancellationRequested(); ReadOne(k); } }, ct)).ToArray();
        Parallel.ForEach(range.Where(k => !DriveOf(todo[k].path).oneAtATime), new ParallelOptions { MaxDegreeOfParallelism = Readers, CancellationToken = ct }, ReadOne);
        Task.WaitAll(spinning, ct);
        return (content.ToList(), props.ToList());

        void ReadOne(int k)
        {
            var t = todo[k];
            try
            {
                if (t.content) content.Add((t.path, t.modified, t.size, ExtractText(t.path, t.ext, maxBytes)));
                if (t.meta)
                {
                    var p = PropertyReader.Read(t.path);
                    if (p.Count == 0) { props.Add((t.path, t.modified, "{}", "")); return; }
                    string text = string.Join(" \n", p.Where(kv => kv.Key is not ("Width" or "Height" or "FrameWidth" or "FrameHeight" or "Bitrate")).Select(kv => kv.Value));
                    props.Add((t.path, t.modified, JsonSerializer.Serialize(p), text));
                }
            }
            catch (Exception ex) { Log.Warn("Index read failed for " + t.path + ": " + ex.Message); }
        }
    }

    /// <summary>Replaces each existing folder with the indexable files inside it; files and gone paths pass through.</summary>
    private static IEnumerable<string> ExpandFolders(List<string> paths, HashSet<string> contentExts, HashSet<string> metaExts)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in paths)
        {
            if (!Directory.Exists(path)) { yield return path; continue; }
            List<string> files;   // listed up front: a folder deleted mid-listing mustn't throw out of the whole batch
            try { files = Directory.EnumerateFiles(path, "*", opts).ToList(); } catch { continue; }
            foreach (var f in files)
            {
                string ext = FileTypes.ExtensionOf(Path.GetFileName(f));
                if (contentExts.Contains(ext) || metaExts.Contains(ext)) yield return f;
            }
        }
    }

    // ------------------------------------------------------------------ extraction

    private static readonly Regex TagStrip = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsCollapse = new(@"[ \t\r\f\v]+", RegexOptions.Compiled);

    public static string ExtractText(string path, string ext, long maxBytes)
    {
        try
        {
            switch (ext.ToLowerInvariant())
            {
                case "docx": return OfficeXml(path, "word/document.xml");
                case "xlsx": return OfficeXml(path, "xl/sharedStrings.xml");
                case "pptx": return OfficeZipAll(path, "ppt/slides/");
                case "html": case "htm": return Clean(TagStrip.Replace(ReadText(path, maxBytes), " "));
                default: return Clean(ReadText(path, maxBytes));
            }
        }
        catch (Exception ex) { Log.Warn("Extract failed for " + path + ": " + ex.Message); return string.Empty; }
    }

    private static string ReadText(string path, long maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        if (fs.Length > maxBytes) return string.Empty;
        // binary sniff: NUL bytes in the first 8 KB => not text
        var head = new byte[Math.Min(8192, fs.Length)];
        int n = fs.Read(head, 0, head.Length);
        for (int i = 0; i < n; i++) if (head[i] == 0) { if (!(n >= 2 && (head[0] == 0xFF && head[1] == 0xFE || head[0] == 0xFE && head[1] == 0xFF))) return string.Empty; break; }
        fs.Position = 0;
        using var reader = new StreamReader(fs, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    private static string OfficeXml(string path, string part)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(part);
        if (entry == null) return string.Empty;
        using var s = entry.Open();
        return XmlText(s);
    }

    private static string OfficeZipAll(string path, string prefix)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var s = entry.Open();
            sb.Append(XmlText(s)).Append('\n');
        }
        return Clean(sb.ToString());
    }

    private static string XmlText(Stream s)
    {
        var sb = new StringBuilder();
        using var reader = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA) { sb.Append(reader.Value).Append(' '); }
            else if (reader.NodeType == XmlNodeType.Element && (reader.LocalName == "p" || reader.LocalName == "br" || reader.LocalName == "tab")) sb.Append('\n');
        }
        return Clean(sb.ToString());
    }

    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = WsCollapse.Replace(text, " ");
        return text.Length > 2_000_000 ? text[..2_000_000] : text;
    }

    public void Dispose() => _cts.Cancel();
}
