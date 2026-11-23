using System.IO;
using System.Text;
using FileHub.Platform;
using Microsoft.Data.Sqlite;

namespace FileHub.Core.Database;

/// <summary>
/// SQLite database (meta.db) holding everything the flat name index does not: full-text content,
/// extracted file properties/metadata, thumbnail cache, per-folder view settings and misc key/values.
/// </summary>
public sealed class MetaDb : IDisposable
{
    private readonly string _connString;
    private readonly string _file;
    private readonly object _writeLock = new();

    public MetaDb(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _file = file;
        _connString = new SqliteConnectionStringBuilder { DataSource = file, Cache = SqliteCacheMode.Private, Pooling = true /* private cache + WAL: readers never wait on the content indexer's writes */ }.ToString();
        try { Init(); }
        catch (Exception ex) when (IsCorrupt(ex)) { Recover(); }
    }

    /// <summary>SQLite's "malformed" (11) or "not a database" (26): the file is damaged, and only a fresh one helps.</summary>
    public static bool IsCorrupt(Exception ex) => ex is SqliteException { SqliteErrorCode: 11 or 26 };

    /// <summary>Set when a search or write hits a damaged file; the content indexer then calls <see cref="Recover"/>.</summary>
    public volatile bool RecoverRequested;
    private int _recoveries;

    /// <summary>
    /// Replaces a damaged meta.db with a fresh one, with no action from the user. Everything in it can be rebuilt (the
    /// content indexer re-reads the files, thumbnails are made again) except folder views and saved values, which are
    /// carried over when they can still be read. Once per run, so a damage that keeps coming back can't loop re-indexing.
    /// </summary>
    public void Recover()
    {
        lock (_writeLock)
        {
            if (_recoveries++ > 0) { RecoverRequested = false; Log.Warn("meta.db is damaged again; not replacing it twice in one run"); return; }
            Log.Warn("meta.db is damaged ('malformed'); replacing it with a fresh one and re-indexing file contents");
            var views = new List<(string, string)>(); var values = new List<(string, string)>();
            void Salvage(string sql, List<(string, string)> into)
            {
                try
                {
                    using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) if (!r.IsDBNull(0) && !r.IsDBNull(1)) into.Add((r.GetString(0), r.GetString(1)));
                }
                catch (Exception ex) { Log.Warn("Could not carry over from the damaged meta.db: " + ex.Message); }
            }
            Salvage("SELECT path, json FROM folder_views", views);
            Salvage("SELECT key, value FROM kv WHERE key <> 'fts_v2_cursor'", values);
            SqliteConnection.ClearAllPools();
            // the WAL first: a fresh file must never meet the old file's WAL
            foreach (var f in new[] { _file + "-wal", _file + "-shm", _file })
                for (int i = 0; ; i++)
                {
                    try { File.Delete(f); break; }
                    catch (IOException) when (i < 40) { Thread.Sleep(250); SqliteConnection.ClearAllPools(); }   // a search may still have it open
                }
            Init();
            using (var c = Open())
            using (var tx = c.BeginTransaction())
            {
                foreach (var (p, j) in views) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT OR REPLACE INTO folder_views(path, json) VALUES($p,$j)"; cmd.Parameters.AddWithValue("$p", p); cmd.Parameters.AddWithValue("$j", j); cmd.ExecuteNonQuery(); }
                foreach (var (k, v) in values) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT OR REPLACE INTO kv(key, value) VALUES($k,$v)"; cmd.Parameters.AddWithValue("$k", k); cmd.Parameters.AddWithValue("$v", v); cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            RecoverRequested = false;
            Log.Info($"Fresh meta.db ready ({views.Count} folder views and {values.Count} saved values carried over); file contents are being re-indexed");
        }
    }

    private void Init()
    {
        using var c = Open();
        // auto_vacuum only takes on a new file; older ones are switched by Maintain's one-time VACUUM
        Exec(c, "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        bool fresh = Scalar(c, "SELECT count(*) FROM sqlite_master WHERE name='content_fts'") == 0;
        Exec(c, $"""
            CREATE TABLE IF NOT EXISTS content_meta(path TEXT PRIMARY KEY, modified INTEGER NOT NULL, size INTEGER NOT NULL, indexed INTEGER NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING {ContentFtsDef};
            CREATE TABLE IF NOT EXISTS props(path TEXT PRIMARY KEY, modified INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS props_fts USING fts5(path UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
            CREATE TABLE IF NOT EXISTS thumbs(key TEXT PRIMARY KEY, modified INTEGER NOT NULL, png BLOB NOT NULL, accessed INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS folder_views(path TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT);
            """);
        if (fresh) Exec(c, "PRAGMA user_version = 2");
        else MigrateFtsToRowids(c);
    }

    /// <summary>
    /// Schema 2 content table: only the search index (FileHub never reads the text back; that stored copy was most of
    /// the 5 GB database) plus prefix indexes for 3- and 4-letter word starts, so as-you-type searches like "con" don't
    /// expand into every word that starts that way. contentless_delete keeps rowid DELETEs working.
    /// </summary>
    private const string ContentFtsDef = "fts5(body, content='', contentless_delete=1, prefix='3 4', tokenize='unicode61 remove_diacritics 2')";

    private static long Scalar(SqliteConnection c, string sql, SqliteTransaction? tx = null, long cursor = 0)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx; cmd.CommandText = sql; cmd.Parameters.AddWithValue("$c", cursor);
        return cmd.ExecuteScalar() is long v ? v : -1;
    }

    /// <summary>
    /// One-time copy of a schema-1 content table into the schema-2 one, in short batches (writes elsewhere wait a
    /// second at most; searches keep using the old table until the swap). Resumes where it stopped after a restart.
    /// Runs on the content indexer's thread, the only writer of the content tables.
    /// </summary>
    public void UpgradeContentFts(CancellationToken ct)
    {
        using (var c = Open()) if (Scalar(c, "PRAGMA user_version") >= 2) return;
        Log.Info("Upgrading the content index to schema 2 (one time, in the background; search keeps working)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long copied = 0;
        while (!ct.IsCancellationRequested)
        {
            lock (_writeLock)
            {
                using var c = Open();
                if (Scalar(c, "PRAGMA user_version") >= 2) return;   // ClearContent finished it meanwhile
                using var tx = c.BeginTransaction();
                Exec(c, $"CREATE VIRTUAL TABLE IF NOT EXISTS content_fts_v2 USING {ContentFtsDef}", tx);
                long cursor = Math.Max(0, Scalar(c, "SELECT CAST(value AS INTEGER) FROM kv WHERE key='fts_v2_cursor'", tx));
                long next = Scalar(c, "SELECT max(r) FROM (SELECT rowid AS r FROM content_fts WHERE rowid > $c ORDER BY rowid LIMIT 500)", tx, cursor);
                if (next < 0)
                {
                    Exec(c, "DROP TABLE content_fts; ALTER TABLE content_fts_v2 RENAME TO content_fts; DELETE FROM kv WHERE key='fts_v2_cursor'; PRAGMA user_version = 2;", tx);
                    tx.Commit();
                    Log.Info($"Content index upgraded: {copied:N0} documents copied in {sw.Elapsed.TotalSeconds:F0} s this run");
                    return;
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO content_fts_v2(rowid, body) SELECT rowid, body FROM content_fts WHERE rowid > $c AND rowid <= $n; INSERT OR REPLACE INTO kv(key, value) VALUES('fts_v2_cursor', $n);";
                    cmd.Parameters.AddWithValue("$c", cursor); cmd.Parameters.AddWithValue("$n", next);
                    copied += cmd.ExecuteNonQuery() - 1;   // minus the cursor row
                }
                tx.Commit();
            }
        }
    }

    /// <summary>
    /// Keeps meta.db small and its search index tidy, with no action from the user: the content indexer runs it after
    /// each pass (every 30 min). A database from before 0.3.17 is switched once to incremental auto-vacuum (one VACUUM,
    /// seconds here; searches keep reading meanwhile). Each run then merges the FTS segments into one when more than
    /// 10 have built up (fewer segments = faster searches) and hands free pages back to the disk, every step a short
    /// write so thumbnails and folder views never wait long.
    /// </summary>
    public void Maintain(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long sizeBefore = new FileInfo(_file).Length;
        using var c = Open();
        if (Scalar(c, "PRAGMA auto_vacuum") != 2)
        {
            Log.Info($"Compacting meta.db ({sizeBefore / 1e9:F2} GB, one time)");
            // temp_store=FILE: VACUUM builds its copy in the temp store, which must not be RAM for a multi-GB file
            lock (_writeLock) Exec(c, "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA temp_store=FILE; VACUUM; PRAGMA temp_store=MEMORY;");
        }
        foreach (var t in new[] { "content_fts", "props_fts" })
        {
            if (Scalar(c, $"SELECT count(DISTINCT segid) FROM {t}_idx") <= 10) continue;
            // a negative merge step works towards a single segment (like 'optimize') but only ~2,000 pages at a time
            for (long changed = 2; changed >= 2 && !ct.IsCancellationRequested;)
                lock (_writeLock)
                {
                    long before = Scalar(c, "SELECT total_changes()");
                    Exec(c, $"INSERT INTO {t}({t}, rank) VALUES('merge', -2000)");
                    changed = Scalar(c, "SELECT total_changes()") - before;
                }
        }
        // keep up to 16 MB of free pages for upcoming writes; give the rest back 8 MB per step
        while (!ct.IsCancellationRequested && Scalar(c, "PRAGMA freelist_count") > 4096)
            lock (_writeLock) Exec(c, "PRAGMA incremental_vacuum(2048)");
        lock (_writeLock) Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)");   // a VACUUM or big merge leaves a WAL as large as what it wrote
        long sizeAfter = new FileInfo(_file).Length;
        if (sizeBefore - sizeAfter > 16 << 20 || sw.ElapsedMilliseconds > 2000)
            Log.Info($"meta.db upkeep: {sizeBefore / 1e9:F2} → {sizeAfter / 1e9:F2} GB in {sw.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>
    /// Schema 1: each FTS row's rowid equals its content_meta / props rowid, so replacing or removing a file's
    /// text is a rowid lookup. Before, rows were found by the UNINDEXED path column: a full scan of every
    /// stored document per file, which made a full content pass take days. Existing rows are carried over.
    /// </summary>
    private static void MigrateFtsToRowids(SqliteConnection c)
    {
        using (var v = c.CreateCommand()) { v.CommandText = "PRAGMA user_version"; if (Convert.ToInt64(v.ExecuteScalar()) >= 1) return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        // CROSS JOIN keeps the FTS table as the outer loop (one scan); the meta side is a primary-key lookup
        cmd.CommandText = """
            CREATE VIRTUAL TABLE content_fts_new USING fts5(path UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
            INSERT INTO content_fts_new(rowid, path, body) SELECT m.rowid, f.path, f.body FROM content_fts f CROSS JOIN content_meta m ON m.path = f.path GROUP BY m.rowid;
            DROP TABLE content_fts;
            ALTER TABLE content_fts_new RENAME TO content_fts;
            CREATE VIRTUAL TABLE props_fts_new USING fts5(path UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
            INSERT INTO props_fts_new(rowid, path, body) SELECT m.rowid, f.path, f.body FROM props_fts f CROSS JOIN props m ON m.path = f.path GROUP BY m.rowid;
            DROP TABLE props_fts;
            ALTER TABLE props_fts_new RENAME TO props_fts;
            PRAGMA user_version = 1;
            """;
        try
        {
            cmd.ExecuteNonQuery();
            tx.Commit();
            Log.Info($"meta.db migrated to rowid-keyed full-text tables in {sw.ElapsedMilliseconds} ms");
        }
        catch (SqliteException ex)
        {
            // never block startup: start the content/metadata tables over; the indexer refills them
            Log.Error("meta.db migration failed; content and metadata will be re-indexed", ex);
            tx.Rollback();
            Exec(c, $"""
                DROP TABLE IF EXISTS content_fts_new; DROP TABLE IF EXISTS props_fts_new;
                DELETE FROM content_meta; DELETE FROM props; DROP TABLE content_fts; DROP TABLE props_fts;
                CREATE VIRTUAL TABLE content_fts USING {ContentFtsDef};
                CREATE VIRTUAL TABLE props_fts USING fts5(path UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
                PRAGMA user_version = 2;
                """);
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ content / props

    public bool IsContentCurrent(string path, long modified, long size)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT modified, size FROM content_meta WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() && r.GetInt64(0) == modified && r.GetInt64(1) == size;
    }

    public HashSet<string> LoadContentIndexedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path || '|' || modified FROM content_meta";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public HashSet<string> LoadPropsIndexedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path || '|' || modified FROM props";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void UpsertContentBatch(IEnumerable<(string path, long modified, long size, string body)> rows)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            // upsert (not INSERT OR REPLACE) keeps the row's rowid, which is also its FTS rowid
            using var meta = c.CreateCommand(); meta.Transaction = tx;
            meta.CommandText = "INSERT INTO content_meta(path, modified, size, indexed) VALUES($p,$m,$s,$i) ON CONFLICT(path) DO UPDATE SET modified=excluded.modified, size=excluded.size, indexed=excluded.indexed RETURNING rowid";
            var mp = meta.Parameters.Add("$p", SqliteType.Text); var mm = meta.Parameters.Add("$m", SqliteType.Integer); var ms = meta.Parameters.Add("$s", SqliteType.Integer); var mi = meta.Parameters.Add("$i", SqliteType.Integer);
            using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM content_fts WHERE rowid=$r"; var dr = del.Parameters.Add("$r", SqliteType.Integer);
            // no path column: works on both the schema-1 table (path left empty) and the schema-2 contentless one
            using var ins = c.CreateCommand(); ins.Transaction = tx; ins.CommandText = "INSERT INTO content_fts(rowid, body) VALUES($r,$b)"; var ir = ins.Parameters.Add("$r", SqliteType.Integer); var ib = ins.Parameters.Add("$b", SqliteType.Text);
            long now = DateTime.UtcNow.Ticks;
            foreach (var (path, modified, size, body) in rows)
            {
                mp.Value = path; mm.Value = modified; ms.Value = size; mi.Value = now;
                long rowid = Convert.ToInt64(meta.ExecuteScalar());
                dr.Value = rowid; del.ExecuteNonQuery();
                if (body.Length > 0) { ir.Value = rowid; ib.Value = body; ins.ExecuteNonQuery(); }
            }
            tx.Commit();
        }
    }

    public void UpsertPropsBatch(IEnumerable<(string path, long modified, string json, string text)> rows)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using var meta = c.CreateCommand(); meta.Transaction = tx;
            meta.CommandText = "INSERT INTO props(path, modified, json) VALUES($p,$m,$j) ON CONFLICT(path) DO UPDATE SET modified=excluded.modified, json=excluded.json RETURNING rowid";
            var mp = meta.Parameters.Add("$p", SqliteType.Text); var mm = meta.Parameters.Add("$m", SqliteType.Integer); var mj = meta.Parameters.Add("$j", SqliteType.Text);
            using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM props_fts WHERE rowid=$r"; var dr = del.Parameters.Add("$r", SqliteType.Integer);
            using var ins = c.CreateCommand(); ins.Transaction = tx; ins.CommandText = "INSERT INTO props_fts(rowid, path, body) VALUES($r,$p,$b)"; var ir = ins.Parameters.Add("$r", SqliteType.Integer); var ip = ins.Parameters.Add("$p", SqliteType.Text); var ib = ins.Parameters.Add("$b", SqliteType.Text);
            foreach (var (path, modified, json, text) in rows)
            {
                mp.Value = path; mm.Value = modified; mj.Value = json;
                long rowid = Convert.ToInt64(meta.ExecuteScalar());
                dr.Value = rowid; del.ExecuteNonQuery();
                if (text.Length > 0) { ir.Value = rowid; ip.Value = path; ib.Value = text; ins.ExecuteNonQuery(); }
            }
            tx.Commit();
        }
    }

    public void RemovePath(string path)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            // the path itself plus everything under it (a deleted/renamed folder); "\" < "]" makes the range an exact prefix match
            const string match = "(path=$p OR (path>=$lo AND path<$hi))";
            cmd.CommandText = $"""
                DELETE FROM content_fts WHERE rowid IN (SELECT rowid FROM content_meta WHERE {match}); DELETE FROM content_meta WHERE {match};
                DELETE FROM props_fts WHERE rowid IN (SELECT rowid FROM props WHERE {match}); DELETE FROM props WHERE {match};
                """;
            string dir = path.TrimEnd('\\');
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$lo", dir + "\\");
            cmd.Parameters.AddWithValue("$hi", dir + "]");
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetPropsJson(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM props WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Photo "date taken" (UTC) for those of the paths the metadata indexer has one for.</summary>
    public Dictionary<string, DateTime> GetDatesTaken(IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // stored as the shell formats a FILETIME: "2018/09/25:22:32:40.000" (UTC)
        foreach (var (path, text) in GetPropValues(paths, "DateTaken"))
            if (DateTime.TryParseExact(text, "yyyy/MM/dd:HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
                result[path] = d;
        return result;
    }

    /// <summary>One indexed file detail (e.g. "Artist", "Duration") for those of the paths that have it.</summary>
    public Dictionary<string, string> GetPropValues(IReadOnlyList<string> paths, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z]+$")) return result;
        try
        {
            using var c = Open();
            for (int i = 0; i < paths.Count; i += 500)
            {
                var chunk = paths.Skip(i).Take(500).ToList();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT path, json_extract(json, $jp) FROM props WHERE path IN (" + string.Join(",", chunk.Select((_, k) => "$p" + k)) + ") AND instr(json, $n) > 0";
                cmd.Parameters.AddWithValue("$jp", "$." + name);
                cmd.Parameters.AddWithValue("$n", "\"" + name + "\"");
                for (int k = 0; k < chunk.Count; k++) cmd.Parameters.AddWithValue("$p" + k, chunk[k]);
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(1)) result[r.GetString(0)] = Convert.ToString(r.GetValue(1)) ?? "";
            }
        }
        catch (Exception ex) { Log.Warn($"{name} lookup failed: " + ex.Message); }
        return result;
    }

    /// <summary>
    /// Full-text search over file contents (and optionally extracted properties). Returns matching paths, best match
    /// first (FTS5 rank: a short note about the word beats a long licence file that mentions it once).
    /// </summary>
    public List<string> SearchContent(string userQuery, int limit, bool includeProps = true)
    {
        var results = new List<string>();
        string match = ToFtsQuery(userQuery);
        if (match.Length == 0) return results;
        try
        {
            using var c = Open();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, meta) in includeProps ? new[] { ("content_fts", "content_meta"), ("props_fts", "props") } : new[] { ("content_fts", "content_meta") })
            {
                using var cmd = c.CreateCommand();
                cmd.Parameters.AddWithValue("$q", match);
                cmd.Parameters.AddWithValue("$n", limit);
                // ranking scores every match, so a half-typed word in 50,000 files ("con") costs ~1 s; counting is cheap
                // with the prefix index. Past 5,000 matches skip the rank (the caller still lists your documents first).
                cmd.CommandText = $"SELECT count(*) FROM {table} WHERE {table} MATCH $q";
                bool rank = Convert.ToInt64(cmd.ExecuteScalar()) <= 5000;
                // the path comes from the meta table (same rowid); the FTS row has none (schema 2) or its whole text with it
                cmd.CommandText = rank
                    ? $"SELECT m.path FROM (SELECT rowid AS r, rank AS rk FROM {table} WHERE {table} MATCH $q ORDER BY rank LIMIT $n) x JOIN {meta} m ON m.rowid = x.r ORDER BY x.rk"
                    : $"SELECT m.path FROM (SELECT rowid AS r FROM {table} WHERE {table} MATCH $q LIMIT $n) x JOIN {meta} m ON m.rowid = x.r ORDER BY x.r";
                using var r = cmd.ExecuteReader();
                while (r.Read()) { var p = r.GetString(0); if (seen.Add(p)) results.Add(p); }
            }
        }
        catch (SqliteException ex) { Log.Warn("FTS query failed: " + ex.Message); if (IsCorrupt(ex)) RecoverRequested = true; }
        return results;
    }

    /// <summary>Turns free text into a safe FTS5 MATCH expression: each word becomes a quoted prefix token, quoted phrases stay phrases.</summary>
    public static string ToFtsQuery(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            string token;
            bool phrase = false;
            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                token = text[(i + 1)..end];
                i = end + 1; phrase = true;
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                token = text[start..i];
            }
            token = token.Replace("\"", "\"\"").Trim();
            if (token.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(token).Append('"');
            if (!phrase) sb.Append('*');
        }
        return sb.ToString();
    }

    public (long contentRows, long propsRows) Stats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM content_meta), (SELECT COUNT(*) FROM props)";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
    }

    public void ClearContent()
    {
        lock (_writeLock)
        {
            using var c = Open();
            // recreated rather than emptied: also finishes a pending schema-2 upgrade
            Exec(c, $"""
                DROP TABLE IF EXISTS content_fts_v2; DROP TABLE content_fts; CREATE VIRTUAL TABLE content_fts USING {ContentFtsDef};
                DELETE FROM kv WHERE key='fts_v2_cursor'; PRAGMA user_version = 2;
                DELETE FROM content_meta; DELETE FROM props_fts; DELETE FROM props;
                """);
        }
    }

    // ------------------------------------------------------------------ thumbnails

    public byte[]? GetThumb(string key, long modified)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT png FROM thumbs WHERE key=$k AND modified=$m";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$m", modified);
        return cmd.ExecuteScalar() as byte[];
    }

    public void PutThumb(string key, long modified, byte[] png)
    {
        // only a cache: when another write (a one-time VACUUM) holds the database, skip it rather than stall thumbnails
        if (!Monitor.TryEnter(_writeLock, 300)) return;
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO thumbs(key, modified, png, accessed) VALUES($k,$m,$p,$a)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$m", modified);
            cmd.Parameters.AddWithValue("$p", png);
            cmd.Parameters.AddWithValue("$a", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
        finally { Monitor.Exit(_writeLock); }
    }

    public long ThumbCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM thumbs";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void ClearThumbs()
    {
        lock (_writeLock) { using var c = Open(); Exec(c, "DELETE FROM thumbs"); }   // Maintain gives the space back
    }

    // ------------------------------------------------------------------ folder views / kv

    public string? GetFolderView(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM folder_views WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteScalar() as string;
    }

    public void SetFolderView(string path, string? json)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            if (json == null) { cmd.CommandText = "DELETE FROM folder_views WHERE path=$p"; }
            else { cmd.CommandText = "INSERT OR REPLACE INTO folder_views(path, json) VALUES($p,$j)"; cmd.Parameters.AddWithValue("$j", json); }
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    public List<string> ListFolderViews()
    {
        var list = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path FROM folder_views ORDER BY path";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void ClearFolderViews()
    {
        lock (_writeLock) { using var c = Open(); Exec(c, "DELETE FROM folder_views"); }
    }

    public string? GetValue(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetValue(string key, string value)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO kv(key, value) VALUES($k,$v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}
