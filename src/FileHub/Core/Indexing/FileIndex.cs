using System.IO;

namespace FileHub.Core.Indexing;

/// <summary>One indexed file or directory. Kept as a compact struct in a flat array (Everything-style).</summary>
public struct Entry
{
    public string? Name;        // null == deleted (tombstone)
    public int Parent;          // index of parent entry, -1 for volume roots
    public long Size;
    public long Modified;       // DateTime UTC ticks
    public long Created;        // DateTime UTC ticks
    public FileAttributes Attributes;

    public readonly bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public readonly bool IsDeleted => Name is null;
}

/// <summary>
/// In-memory, columnar-ish file index. Reads are lock-free (readers grab the array reference and
/// count once); mutations (add / remove / rename) are serialized with a lock.
/// </summary>
public sealed class FileIndex
{
    private Entry[] _entries;
    private int _count;
    private int _deleted;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _dirs;              // full path -> id (directories only)
    private readonly Dictionary<int, int[]> _childrenCache = new();

    public FileIndex(int capacity = 1 << 16)
    {
        _entries = new Entry[Math.Max(16, capacity)];
        _dirs = new Dictionary<string, int>(Math.Max(16, capacity / 8), StringComparer.OrdinalIgnoreCase);
    }

    public int Count => Volatile.Read(ref _count);
    public int DeletedCount => Volatile.Read(ref _deleted);
    public int LiveCount => Count - DeletedCount;
    public Entry[] Entries => _entries;          // snapshot; use together with Count
    public object SyncRoot => _lock;
    public DateTime BuiltUtc { get; set; }
    public IReadOnlyList<int> Roots => _roots;
    private readonly List<int> _roots = new();

    public ref Entry this[int id] => ref _entries[id];

    // ------------------------------------------------------------------ mutation

    public int AddRoot(string rootPath)
    {
        lock (_lock)
        {
            string norm = NormalizeDir(rootPath);
            if (_dirs.TryGetValue(norm, out int existing)) return existing;
            int id = AppendNoLock(norm, -1, 0, 0, 0, FileAttributes.Directory);
            _dirs[norm] = id;
            _roots.Add(id);
            return id;
        }
    }

    public int Add(string name, int parent, long size, long modified, long created, FileAttributes attrs)
    {
        lock (_lock)
        {
            int id = AppendNoLock(name, parent, size, modified, created, attrs);
            if ((attrs & FileAttributes.Directory) != 0) _dirs[PathOf(id)] = id;
            _childrenCache.Remove(parent);
            return id;
        }
    }

    /// <summary>Fast append used by the crawler; caller guarantees the directory dictionary is kept in sync via <see cref="RegisterDirectory"/>.</summary>
    public int AppendRaw(string name, int parent, long size, long modified, long created, FileAttributes attrs)
    {
        lock (_lock) return AppendNoLock(name, parent, size, modified, created, attrs);
    }

    public void RegisterDirectory(string fullPath, int id)
    {
        lock (_lock) _dirs[NormalizeDir(fullPath)] = id;
    }

    private int AppendNoLock(string name, int parent, long size, long modified, long created, FileAttributes attrs)
    {
        if (_count == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
        int id = _count;
        _entries[id] = new Entry { Name = name, Parent = parent, Size = size, Modified = modified, Created = created, Attributes = attrs };
        Volatile.Write(ref _count, id + 1);
        return id;
    }

    public void Remove(int id)
    {
        lock (_lock)
        {
            if (id < 0 || id >= _count || _entries[id].Name is null) return;
            RemoveNoLock(id);
        }
    }

    private void RemoveNoLock(int id)
    {
        ref var e = ref _entries[id];
        if (e.Name is null) return;
        if (e.IsDirectory)
        {
            _dirs.Remove(PathOf(id));
            foreach (int child in ChildrenNoLock(id)) RemoveNoLock(child);
            _childrenCache.Remove(id);
        }
        _childrenCache.Remove(e.Parent);
        e.Name = null;
        _deleted++;
    }

    public void Rename(int id, string newName)
    {
        lock (_lock)
        {
            if (id < 0 || id >= _count) return;
            ref var e = ref _entries[id];
            if (e.Name is null) return;
            if (e.IsDirectory)
            {
                string oldPath = PathOf(id);
                // rebuild directory keys for this subtree
                var affected = _dirs.Where(kv => kv.Key.Length >= oldPath.Length && kv.Key.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase)
                                                 && (kv.Key.Length == oldPath.Length || kv.Key[oldPath.Length] == '\\')).ToList();
                foreach (var kv in affected) _dirs.Remove(kv.Key);
                e.Name = newName;
                foreach (var kv in affected) _dirs[PathOf(kv.Value)] = kv.Value;
            }
            else e.Name = newName;
            _childrenCache.Remove(e.Parent);
        }
    }

    public void Update(int id, long size, long modified, FileAttributes attrs)
    {
        lock (_lock)
        {
            if (id < 0 || id >= _count) return;
            ref var e = ref _entries[id];
            if (e.Name is null) return;
            e.Size = size; e.Modified = modified; e.Attributes = attrs;
        }
    }

    // ------------------------------------------------------------------ queries

    [ThreadStatic] private static System.Text.StringBuilder? _pathBuilder;

    public string PathOf(int id)
    {
        var entries = _entries;
        if (id < 0 || id >= _count) return string.Empty;
        Span<int> chain = stackalloc int[512];
        int depth = 0;
        int cur = id;
        while (cur >= 0 && depth < chain.Length)
        {
            chain[depth++] = cur;
            cur = entries[cur].Parent;
        }
        var sb = _pathBuilder ??= new System.Text.StringBuilder(260);
        sb.Clear();
        for (int i = depth - 1; i >= 0; i--)
        {
            var name = entries[chain[i]].Name ?? string.Empty;
            if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
            sb.Append(name);
        }
        return sb.ToString();
    }

    public string ParentPathOf(int id)
    {
        int p = _entries[id].Parent;
        return p < 0 ? string.Empty : PathOf(p);
    }

    public int FindDirectory(string path)
    {
        lock (_lock) return _dirs.TryGetValue(NormalizeDir(path), out int id) ? id : -1;
    }

    public int FindChild(int parent, string name)
    {
        var entries = _entries; int count = _count;
        for (int i = 0; i < count; i++)
        {
            if (entries[i].Parent == parent && entries[i].Name is { } n && string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public int FindByPath(string fullPath)
    {
        fullPath = fullPath.TrimEnd('\\');
        int dir = FindDirectory(fullPath);
        if (dir >= 0) return dir;
        int sep = fullPath.LastIndexOf('\\');
        if (sep <= 0) return -1;
        string parentPath = sep == 2 && fullPath[1] == ':' ? fullPath[..3] : fullPath[..sep];
        int parent = FindDirectory(parentPath);
        if (parent < 0) return -1;
        return FindChild(parent, fullPath[(sep + 1)..]);
    }

    public int[] Children(int parent)
    {
        lock (_lock) return ChildrenNoLock(parent);
    }

    private int[] ChildrenNoLock(int parent)
    {
        if (_childrenCache.TryGetValue(parent, out var cached)) return cached;
        var list = new List<int>();
        var entries = _entries; int count = _count;
        for (int i = 0; i < count; i++)
            if (entries[i].Parent == parent && entries[i].Name is not null) list.Add(i);
        var arr = list.ToArray();
        if (_childrenCache.Count > 512) _childrenCache.Clear();
        _childrenCache[parent] = arr;
        return arr;
    }

    public bool IsDescendantOf(int id, int ancestor)
    {
        var entries = _entries;
        int cur = entries[id].Parent;
        int guard = 0;
        while (cur >= 0 && guard++ < 512)
        {
            if (cur == ancestor) return true;
            cur = entries[cur].Parent;
        }
        return false;
    }

    public IEnumerable<string> DirectoryPaths()
    {
        lock (_lock) return _dirs.Keys.ToArray();
    }

    public static string NormalizeDir(string path)
    {
        if (path.Length == 3 && path[1] == ':' && path[2] == '\\') return path.ToUpperInvariant();
        if (path.Length == 2 && path[1] == ':') return path.ToUpperInvariant() + "\\";
        return path.TrimEnd('\\');
    }

    /// <summary>Copies live entries into a new compacted index with remapped ids.</summary>
    public FileIndex Compact()
    {
        lock (_lock)
        {
            var map = new int[_count];
            var result = new FileIndex(LiveCount + 1024) { BuiltUtc = BuiltUtc };
            for (int i = 0; i < _count; i++)
            {
                ref var e = ref _entries[i];
                if (e.Name is null) { map[i] = -1; continue; }
                int parent = e.Parent < 0 ? -1 : map[e.Parent];
                if (e.Parent >= 0 && parent < 0) { map[i] = -1; continue; } // orphan
                if (parent < 0) map[i] = result.AddRoot(e.Name);
                else
                {
                    map[i] = result.AppendRaw(e.Name, parent, e.Size, e.Modified, e.Created, e.Attributes);
                    if (e.IsDirectory) result.RegisterDirectory(result.PathOf(map[i]), map[i]);
                }
            }
            return result;
        }
    }
}
