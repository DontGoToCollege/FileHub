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

    public string StatusText { get; private set; } = "Idle";
    public long Processed { get; private set; }
    public long Candidates { get; private set; }
    public bool IsRunning { get; private set; }
    public event Action? StatusChanged;

    public ContentIndexer(IndexService index, MetaDb db, AppSettings settings)
    {
        _index = index; _db = db; _settings = settings;
        _index.IndexChanged += () => RequestFullPass();
        _index.FileChanged += (path, isDir) => { if (!isDir) _changed.Writer.TryWrite(path); };
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Status(value ? "Paused" : "Idle");
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

    private void Status(string s) { StatusText = s; StatusChanged?.Invoke(); }

    private void Loop()
    {
        var ct = _cts.Token;
        var lastPass = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_paused) { Thread.Sleep(1000); continue; }
                if (!_settings.Index.ContentIndexing && !_settings.Index.MetadataIndexing) { Thread.Sleep(2000); continue; }

                // individual changed files first
                var changedBatch = new List<string>();
                while (changedBatch.Count < 200 && _changed.Reader.TryRead(out var p)) changedBatch.Add(p);
                if (changedBatch.Count > 0) { ProcessPaths(changedBatch.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct); continue; }

                if (_fullPassRequested || (DateTime.UtcNow - lastPass).TotalMinutes > 30)
                {
                    if (_index.State != IndexState.Ready) { Thread.Sleep(2000); continue; }
                    _fullPassRequested = false;
                    FullPass(ct);
                    lastPass = DateTime.UtcNow;
                    continue;
                }
                Thread.Sleep(1000);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Error("Content indexer loop error", ex); Thread.Sleep(5000); }
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

        var todo = new List<int>();
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
            todo.Add(i);
        }
        Candidates = todo.Count; Processed = 0;
        Status($"Indexing content 0 / {Candidates:N0}");

        var contentBatch = new List<(string, long, long, string)>();
        var propsBatch = new List<(string, long, string, string)>();
        int sinceReport = 0;
        foreach (int id in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (_paused || _fullPassRequested) break;
            ref var e = ref entries[id];
            if (e.Name is null) continue;
            string path;
            try { path = index.PathOf(id); } catch { continue; }
            string ext = FileTypes.ExtensionOf(e.Name);
            string stamp = path + "|" + e.Modified;
            bool wantContent = _settings.Index.ContentIndexing && contentExts.Contains(ext) && e.Size <= maxBytes && !haveContent.Contains(stamp);
            bool wantMeta = _settings.Index.MetadataIndexing && metaExts.Contains(ext) && !haveProps.Contains(stamp);
            if (wantContent)
            {
                string body = ExtractText(path, ext, maxBytes);
                contentBatch.Add((path, e.Modified, e.Size, body));
            }
            if (wantMeta)
            {
                var props = PropertyReader.Read(path);
                if (props.Count > 0)
                {
                    string json = JsonSerializer.Serialize(props);
                    string text = string.Join(" \n", props.Where(kv => kv.Key is not ("Width" or "Height" or "FrameWidth" or "FrameHeight" or "Bitrate")).Select(kv => kv.Value));
                    propsBatch.Add((path, e.Modified, json, text));
                }
                else propsBatch.Add((path, e.Modified, "{}", ""));
            }
            Processed++;
            if (contentBatch.Count >= 100) { _db.UpsertContentBatch(contentBatch); contentBatch.Clear(); }
            if (propsBatch.Count >= 200) { _db.UpsertPropsBatch(propsBatch); propsBatch.Clear(); }
            if (++sinceReport >= 250) { sinceReport = 0; Status($"Indexing content {Processed:N0} / {Candidates:N0}"); Thread.Sleep(5); }
        }
        if (contentBatch.Count > 0) _db.UpsertContentBatch(contentBatch);
        if (propsBatch.Count > 0) _db.UpsertPropsBatch(propsBatch);
        IsRunning = false;
        if (_paused) { Status($"Paused at {Processed:N0} / {Candidates:N0}"); return; }
        var stats = _db.Stats();
        Status($"Content index up to date ({stats.contentRows:N0} documents, {stats.propsRows:N0} with metadata)");
    }

    private void ProcessPaths(List<string> paths, CancellationToken ct)
    {
        var contentExts = new HashSet<string>(_settings.Index.ContentExtensions, StringComparer.OrdinalIgnoreCase);
        var metaExts = new HashSet<string>(_settings.Index.MetadataExtensions, StringComparer.OrdinalIgnoreCase);
        long maxBytes = Math.Max(1, _settings.Index.ContentMaxMB) * 1024L * 1024L;
        var contentBatch = new List<(string, long, long, string)>();
        var propsBatch = new List<(string, long, string, string)>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) { _db.RemovePath(path); continue; }
                var fi = new FileInfo(path);
                string ext = FileTypes.ExtensionOf(fi.Name);
                if (_settings.Index.ContentIndexing && contentExts.Contains(ext) && fi.Length <= maxBytes)
                    contentBatch.Add((path, fi.LastWriteTimeUtc.Ticks, fi.Length, ExtractText(path, ext, maxBytes)));
                if (_settings.Index.MetadataIndexing && metaExts.Contains(ext))
                {
                    var props = PropertyReader.Read(path);
                    propsBatch.Add((path, fi.LastWriteTimeUtc.Ticks, JsonSerializer.Serialize(props), string.Join(" \n", props.Values)));
                }
            }
            catch (Exception ex) { Log.Warn("Re-index failed for " + path + ": " + ex.Message); }
        }
        if (contentBatch.Count > 0) _db.UpsertContentBatch(contentBatch);
        if (propsBatch.Count > 0) _db.UpsertPropsBatch(propsBatch);
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
