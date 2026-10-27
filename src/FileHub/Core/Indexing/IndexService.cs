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
    public string StatusText { get; private set; } = "Not indexed";
    public long ProgressCount { get; private set; }

    public event Action? IndexChanged;          // index instance swapped (rebuild)
    public event Action? StatusChanged;         // state / progress text
    public event Action<string, bool>? FileChanged; // path, isDirectory (for content indexer)

    public IndexService(AppSettings settings) => _settings = settings;

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
                _lastSave = DateTime.UtcNow; // the snapshot on disk is current; no need to re-save on the first watcher event
                SetState(IndexState.Ready, $"{loaded.LiveCount:N0} items (snapshot)");
                IndexChanged?.Invoke();
                Log.Info($"Index snapshot loaded: {loaded.LiveCount:N0} entries in {sw.ElapsedMilliseconds} ms");
            }
            _monitor.Watch(GetRoots());
            _ = Task.Run(ConsumeChangesAsync);

            bool stale = loaded == null
                         || _settings.Index.RebuildOnStartup
                         || (DateTime.UtcNow - loaded.BuiltUtc).TotalHours >= Math.Max(1, _settings.Index.RebuildIntervalHours)
                         || RootsDiffer(loaded);
            if (stale) await RebuildAsync();
            else SetState(IndexState.Ready, $"{Index.LiveCount:N0} items");
        });
    }

    private bool RootsDiffer(FileIndex loaded)
    {
        var want = GetRoots().Select(FileIndex.NormalizeDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var have = loaded.Roots.Select(loaded.PathOf).Select(FileIndex.NormalizeDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return !want.SequenceEqual(have, StringComparer.OrdinalIgnoreCase);
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
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var progress = new Progress<IndexProgress>(p =>
            {
                lock (counts) { counts[p.Root] = p.Entries; ProgressCount = counts.Values.Sum(); }
                StatusText = $"Indexing… {ProgressCount:N0} items";
                StatusChanged?.Invoke();
            });
            FileIndex built;
            try
            {
                built = await Task.Run(() => IndexBuilder.Build(roots, _settings.Index.Excludes, progress, ct), ct);
            }
            catch (OperationCanceledException) { SetState(IndexState.Ready, $"{Index.LiveCount:N0} items"); return; }

            Index = built;
            _building = false;
            List<FsChange> replay;
            lock (_pendingDuringBuild) { replay = _pendingDuringBuild.ToList(); _pendingDuringBuild.Clear(); }
            foreach (var c in replay) Apply(c);
            SetState(IndexState.Ready, $"{built.LiveCount:N0} items");
            IndexChanged?.Invoke();
            Log.Info($"Index built: {built.LiveCount:N0} entries in {sw.Elapsed.TotalSeconds:F1}s");
            _monitor.Watch(roots);
            await SaveAsync();
        }
        finally { _building = false; _buildGate.Release(); }
    }

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public Task SaveAsync() => Task.Run(() =>
    {
        if (!_saveGate.Wait(0)) return; // a save is already running
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IndexStore.Save(Index, Paths.IndexFile);
            _lastSave = DateTime.UtcNow;
            Log.Info($"Index snapshot saved in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { Log.Error("Index save failed", ex); }
        finally { _saveGate.Release(); }
    });

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
                while (reader.TryRead(out var change))
                {
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
        switch (c.Kind)
        {
            case FsChangeKind.Created:
                AddPath(index, c.Path);
                break;
            case FsChangeKind.Deleted:
            {
                int id = index.FindByPath(c.Path);
                if (id >= 0) index.Remove(id);
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
                else if (id < 0) AddPath(index, c.Path);
                break;
            }
            case FsChangeKind.Renamed:
            {
                int id = c.OldPath != null ? index.FindByPath(c.OldPath) : -1;
                string newName = Path.GetFileName(c.Path);
                if (id >= 0)
                {
                    string oldParent = Path.GetDirectoryName(c.OldPath!) ?? string.Empty;
                    string newParent = Path.GetDirectoryName(c.Path) ?? string.Empty;
                    if (string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase)) index.Rename(id, newName);
                    else { index.Remove(id); AddPath(index, c.Path); }
                    FileChanged?.Invoke(c.Path, index[id].IsDirectory);
                }
                else AddPath(index, c.Path);
                break;
            }
            case FsChangeKind.Overflow:
                Log.Warn("Watcher overflow; scheduling rebuild");
                _ = Task.Delay(5000).ContinueWith(_ => RebuildAsync());
                break;
        }
    }

    private void AddPath(FileIndex index, string path)
    {
        try
        {
            if (index.FindByPath(path) >= 0) return;
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
        catch (Exception ex) { Log.Warn("AddPath failed for " + path + ": " + ex.Message); }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _buildCts?.Cancel();
        _monitor.Dispose();
    }
}
