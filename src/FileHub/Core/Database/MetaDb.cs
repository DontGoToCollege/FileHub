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
        _connString = new SqliteConnectionStringBuilder { DataSource = file, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
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
            using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM content_fts WHERE path=$p"; var dp = del.Parameters.Add("$p", SqliteType.Text);
            using var ins = c.CreateCommand(); ins.Transaction = tx; ins.CommandText = "INSERT INTO content_fts(path, body) VALUES($p,$b)"; var ip = ins.Parameters.Add("$p", SqliteType.Text); var ib = ins.Parameters.Add("$b", SqliteType.Text);
            using var meta = c.CreateCommand(); meta.Transaction = tx; meta.CommandText = "INSERT OR REPLACE INTO content_meta(path, modified, size, indexed) VALUES($p,$m,$s,$i)";
            var mp = meta.Parameters.Add("$p", SqliteType.Text); var mm = meta.Parameters.Add("$m", SqliteType.Integer); var ms = meta.Parameters.Add("$s", SqliteType.Integer); var mi = meta.Parameters.Add("$i", SqliteType.Integer);
            long now = DateTime.UtcNow.Ticks;
            foreach (var (path, modified, size, body) in rows)
            {
                dp.Value = path; del.ExecuteNonQuery();
                if (body.Length > 0) { ip.Value = path; ib.Value = body; ins.ExecuteNonQuery(); }
                mp.Value = path; mm.Value = modified; ms.Value = size; mi.Value = now; meta.ExecuteNonQuery();
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
            using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM props_fts WHERE path=$p"; var dp = del.Parameters.Add("$p", SqliteType.Text);
            using var ins = c.CreateCommand(); ins.Transaction = tx; ins.CommandText = "INSERT INTO props_fts(path, body) VALUES($p,$b)"; var ip = ins.Parameters.Add("$p", SqliteType.Text); var ib = ins.Parameters.Add("$b", SqliteType.Text);
            using var meta = c.CreateCommand(); meta.Transaction = tx; meta.CommandText = "INSERT OR REPLACE INTO props(path, modified, json) VALUES($p,$m,$j)";
            var mp = meta.Parameters.Add("$p", SqliteType.Text); var mm = meta.Parameters.Add("$m", SqliteType.Integer); var mj = meta.Parameters.Add("$j", SqliteType.Text);
            foreach (var (path, modified, json, text) in rows)
            {
                dp.Value = path; del.ExecuteNonQuery();
                if (text.Length > 0) { ip.Value = path; ib.Value = text; ins.ExecuteNonQuery(); }
                mp.Value = path; mm.Value = modified; mj.Value = json; meta.ExecuteNonQuery();
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
            cmd.CommandText = "DELETE FROM content_fts WHERE path=$p; DELETE FROM content_meta WHERE path=$p; DELETE FROM props_fts WHERE path=$p; DELETE FROM props WHERE path=$p;";
            cmd.Parameters.AddWithValue("$p", path);
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

    /// <summary>Full-text search over file contents (and optionally extracted properties). Returns matching paths.</summary>
    public List<string> SearchContent(string userQuery, int limit, bool includeProps = true)
    {
        var results = new List<string>();
        string match = ToFtsQuery(userQuery);
        if (match.Length == 0) return results;
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = includeProps
                ? "SELECT path FROM content_fts WHERE content_fts MATCH $q LIMIT $n UNION SELECT path FROM props_fts WHERE props_fts MATCH $q LIMIT $n"
                : "SELECT path FROM content_fts WHERE content_fts MATCH $q LIMIT $n";
            cmd.Parameters.AddWithValue("$q", match);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) results.Add(r.GetString(0));
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
