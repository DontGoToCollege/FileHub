using System.IO;
using System.IO.Enumeration;
using FileHub.Platform;

namespace FileHub.Core.Indexing;

public sealed record IndexProgress(string Root, long Entries, bool Done);

/// <summary>
/// Crawls volumes with the Win32 FindFirstFileEx-backed FileSystemEnumerator (large fetch, no extra
/// stat calls). Volumes are crawled in parallel, each in breadth-first order so results naturally
/// group by folder; per-volume indexes are then merged into one.
/// </summary>
public static class IndexBuilder
{
    public static FileIndex Build(IReadOnlyList<string> roots, IReadOnlyList<string> excludes, IProgress<IndexProgress>? progress, CancellationToken ct, ManualResetEventSlim? runGate = null)
    {
        var partials = new FileIndex[roots.Count];
        var excludeNorm = excludes.Select(e => e.Trim().TrimEnd('\\')).Where(e => e.Length > 0).ToArray();

        Parallel.For(0, roots.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Math.Min(roots.Count, Environment.ProcessorCount)) }, i =>
        {
            partials[i] = CrawlVolume(roots[i], excludeNorm, progress, ct, runGate);
        });

        ct.ThrowIfCancellationRequested();

        // merge
        long total = partials.Sum(p => (long)p.Count);
        var merged = new FileIndex((int)Math.Min(int.MaxValue - 1024, total + 1024)) { BuiltUtc = DateTime.UtcNow };
        foreach (var part in partials)
        {
            if (part is null) continue;
            var map = new int[part.Count];
            var entries = part.Entries;
            for (int i = 0; i < part.Count; i++)
            {
                ref var e = ref entries[i];
                if (e.Name is null) { map[i] = -1; continue; }
                if (e.Parent < 0) map[i] = merged.AddRoot(e.Name);
                else
                {
                    int parent = map[e.Parent];
                    map[i] = merged.AppendRaw(e.Name, parent, e.Size, e.Modified, e.Created, e.Attributes);
                    if (e.IsDirectory) merged.RegisterDirectory(merged.PathOf(map[i]), map[i]);
                }
            }
        }
        return merged;
    }

    public static FileIndex CrawlVolume(string root, IReadOnlyList<string> excludes, IProgress<IndexProgress>? progress, CancellationToken ct, ManualResetEventSlim? runGate = null)
    {
        var index = new FileIndex(1 << 18);
        string rootNorm = FileIndex.NormalizeDir(root);
        int rootId = index.AddRoot(rootNorm);
        try
        {
            using var crawler = new Crawler(rootNorm, index, rootId, excludes, progress, ct) { RunGate = runGate };
            while (crawler.MoveNext()) { }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error("Crawl failed for " + root, ex); }
        progress?.Report(new IndexProgress(root, index.Count, true));
        return index;
    }

    /// <summary>Crawls a directory subtree into an existing index under the given parent id (used for watcher "folder appeared" events).</summary>
    public static void CrawlInto(FileIndex index, string directory, int dirId, IReadOnlyList<string> excludes, CancellationToken ct)
    {
        try
        {
            using var crawler = new Crawler(FileIndex.NormalizeDir(directory), index, dirId, excludes, null, ct);
            while (crawler.MoveNext()) { }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("CrawlInto failed for " + directory + ": " + ex.Message); }
    }

    private sealed class Crawler : FileSystemEnumerator<int>
    {
        private readonly FileIndex _index;
        private readonly IReadOnlyList<string> _excludes;
        private readonly IProgress<IndexProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly string _root;
        private string _curDir;
        private int _curDirId;
        private long _count;
        /// <summary>When reset (indexing paused) the crawl blocks here until it is set again.</summary>
        public ManualResetEventSlim? RunGate { get; init; }

        private static readonly EnumerationOptions Options = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            BufferSize = 256 * 1024,
        };

        public Crawler(string root, FileIndex index, int rootId, IReadOnlyList<string> excludes, IProgress<IndexProgress>? progress, CancellationToken ct)
            : base(root, Options)
        {
            _root = root; _index = index; _excludes = excludes; _progress = progress; _ct = ct;
            _curDir = root; _curDirId = rootId;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) => true;

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false; // avoid junction loops
            if (_excludes.Count > 0)
            {
                var full = entry.ToFullPath();
                foreach (var ex in _excludes)
                    if (full.Equals(ex, StringComparison.OrdinalIgnoreCase) || (ex.Contains('\\') == false && entry.FileName.Equals(ex, StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }

        protected override bool ContinueOnError(int error) => true;

        protected override int TransformEntry(ref FileSystemEntry entry)
        {
            if ((++_count & 0x3FF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
                RunGate?.Wait(_ct);
                if ((_count & 0x3FFF) == 0) _progress?.Report(new IndexProgress(_root, _count, false));
            }
            var dir = entry.Directory;
            if (!dir.Equals(_curDir.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                _curDir = dir.ToString();
                int id = _index.FindDirectory(_curDir);
                if (id < 0)
                {
                    // directory was skipped (excluded) or unknown: register on the fly
                    id = _curDirId;
                }
                _curDirId = id;
            }
            long size = entry.IsDirectory ? 0 : entry.Length;
            int newId = _index.AppendRaw(entry.FileName.ToString(), _curDirId, size, entry.LastWriteTimeUtc.UtcTicks, entry.CreationTimeUtc.UtcTicks, entry.Attributes);
            if (entry.IsDirectory)
            {
                _index.RegisterDirectory(entry.ToFullPath(), newId);
            }
            return newId;
        }
    }
}
