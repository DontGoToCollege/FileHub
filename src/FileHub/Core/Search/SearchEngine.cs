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

        if (options.Sort == SortKey.Name && all.Length > 20_000)
            all = SortByName(index, all, options.SortDescending, options.MaxResults);
        else if (options.Sort != SortKey.None && all.Length > 1)
        {
            var cmp = MakeComparer(index, options.Sort, options.SortDescending);
            if (all.Length > options.MaxResults * 8)   // below that a plain sort is as quick
                all = SmallestSorted(all, cmp, options.MaxResults);
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
            // on every core: a path and its place for each of ~10,000 content matches took 15-25 ms on one
            Parallel.For(0, all.Length, k =>
            {
                string? p = null;
                if (nameRoot != null && nameRoot.Match(index, all[k], ref entries[all[k]], ref p)) { key[k] = k; return; }
                p ??= index.PathOf(all[k]);
                // within each place: documents (txt, md, docx, pdf…) before source code
                int group = PlaceTier(p) * 2 + (FileTypes.IsIn("document", FileTypes.ExtensionOf(p)) ? 0 : 1);
                key[k] = 1L << 40 | (long)group << 32 | (uint)(rank.TryGetValue(p, out int r) ? r : int.MaxValue);
            });
            nameHits = nameRoot == null ? all.Length : key.Count(x => x < 1L << 40);
            Array.Sort(key, all);
        }

        var ids = all.Length > options.MaxResults ? all[..options.MaxResults] : all;
        return new SearchResult { Index = index, Ids = ids, TotalMatches = total, Elapsed = sw.Elapsed, Error = parsed.Error,
            Ranked = options.Sort == SortKey.None && parsed.ContentRank is { Count: > 0 } && parsed.NameRoot == null,
            ContentFrom = Math.Min(nameHits, ids.Length) };
    }

    /// <summary>
    /// The first <paramref name="k"/> of <paramref name="items"/> in sorted order, without sorting the rest (only those are
    /// shown): each core keeps the k smallest of its share, then the shares are merged. 1.1M by name: 7 s before.
    /// Shuffles <paramref name="items"/> in place.
    /// </summary>
    public static T[] SmallestSorted<T>(T[] items, IComparer<T> cmp, int k)
    {
        // results come in index order, where neighbours have similar names and sizes, so a share would keep replacing its
        // kept k; in random order nearly every item loses to the root in one comparison (785k by name: 275 → ~115 ms)
        Random.Shared.Shuffle(items);
        int parts = Math.Clamp(items.Length / 50_000, 1, Environment.ProcessorCount);
        var shares = new T[parts][];
        Parallel.For(0, parts, p => shares[p] = HeapTop(items.AsSpan(items.Length * p / parts, items.Length * (p + 1) / parts - items.Length * p / parts).ToArray(), cmp, k));
        // k-way merge of the sorted shares
        var next = new PriorityQueue<(int share, int pos), T>(parts, cmp);
        for (int p = 0; p < parts; p++) if (shares[p].Length > 0) next.Enqueue((p, 0), shares[p][0]);
        var top = new T[Math.Min(k, shares.Sum(s => s.Length))];
        for (int i = 0; i < top.Length; i++)
        {
            next.TryDequeue(out var at, out top[i]);
            if (at.pos + 1 < shares[at.share].Length) next.Enqueue((at.share, at.pos + 1), shares[at.share][at.pos + 1]);
        }
        return top;
    }

    private static T[] HeapTop<T>(T[] items, IComparer<T> cmp, int k)
    {
        // max-heap of the k smallest so far: its root is the one to beat, and most items lose to it in one comparison
        var heap = new PriorityQueue<T, T>(k, Comparer<T>.Create((a, b) => cmp.Compare(b, a)));
        foreach (var item in items)
        {
            if (heap.Count < k) heap.Enqueue(item, item);
            else if (cmp.Compare(item, heap.Peek()) < 0) heap.DequeueEnqueue(item, item);
        }
        var top = new T[heap.Count];
        for (int i = top.Length - 1; i >= 0; i--) top[i] = heap.Dequeue();
        return top;
    }

    /// <summary>
    /// Name sort of a big result set: comparing two names mostly waits on memory (two strings scattered among a million),
    /// so each result carries its name's first 8 letters packed in a number, side by side, and the names themselves are
    /// read only when those can't tell. Same order as <see cref="MakeComparer"/>.
    /// </summary>
    private static int[] SortByName(FileIndex index, int[] ids, bool desc, int max)
    {
        var entries = index.Entries;
        var keyed = new (ulong key, int id)[ids.Length];
        Parallel.For(0, ids.Length, i => keyed[i] = (NameKey(entries[ids[i]].Name ?? ""), ids[i]));
        int sign = desc ? -1 : 1;
        var cmp = Comparer<(ulong key, int id)>.Create((a, b) =>
        {
            int r = CompareNameKeys(a.key, b.key);
            if (r == 0) r = CompareNames(entries[a.id].Name ?? "", entries[b.id].Name ?? "");
            return r != 0 ? sign * r : a.id.CompareTo(b.id);
        });
        if (ids.Length > max * 8) keyed = SmallestSorted(keyed, cmp, max); else Array.Sort(keyed, cmp);
        return Array.ConvertAll(keyed, x => x.id);
    }

    /// <summary>
    /// A name's first 8 characters as bytes, first in the top byte, so that keys order like <see cref="CompareNames"/>:
    /// letters upper-cased, 0 where the name ends, and the key stops at the first digit ('0': a number's value needs the
    /// whole name) or non-ASCII character: 0xFE, after every ASCII one (é, 한, Ж…), or 0xFF "can't tell" for the few
    /// that upper-case to ASCII (ı → I) or are digits (٣).
    /// </summary>
    public static ulong NameKey(string s)
    {
        ulong key = 0;
        for (int i = 0; i < 8; i++)
        {
            int b = i >= s.Length ? 0
                : s[i] < 128 ? (char.IsAsciiDigit(s[i]) ? '0' : char.ToUpperInvariant(s[i]))
                : char.IsDigit(s[i]) || char.ToUpperInvariant(s[i]) < 128 ? 0xFF : 0xFE;
            key |= (ulong)b << (56 - 8 * i);
            if (b is 0 or 0xFE or 0xFF or '0') break;
        }
        return key;
    }

    /// <summary>Order of two <see cref="NameKey"/>s, or 0 when equal or when they differ at a "can't tell" byte.</summary>
    public static int CompareNameKeys(ulong x, ulong y)
    {
        if (x == y) return 0;
        int shift = 56 - (System.Numerics.BitOperations.LeadingZeroCount(x ^ y) & ~7);
        if ((byte)(x >> shift) == 0xFF || (byte)(y >> shift) == 0xFF) return 0;
        return x < y ? -1 : 1;
    }

    /// <summary>
    /// Path order, folder by folder (a folder's files stay together), by walking both parent chains instead of
    /// building both paths for every comparison: sorting 100k results by path took 2.8 s.
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]   // the chains are written before they are read; zeroing 4 KB per comparison was a third of its time
    private static int ComparePaths(Entry[] entries, int a, int b)
    {
        Span<int> ca = stackalloc int[512], cb = stackalloc int[512];
        int na = 0, nb = 0;
        for (int x = a; x >= 0 && na < ca.Length; x = entries[x].Parent) ca[na++] = x;
        for (int y = b; y >= 0 && nb < cb.Length; y = entries[y].Parent) cb[nb++] = y;
        for (int i = 1; i <= Math.Min(na, nb); i++)   // chains are leaf first: compare from the drive down
        {
            int x = ca[na - i], y = cb[nb - i];
            if (x != y) return string.Compare(entries[x].Name, entries[y].Name, StringComparison.OrdinalIgnoreCase);
        }
        return na.CompareTo(nb);   // a folder before what is inside it
    }

    /// <summary><see cref="FileTypes.ExtensionOf"/> as a view into the name: sorting by type no longer copies two extensions per comparison.</summary>
    private static ReadOnlySpan<char> Ext(string? name)
    {
        if (name is null) return default;
        int dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? default : name.AsSpan(dot + 1);
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
            SortKey.Extension => (a, b) => Ext(entries[a].Name).CompareTo(Ext(entries[b].Name), StringComparison.OrdinalIgnoreCase),
            SortKey.Type => (a, b) =>
            {
                int d = entries[b].IsDirectory.CompareTo(entries[a].IsDirectory);
                return d != 0 ? d : Ext(entries[a].Name).CompareTo(Ext(entries[b].Name), StringComparison.OrdinalIgnoreCase);
            },
            SortKey.Path => (a, b) => ComparePaths(entries, a, b),
            _ => (a, b) => a.CompareTo(b),
        };
        return Comparer<int>.Create((a, b) => { int r = c(a, b); return r != 0 ? sign * r : a.CompareTo(b); });
    }

    /// <summary>Natural ("logical") string comparison like Explorer: file2 &lt; file10.</summary>
    public static int CompareNames(string a, string b)
    {
        // skip the identical start in one vectorised step (sibling names often share 50+ characters), backing up to the
        // start of a number it cuts through so "file9" vs "file10" still compare by value. Sorting 81k names: 2 s → 0.3 s
        int ia = a.AsSpan().CommonPrefixLength(b);
        while (ia > 0 && char.IsDigit(a[ia - 1])) ia--;
        int ib = ia;
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
