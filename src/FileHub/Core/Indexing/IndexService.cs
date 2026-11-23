using System.IO;
using FileHub.Core.Settings;
using FileHub.Platform;

namespace FileHub.Core.Indexing;

public enum IndexState { Idle, Loading, Building, Ready }

/// <summary>
/// Owns the live <see cref="FileIndex"/>: loads the snapshot at startup, (re)builds in the background,
/// applies file-system change events, and persists the snapshot. All public events fire on a
/// background thread; UI code must marshal.
/// </summary>
public sealed class IndexService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ChangeMonitor _monitor = new();
    private CancellationTokenSource? _buildCts;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<FsChange> _pendingDuringBuild = new();
    private volatile bool _building;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private DateTime _lastSave = DateTime.MinValue;

    public FileIndex Index { get; private set; } = new(16);
    public IndexState State { get; private set; } = IndexState.Idle;
    private string _statusText = "Not indexed";
    public string StatusText { get => Paused ? "Paused · " + _statusText : _statusText; private set => _statusText = value; }

    /// <summary>Paused = crawling blocks and live changes queue up until resumed (runtime only).</summary>
    public bool Paused { get; private set; }
    private readonly ManualResetEventSlim _runGate = new(true);
    public bool RealTime => _settings.Index.RealTimeIndexing;
    private DateTime _lastOverflowRebuild = DateTime.MinValue;
    public long ProgressCount { get; private set; }

    public event Action? IndexChanged;          // index instance swapped (rebuild)
    public event Action? StatusChanged;         // state / progress text
    public event Action<string, bool>? FileChanged; // path, isDirectory (for content indexer)

    public IndexService(AppSettings settings) => _settings = settings;

    public void SetPaused(bool paused)
    {
        Paused = paused;
        if (paused) _runGate.Reset(); else _runGate.Set();
        Log.Info(paused ? "Indexing paused" : "Indexing resumed");
        StatusChanged?.Invoke();
    }

    /// <summary>Turns live file-system tracking on/off. Changes made while off are picked up by the next rebuild.</summary>
    public void SetRealTime(bool on)
    {
        _settings.Index.RealTimeIndexing = on;
        if (on) _monitor.Watch(GetRoots()); else _monitor.Stop();
        Log.Info(on ? "Real-time indexing on" : "Real-time indexing off");
        StatusChanged?.Invoke();
    }

    public IReadOnlyList<string> GetRoots()
    {
        var roots = new List<string>();
        if (_settings.Index.IndexAllFixedDrives)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType == DriveType.Fixed || (d.DriveType == DriveType.Removable && _settings.Index.IndexRemovableDrives))
                        roots.Add(d.RootDirectory.FullName);
                }
                catch { }
            }
        }
        foreach (var r in _settings.Index.Roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var norm = FileIndex.NormalizeDir(r.Trim());
            if (!roots.Any(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase) || string.Equals(FileIndex.NormalizeDir(x), norm, StringComparison.OrdinalIgnoreCase)))
                roots.Add(norm);
        }
        return roots;
    }

    public void Start()
    {
        Task.Run(async () =>
        {
            SetState(IndexState.Loading, "Loading index…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var loaded = IndexStore.Load(Paths.IndexFile);
            if (loaded != null)
            {
                Index = loaded;
                _goodCount = loaded.LiveCount;
                _lastSave = DateTime.UtcNow; // the snapshot on disk is current; no need to re-save on the first watcher event
                // searchable now; Ready (which lets the content and thumbnail passes start) once the drives are matched up
                SetState(IndexState.Loading, $"{loaded.LiveCount:N0} items (snapshot)");
                IndexChanged?.Invoke();
                Log.Info($"Index snapshot loaded: {loaded.LiveCount:N0} entries in {sw.ElapsedMilliseconds} ms");
            }
            if (RealTime) _monitor.Watch(GetRoots());
            _ = Task.Run(ConsumeChangesAsync);

            bool stale = loaded == null
                         || _settings.Index.RebuildOnStartup
                         || (DateTime.UtcNow - loaded.BuiltUtc).TotalHours >= Math.Max(1, _settings.Index.RebuildIntervalHours);
            await SyncDrivesAsync(crawl: !stale);   // a full rebuild follows anyway: no need to crawl new drives first
            _driveMask = string.Join("", Environment.GetLogicalDrives());
            _drivePoll = new Timer(_ => PollDrives(), null, 3000, 3000);
            if (stale) await RebuildAsync();
            else SetState(IndexState.Ready, $"{Index.LiveCount:N0} items");
        });
    }

    // ------------------------------------------------------------------ drives

    /// <summary>The drives FileHub knows by partition ID (drives.json).</summary>
    public DriveRegistry Drives { get; } = DriveRegistry.Load();

    /// <summary>Re-keys a drive's meta.db / thumbs.db rows from one path prefix to another, reporting rows moved (set by App).</summary>
    public Action<string, string, Action<long>>? MoveRows { get; set; }

    private Timer? _drivePoll;
    private string _driveMask = "";
    private volatile bool _syncQueued;

    // shortcut: watches drive letters only, so a drive swapped for another under the same letter within 3 s, or a card
    // put into an empty reader slot, is noticed at the next start; upgrade to WM_DEVICECHANGE if that ever matters
    private void PollDrives()
    {
        string mask = string.Join("", Environment.GetLogicalDrives());
        if (mask == _driveMask) return;
        _driveMask = mask;
        Log.Info("Drive letters changed: " + mask.Replace(":\\", ""));
        if (_syncQueued) return;
        _syncQueued = true;
        _ = Task.Run(() => SyncDrivesAsync(crawl: true));
    }

    private async Task SyncDrivesAsync(bool crawl)
    {
        try { await _buildGate.WaitAsync(_lifetime.Token); } catch (OperationCanceledException) { return; }
        _syncQueued = false;
        _building = true;   // live changes queue up meanwhile and are applied after
        try { SyncDrives(crawl); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Drive check failed", ex); }
        finally
        {
            _building = false;
            ReplayPending();
            if (State != IndexState.Ready && State != IndexState.Loading) SetState(IndexState.Ready, $"{Index.LiveCount:N0} items");
            _buildGate.Release();
        }
    }

    /// <summary>
    /// Matches the drives plugged in now to the ones FileHub knows by partition ID. A known drive under another letter (or
    /// on another PC) keeps its file list, contents and thumbnails, moved to the new letter, and only it is re-read for
    /// changes; an unplugged one is set aside, not wiped; a new drive or folder root is crawled on its own.
    /// </summary>
    private void SyncDrives(bool crawl)
    {
        ContentIndexer.ForgetDrives();
        var roots = GetRoots();
        var index = Index;
        var present = new Dictionary<char, (string key, string label, string kind)>();
        // a clone shares its original's ID: the letter the drive was last seen under keeps it, the other counts as new
        var found = roots.Where(r => r.Length == 3 && r[1] == ':').Select(r => (letter: char.ToUpperInvariant(r[0]), id: DriveRegistry.Identify(r[0])))
            .Where(x => x.id != null).OrderBy(x => Drives.Find(x.id!.Value.key)?.Letter == x.letter.ToString() ? 0 : 1).ToList();
        foreach (var (letter, id) in found)
        {
            var p = id!.Value;
            if (present.Values.Any(o => o.key == p.key)) { Log.Warn($"Drive {letter}: same partition ID as another drive plugged in (a clone?); treated as a new drive"); p.key += "#" + letter; }
            present[letter] = p;
        }
        string? KeyAt(string letter) => present.TryGetValue(letter[0], out var p) ? p.key : null;

        bool changed = false;
        var aside = new Dictionary<string, FileIndex>();
        // 1. each known drive leaves a letter that now holds another drive (or none)
        foreach (var d in Drives.Drives)
        {
            d.Online = false;
            if (d.IndexAt is { } at && KeyAt(at) != d.Key)
            {
                int root = index.FindDirectory(at + ":\\");
                if (root >= 0 && index[root].Parent < 0)
                {
                    aside[d.Key] = index.ExtractRoot(root);
                    index.RemoveRoot(root);
                    Log.Info($"Drive {at}: ({d.Label}) gone; its {aside[d.Key].LiveCount:N0} files are set aside");
                }
                d.IndexAt = null; changed = true;
            }
            // its saved contents and thumbnails stay under the letter unless another drive now has it
            if (d.RowsAt is { } rows && KeyAt(rows) is { } other && other != d.Key) { Move(rows + ":\\", DriveRegistry.Parked(d)); d.RowsAt = null; }
        }
        // 2. each drive plugged in comes back under its letter
        var refresh = new List<string>();
        foreach (var (letter, p) in present)
        {
            string at = letter.ToString(), root = at + ":\\";
            var d = Drives.Find(p.key);
            if (d == null)
            {
                // first sight: what's listed or stored under this letter and claimed by no other drive is this one's (that's how
                // the letter-keyed data of 0.3.38 and earlier is taken over)
                d = new DriveRecord { Key = p.key };
                if (!Drives.Drives.Any(o => o.RowsAt == at)) d.RowsAt = at;
                if (!Drives.Drives.Any(o => o.IndexAt == at) && index.FindDirectory(root) >= 0) d.IndexAt = at;
                Drives.Drives.Add(d);
                Log.Info($"Drive {at}: new to FileHub ({p.label}, {p.kind}, {p.key})");
                changed = true;
            }
            d.Letter = at; d.Label = p.label; d.Kind = p.kind; d.LastSeenUtc = DateTime.UtcNow; d.Online = true;
            if (d.RowsAt != at) { Move(d.RowsAt is { } was ? was + ":\\" : DriveRegistry.Parked(d), root); d.RowsAt = at; }
            if (d.IndexAt != at)
            {
                int stale = index.FindDirectory(root);
                if (stale >= 0 && index[stale].Parent < 0) index.RemoveRoot(stale);   // listed under this letter, but no known drive's
                string file = DriveRegistry.SetAsideFile(d);
                var tree = aside.Remove(d.Key, out var t) ? t : IndexStore.Load(file);
                if (tree != null)
                {
                    index.Merge(tree, root);
                    refresh.Add(root);
                    Log.Info($"Drive {at}: recognised ({p.label}); its {tree.LiveCount:N0} files are back, re-reading the drive for changes");
                }
                try { File.Delete(file); } catch { }
                d.IndexAt = at; changed = true;
            }
        }
        // drives still unplugged: their file lists wait on disk
        foreach (var (key, tree) in aside)
        {
            try
            {
                string file = DriveRegistry.SetAsideFile(Drives.Find(key)!);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                IndexStore.Save(tree, file);
            }
            catch (Exception ex) { Log.Warn("Set-aside file list not saved: " + ex.Message); }
        }
        // 3. roots no longer wanted go; new ones (a new drive, a folder added in Settings) are crawled on their own
        var wanted = new HashSet<string>(roots.Select(FileIndex.NormalizeDir), StringComparer.OrdinalIgnoreCase);
        foreach (int r in index.Roots.ToArray())
            if (!wanted.Contains(FileIndex.NormalizeDir(index.PathOf(r)))) { Log.Info($"Root {index.PathOf(r)} no longer indexed"); index.RemoveRoot(r); changed = true; }
        var live = index.Roots.Select(r => FileIndex.NormalizeDir(index.PathOf(r))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toCrawl = wanted.Where(w => !live.Contains(w)).Concat(refresh).ToList();
        if (crawl && toCrawl.Count > 0)
        {
            SetState(IndexState.Building, $"Indexing {string.Join(", ", toCrawl)}…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var built = IndexBuilder.Build(toCrawl, _settings.Index.Excludes, CrawlProgress(), _lifetime.Token, _runGate);
            foreach (var r in refresh) { int id = index.FindDirectory(r); if (id >= 0) index.RemoveRoot(id); }
            index.Merge(built);
            Log.Info($"Indexed {string.Join(", ", toCrawl)}: {built.LiveCount:N0} entries in {sw.Elapsed.TotalSeconds:F1}s");
            changed = true;
        }

        Drives.Save();
        if (!changed) return;
        _goodCount = index.LiveCount;   // an unplugged drive is no reason to think the index broke (see SaveAsync)
        _dirty = true;
        IndexChanged?.Invoke();
        if (RealTime) _monitor.Watch(roots);
        _ = SaveAsync();

        void Move(string from, string to)
        {
            if (MoveRows == null) return;
            static string Name(string prefix) => prefix.StartsWith("::") ? "storage" : prefix.TrimEnd('\\');
            string what = $"Moving saved contents and thumbnails {Name(from)} → {Name(to)}";
            SetState(IndexState.Building, what + "…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long shown = 0;
            MoveRows(from, to, n =>
            {
                if (sw.ElapsedMilliseconds - shown < 250) return;
                shown = sw.ElapsedMilliseconds;
                StatusText = $"{what}: {n:N0} rows"; StatusChanged?.Invoke();
            });
            Log.Info($"Drive data moved {from} -> {to} in {sw.ElapsedMilliseconds} ms");
        }
    }

    private IProgress<IndexProgress> CrawlProgress()
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        return new Progress<IndexProgress>(p =>
        {
            lock (counts) { counts[p.Root] = p.Entries; ProgressCount = counts.Values.Sum(); }
            StatusText = $"Indexing… {ProgressCount:N0} items";
            StatusChanged?.Invoke();
        });
    }

    public async Task RebuildAsync()
    {
        if (!await _buildGate.WaitAsync(0)) return; // already building
        try
        {
            _buildCts?.Cancel();
            _buildCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var ct = _buildCts.Token;
            _building = true;
            lock (_pendingDuringBuild) _pendingDuringBuild.Clear();
            var roots = GetRoots();
            SetState(IndexState.Building, "Indexing…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = CrawlProgress();
            FileIndex built;
            try
            {
                built = await Task.Run(() => IndexBuilder.Build(roots, _settings.Index.Excludes, progress, ct, _runGate), ct);
            }
            catch (OperationCanceledException)
            {
                // the old index stays: apply what changed meanwhile instead of dropping it
                _building = false;
                ReplayPending();
                SetState(IndexState.Ready, $"{Index.LiveCount:N0} items");
                return;
            }

            Index = built;
            _goodCount = built.LiveCount;
            foreach (var d in Drives.Drives.Where(d => d.Online)) d.IndexAt = built.FindDirectory(d.Letter + ":\\") >= 0 ? d.Letter : null;
            Drives.Save();
            _building = false;
            ReplayPending();
            SetState(IndexState.Ready, $"{built.LiveCount:N0} items");
            IndexChanged?.Invoke();
            Log.Info($"Index built: {built.LiveCount:N0} entries in {sw.Elapsed.TotalSeconds:F1}s");
            if (RealTime) _monitor.Watch(roots);
            await SaveAsync();
        }
        finally { _building = false; _buildGate.Release(); }
    }

    private void ReplayPending()
    {
        List<FsChange> replay;
        lock (_pendingDuringBuild) { replay = _pendingDuringBuild.ToList(); _pendingDuringBuild.Clear(); }
        foreach (var c in replay)
            try { Apply(c); } catch (Exception ex) { Log.Warn("Apply change failed: " + ex.Message); }
    }

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public Task SaveAsync() => Task.Run(() =>
    {
        if (!_saveGate.Wait(0)) return; // a save is already running
        try
        {
            // never store an index that lost over half its entries through live changes: something went wrong
            // (that's how an empty index got saved once); keep the good snapshot and rebuild instead
            int live = Index.LiveCount;
            if (_goodCount > 10_000 && live < _goodCount / 2)
            {
                Log.Warn($"Index shrank from {_goodCount:N0} to {live:N0} entries without a rebuild; not saving it, rebuilding instead");
                if (!_building && !_lifetime.IsCancellationRequested) _ = Task.Run(RebuildAsync);
                return;
            }
            _dirty = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IndexStore.Save(Index, Paths.IndexFile);
            _lastSave = DateTime.UtcNow;
            _goodCount = live;
            Log.Info($"Index snapshot saved in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { Log.Error("Index save failed", ex); }
        finally { _saveGate.Release(); }
    });

    private volatile bool _dirty;   // live changes applied since the last snapshot save
    private volatile int _goodCount; // entries at the last load, build or save: the yardstick for "the index suddenly shrank"

    /// <summary>Called on exit: writes the snapshot if live changes would otherwise be lost (never while loading, so an empty index can't overwrite a good one).</summary>
    public void SaveIfChanged()
    {
        if (_dirty && State == IndexState.Ready) SaveAsync().Wait(10_000);
    }

    public void CancelBuild() => _buildCts?.Cancel();

    private void SetState(IndexState s, string text)
    {
        State = s; StatusText = text;
        StatusChanged?.Invoke();
    }

    // ------------------------------------------------------------------ change application

    private async Task ConsumeChangesAsync()
    {
        var reader = _monitor.Changes;
        try
        {
            while (await reader.WaitToReadAsync(_lifetime.Token))
            {
                // paused: leave changes queued in the channel until resumed
                while (Paused) await Task.Delay(250, _lifetime.Token);
                // coalesce: apps often fire many "Changed" events for the same file in a burst
                var changedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int n = 0;
                while (n++ < 5000 && reader.TryRead(out var change))
                {
                    if (change.Kind == FsChangeKind.Changed && !changedSeen.Add(change.Path)) continue;
                    if (_building) { lock (_pendingDuringBuild) _pendingDuringBuild.Add(change); continue; }
                    try { Apply(change); } catch (Exception ex) { Log.Warn("Apply change failed: " + ex.Message); }
                }
                // opportunistic periodic snapshot save after activity
                if ((DateTime.UtcNow - _lastSave).TotalMinutes > 10 && State == IndexState.Ready) _ = SaveAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Apply(FsChange c)
    {
        var index = Index;
        _dirty = true;
        switch (c.Kind)
        {
            case FsChangeKind.Created:
                AddPath(index, c.Path);
                break;
            case FsChangeKind.Deleted:
            {
                int id = index.FindByPath(c.Path);
                // a drive root can't be deleted: such an event is bogus, and removing the root would empty the whole index
                if (id >= 0 && index[id].Parent < 0) { Log.Warn("Ignored a delete event for drive root " + c.Path); break; }
                // still on disk: a late or bogus event (e.g. an app that deletes and re-creates a file while saving)
                if (id >= 0 && ExistsOnDisk(c.Path)) break;
                if (id >= 0) RemoveLogged(index, id, c.Path);
                FileChanged?.Invoke(c.Path, false);
                break;
            }
            case FsChangeKind.Changed:
            {
                int id = index.FindByPath(c.Path);
                if (id >= 0 && !index[id].IsDirectory)
                {
                    try
                    {
                        var fi = new FileInfo(c.Path);
                        if (fi.Exists) { index.Update(id, fi.Length, fi.LastWriteTimeUtc.Ticks, fi.Attributes); FileChanged?.Invoke(c.Path, false); }
                    }
                    catch { }
                }
                else if (id >= 0)
                {
                    // folder contents changed: keep its timestamp current so Explorer's index preview matches disk
                    try { var di = new DirectoryInfo(c.Path); if (di.Exists) index.Update(id, 0, di.LastWriteTimeUtc.Ticks, di.Attributes); } catch { }
                }
                else AddPath(index, c.Path);
                break;
            }
            case FsChangeKind.Renamed:
            {
                int id = c.OldPath != null ? index.FindByPath(c.OldPath) : -1;
                // the watcher reports the watched root as the old path when it lost the old name; treat it as a new item,
                // never as "the drive moved" (removing the root would empty the whole index)
                bool rootOld = id >= 0 && index[id].Parent < 0;
                if (rootOld) { Log.Warn($"Rename event with drive root as old path ({c.OldPath} -> {c.Path}); treated as new"); id = -1; }
                string newName = Path.GetFileName(c.Path);
                if (id >= 0)
                {
                    bool isDir = index[id].IsDirectory;   // read before Remove frees the slot
                    string oldParent = Path.GetDirectoryName(c.OldPath!) ?? string.Empty;
                    string newParent = Path.GetDirectoryName(c.Path) ?? string.Empty;
                    if (string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
                    {
                        // safe-save (write temp, rename over the original): the original's entry would stay as a duplicate
                        int dup = index.FindByPath(c.Path);
                        if (dup >= 0 && dup != id) RemoveLogged(index, dup, c.Path);
                        index.Rename(id, newName);
                    }
                    else
                    {
                        // moved to another folder; only drop the old entry if it's really gone from the old place
                        if (!ExistsOnDisk(c.OldPath!)) RemoveLogged(index, id, c.OldPath!);
                        AddPath(index, c.Path);
                    }
                    FileChanged?.Invoke(c.Path, isDir);
                }
                else AddPath(index, c.Path);
                // drop the old path's content rows (and a folder's children); skip case-only renames, which would re-add the old casing
                // never the bogus drive-root old path: the content pass would expand it into every file on the drive (0.3.34)
                if (c.OldPath != null && !rootOld && !string.Equals(c.OldPath, c.Path, StringComparison.OrdinalIgnoreCase))
                    FileChanged?.Invoke(c.OldPath, false);
                break;
            }
            case FsChangeKind.Overflow:
                // never let a noisy disk trigger back-to-back full rebuilds
                if ((DateTime.UtcNow - _lastOverflowRebuild).TotalMinutes < 30) { Log.Warn("Watcher overflow; rebuild skipped (last overflow rebuild < 30 min ago)"); break; }
                _lastOverflowRebuild = DateTime.UtcNow;
                Log.Warn("Watcher overflow; scheduling rebuild");
                _ = Task.Delay(5000).ContinueWith(_ =>
                {
                    // an unplugged drive's watcher fails too: the drive check sets it aside, no rebuild needed
                    if (!Directory.Exists(c.Path)) { Log.Info($"Rebuild skipped: {c.Path} is gone (unplugged)"); return Task.CompletedTask; }
                    return RebuildAsync();
                });
                break;
        }
    }

    private static bool ExistsOnDisk(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); } catch { return false; }
    }

    /// <summary>Removes an entry (a folder takes its contents with it) and logs any removal that takes a big bite out of the index.</summary>
    private static void RemoveLogged(FileIndex index, int id, string path)
    {
        int before = index.LiveCount;
        index.Remove(id);
        int gone = before - index.LiveCount;
        if (gone >= 10_000) Log.Warn($"Removing {path} took {gone:N0} entries out of the index ({before:N0} -> {index.LiveCount:N0})");
    }

    private void AddPath(FileIndex index, string path)
    {
        try
        {
            int existing = index.FindByPath(path);
            if (existing >= 0)
            {
                // already listed (a re-created file, or a snapshot older than the change): refresh it and re-read its contents
                bool dir = index[existing].IsDirectory;
                if (!dir && File.Exists(path)) { var f = new FileInfo(path); index.Update(existing, f.Length, f.LastWriteTimeUtc.Ticks, f.Attributes); }
                FileChanged?.Invoke(path, dir);
                return;
            }
            string? parentPath = Path.GetDirectoryName(path);
            if (parentPath == null) return;
            int parent = index.FindDirectory(parentPath);
            if (parent < 0) return; // outside indexed roots or parent missing
            FileAttributes attrs;
            long size = 0, modified, created;
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                attrs = di.Attributes; modified = di.LastWriteTimeUtc.Ticks; created = di.CreationTimeUtc.Ticks;
                int id = index.Add(di.Name, parent, 0, modified, created, attrs);
                if ((attrs & FileAttributes.ReparsePoint) == 0)
                    IndexBuilder.CrawlInto(index, path, id, _settings.Index.Excludes, _lifetime.Token);
                FileChanged?.Invoke(path, true);
            }
            else if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                attrs = fi.Attributes; size = fi.Length; modified = fi.LastWriteTimeUtc.Ticks; created = fi.CreationTimeUtc.Ticks;
                index.Add(fi.Name, parent, size, modified, created, attrs);
                FileChanged?.Invoke(path, false);
            }
        }
        catch (FileNotFoundException) { }       // short-lived temp file already gone
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) { Log.Warn("AddPath failed for " + path + ": " + ex.Message); }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _drivePoll?.Dispose();
        _buildCts?.Cancel();
        _monitor.Dispose();
    }
}
