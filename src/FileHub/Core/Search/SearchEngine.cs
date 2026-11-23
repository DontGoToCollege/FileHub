using System.IO;
using System.Diagnostics;
using FileHub.Core.Database;
using FileHub.Core.Indexing;

namespace FileHub.Core.Search;

public sealed class SearchResult
{
    public required FileIndex Index { get; init; }
    public required int[] Ids { get; init; }
    public int TotalMatches { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
    /// <summary>The query searched file contents and the ids are in best-match order (your documents first).</summary>
    public bool Ranked { get; init; }
    /// <summary>Where the content-only matches added by IncludeContent start in Ids (Ids.Length when there are none).</summary>
    public int ContentFrom { get; init; }
    public bool Truncated => Ids.Length < TotalMatches;
}

/// <summary>Runs parsed queries over the in-memory index in parallel, then sorts/caps.</summary>
public sealed class SearchEngine
{
    private readonly IndexService _indexService;
    private readonly MetaDb _db;

    public SearchEngine(IndexService indexService, MetaDb db)
    {
        _indexService = indexService; _db = db;
    }

    private static readonly string[] UserPlaces = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Environment.GetEnvironmentVariable("OneDrive") ?? "",
    }.Where(p => p.Length > 0).Select(p => p.TrimEnd('\\') + "\\").ToArray();

    private static readonly string[] SystemPlaces = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    }.Where(p => p.Length > 0).Select(p => p.TrimEnd('\\') + "\\").ToArray();

    private static readonly string[] CodeLibraryParts = { "\\node_modules\\", "\\site-packages\\", "\\venv\\", "\\.venv\\", "\\.git\\", "\\bin\\", "\\obj\\", "\\$Recycle.Bin\\" };

    /// <summary>0 = your folders (Desktop, Documents…), 1 = elsewhere, 2 = Windows / programs / app data / code libraries.</summary>
    internal static int PlaceTier(string path)
    {
        if (CodeLibraryParts.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase))) return 2;
        if (SystemPlaces.Any(s => path.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return 2;
        if (UserPlaces.Any(s => path.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return 0;
        return 1;
    }

    public SearchResult Search(string text, SearchOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var index = _indexService.Index;
        options.ContentSearch ??= (q, limit) => _db.SearchContent(q, limit);
        var parsed = QueryParser.Parse(text, options, index);
        var root = parsed.Root;

        int count = index.Count;
        var entries = index.Entries;
        int workers = Math.Clamp(Environment.ProcessorCount, 1, 32);
        int chunk = Math.Max(4096, (count + workers - 1) / workers);
        var buckets = new List<int>[(count + chunk - 1) / chunk + 1];
        bool matchAll = root.IsMatchAll;

        try
        {
            Parallel.For(0, buckets.Length, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers }, b =>
            {
                int start = b * chunk, end = Math.Min(count, start + chunk);
                if (start >= end) { buckets[b] = new List<int>(0); return; }
                var list = new List<int>(matchAll ? end - start : 256);
                string? path = null;
                for (int i = start; i < end; i++)
                {
                    ref var e = ref entries[i];
                    if (e.Name is null) continue;
                    path = null;
                    if (matchAll || root.Match(index, i, ref e, ref path)) list.Add(i);
                    if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                }
                buckets[b] = list;
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (AggregateException ex) when (ex.InnerExceptions.Any(x => x is OperationCanceledException)) { throw new OperationCanceledException(); }

        int total = 0;
        foreach (var b in buckets) total += b?.Count ?? 0;
        var all = new int[total];
        int pos = 0;
        foreach (var b in buckets) { if (b == null) continue; b.CopyTo(all, pos); pos += b.Count; }

        ct.ThrowIfCancellationRequested();

        if (options.Sort != SortKey.None && all.Length > 1)
        {
            var cmp = MakeComparer(index, options.Sort, options.SortDescending);
            // sorting very large result sets by path would be costly; cap the sort work
            if (all.Length > 400_000 && (options.Sort == SortKey.Path))
                Array.Sort(all, 0, all.Length, Comparer<int>.Create((a, b) => a.CompareTo(b)));
            else
                Array.Sort(all, cmp);
        }
        int nameHits = all.Length;
        if (options.Sort == SortKey.None && parsed.ContentRank is { Count: > 0 } rank && all.Length > 0)
        {
            // no sort chosen: content searches list the best matches first, not in disk order
            // your own documents before program files and code libraries that happen to mention the word
            // with IncludeContent, name matches stay first in disk order and the content-only matches follow
            var nameRoot = parsed.NameRoot;
            var key = new long[all.Length];
            nameHits = 0;
            for (int k = 0; k < all.Length; k++)
            {
                string? p = null;
                if (nameRoot != null && nameRoot.Match(index, all[k], ref entries[all[k]], ref p)) { key[k] = k; nameHits++; continue; }
                p ??= index.PathOf(all[k]);
                // within each place: documents (txt, md, docx, pdf…) before source code
                int group = PlaceTier(p) * 2 + (FileTypes.IsIn("document", FileTypes.ExtensionOf(p)) ? 0 : 1);
                key[k] = 1L << 40 | (long)group << 32 | (uint)(rank.TryGetValue(p, out int r) ? r : int.MaxValue);
            }
            if (nameRoot == null) nameHits = all.Length;
            Array.Sort(key, all);
        }

        var ids = all.Length > options.MaxResults ? all[..options.MaxResults] : all;
        return new SearchResult { Index = index, Ids = ids, TotalMatches = total, Elapsed = sw.Elapsed, Error = parsed.Error,
            Ranked = options.Sort == SortKey.None && parsed.ContentRank is { Count: > 0 } && parsed.NameRoot == null,
            ContentFrom = Math.Min(nameHits, ids.Length) };
    }

    public static IComparer<int> MakeComparer(FileIndex index, SortKey key, bool desc)
    {
        // a file deleted by the live indexer mid-sort has a null name; compare it as "" instead of crashing
        var entries = index.Entries;
        int sign = desc ? -1 : 1;
        Comparison<int> c = key switch
        {
            SortKey.Name => (a, b) => CompareNames(entries[a].Name ?? "", entries[b].Name ?? ""),
            SortKey.Size => (a, b) => entries[a].Size.CompareTo(entries[b].Size),
            SortKey.Modified => (a, b) => entries[a].Modified.CompareTo(entries[b].Modified),
            SortKey.Created => (a, b) => entries[a].Created.CompareTo(entries[b].Created),
            SortKey.Extension => (a, b) => string.Compare(FileTypes.ExtensionOf(entries[a].Name ?? ""), FileTypes.ExtensionOf(entries[b].Name ?? ""), StringComparison.OrdinalIgnoreCase),
            SortKey.Type => (a, b) =>
            {
                int d = entries[b].IsDirectory.CompareTo(entries[a].IsDirectory);
                return d != 0 ? d : string.Compare(FileTypes.ExtensionOf(entries[a].Name ?? ""), FileTypes.ExtensionOf(entries[b].Name ?? ""), StringComparison.OrdinalIgnoreCase);
            },
            SortKey.Path => (a, b) => string.Compare(index.PathOf(a), index.PathOf(b), StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => a.CompareTo(b),
        };
        return Comparer<int>.Create((a, b) => { int r = c(a, b); return r != 0 ? sign * r : a.CompareTo(b); });
    }

    /// <summary>Natural ("logical") string comparison like Explorer: file2 &lt; file10.</summary>
    public static int CompareNames(string a, string b)
    {
        int ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            char ca = a[ia], cb = b[ib];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                int sa = ia, sb = ib;
                while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                while (ib < b.Length && char.IsDigit(b[ib])) ib++;
                var na = a.AsSpan(sa, ia - sa).TrimStart('0');
                var nb = b.AsSpan(sb, ib - sb).TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int r = na.SequenceCompareTo(nb);
                if (r != 0) return r;
                continue;
            }
            int cr = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
            if (cr != 0) return cr;
            ia++; ib++;
        }
        return (a.Length - ia).CompareTo(b.Length - ib);
    }

    /// <summary>Ranks a capped result set for the Start-menu style quick search: apps and exact/prefix name matches first.</summary>
    public static IEnumerable<int> RankForQuickSearch(SearchResult result, string query, int take)
    {
        var index = result.Index;
        var entries = index.Entries;
        string q = query.Trim();
        var scored = new List<(int id, int score)>(Math.Min(result.Ids.Length, 20000));
        int limit = Math.Min(result.ContentFrom, 20000);   // name matches only; content-only matches are merged in by the caller
        for (int i = 0; i < limit; i++)
        {
            int id = result.Ids[i];
            ref var e = ref entries[id];
            if (e.Name is not string name) continue; // deleted since the search ran
            int score = 0;
            string ext = FileTypes.ExtensionOf(name);
            bool isApp = ext.Equals("lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals("exe", StringComparison.OrdinalIgnoreCase) || ext.Equals("url", StringComparison.OrdinalIgnoreCase);
            string stem = ext.Length > 0 ? name[..^(ext.Length + 1)] : name;
            if (stem.Equals(q, StringComparison.OrdinalIgnoreCase)) score += 1000;
            else if (stem.StartsWith(q, StringComparison.OrdinalIgnoreCase)) score += 600;
            else if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 200;
            if (isApp)
            {
                score += 300;
                string path = index.PathOf(id);
                if (path.Contains("\\Start Menu\\", StringComparison.OrdinalIgnoreCase)) score += 500;
                if (path.Contains("\\Desktop\\", StringComparison.OrdinalIgnoreCase)) score += 200;
            }
            if (e.IsDirectory) score += 100;
            score -= Math.Min(90, name.Length / 2);
            scored.Add((id, score));
        }
        return scored.OrderByDescending(s => s.score).ThenBy(s => s.id).Select(s => s.id).Take(take);
    }
}
