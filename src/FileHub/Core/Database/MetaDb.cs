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
    private readonly object _writeLock = new();

    public MetaDb(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = file, Cache = SqliteCacheMode.Private, Pooling = true /* private cache + WAL: readers never wait on the content indexer's writes */ }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS content_meta(path TEXT PRIMARY KEY, modified INTEGER NOT NULL, size INTEGER NOT NULL, indexed INTEGER NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5(path UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
            CREATE TABLE IF NOT EXISTS props(path TEXT PRIMARY KEY, modified INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS props_fts USING fts5(path UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
            CREATE TABLE IF NOT EXISTS thumbs(key TEXT PRIMARY KEY, modified INTEGER NOT NULL, png BLOB NOT NULL, accessed INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS folder_views(path TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT);
            """);
        MigrateFtsToRowids(c);
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
            Exec(c, """
                DROP TABLE IF EXISTS content_fts_new; DROP TABLE IF EXISTS props_fts_new;
                DELETE FROM content_meta; DELETE FROM props; DROP TABLE content_fts; DROP TABLE props_fts;
                CREATE VIRTUAL TABLE content_fts USING fts5(path UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
                CREATE VIRTUAL TABLE props_fts USING fts5(path UNINDEXED, body, tokenize='unicode61 remove_diacritics 2');
                PRAGMA user_version = 1;
                """);
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
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
            using var ins = c.CreateCommand(); ins.Transaction = tx; ins.CommandText = "INSERT INTO content_fts(rowid, path, body) VALUES($r,$p,$b)"; var ir = ins.Parameters.Add("$r", SqliteType.Integer); var ip = ins.Parameters.Add("$p", SqliteType.Text); var ib = ins.Parameters.Add("$b", SqliteType.Text);
            long now = DateTime.UtcNow.Ticks;
            foreach (var (path, modified, size, body) in rows)
            {
                mp.Value = path; mm.Value = modified; ms.Value = size; mi.Value = now;
                long rowid = Convert.ToInt64(meta.ExecuteScalar());
                dr.Value = rowid; del.ExecuteNonQuery();
                if (body.Length > 0) { ir.Value = rowid; ip.Value = path; ib.Value = body; ins.ExecuteNonQuery(); }
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
                // the path comes from the meta table (same rowid): reading it from the FTS row loads each file's whole
                // stored text, which made a content search take 0.2–2 s instead of 10–500 ms
                cmd.CommandText = $"SELECT m.path FROM (SELECT rowid AS r, rank AS rk FROM {table} WHERE {table} MATCH $q ORDER BY rank LIMIT $n) x JOIN {meta} m ON m.rowid = x.r ORDER BY x.rk";
                cmd.Parameters.AddWithValue("$q", match);
                cmd.Parameters.AddWithValue("$n", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read()) { var p = r.GetString(0); if (seen.Add(p)) results.Add(p); }
            }
        }
        catch (SqliteException ex) { Log.Warn("FTS query failed: " + ex.Message); }
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
            Exec(c, "DELETE FROM content_fts; DELETE FROM content_meta; DELETE FROM props_fts; DELETE FROM props;");
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
        lock (_writeLock)
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
        lock (_writeLock) { using var c = Open(); Exec(c, "DELETE FROM thumbs; VACUUM;"); }
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
