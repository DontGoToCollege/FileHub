using System.IO;
using FileHub.Platform;
using Microsoft.Data.Sqlite;

namespace FileHub.Core.Database;

/// <summary>
/// Thumbnail store (thumbs.db, beside meta.db): one 256 px image per picture, video, PDF or folder (JPEG, or PNG when
/// it has transparency), shown at every view size by scaling it down when read (0.3–0.5 ms). Its own file, so making
/// thousands of thumbnails never waits on the content index or the other way round, and it can be cleared alone.
/// Only a cache: a damaged file is simply started again. An empty image means "this file has no thumbnail".
/// </summary>
public sealed class ThumbDb : IDisposable
{
    public const int Size = 256;
    private readonly string _file;
    private readonly string _connString;
    private readonly object _writeLock = new();
    private int _resets;

    public ThumbDb(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _connString = new SqliteConnectionStringBuilder { DataSource = file, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
        try { Init(); }
        catch (Exception ex) when (MetaDb.IsCorrupt(ex)) { Reset(); }
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connString); c.Open(); return c; }

    private void Init()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        // auto_vacuum before the table exists: space from deleted thumbnails goes back to the disk in Maintain
        cmd.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; " +
                          "CREATE TABLE IF NOT EXISTS thumbs(path TEXT PRIMARY KEY COLLATE NOCASE, modified INTEGER NOT NULL, img BLOB NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Starts over with an empty file, once per run (a thumbnail cache has nothing worth saving).</summary>
    private void Reset()
    {
        lock (_writeLock)
        {
            if (_resets++ > 0) return;
            Log.Warn("thumbs.db is damaged; starting a fresh one (thumbnails are made again)");
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { _file + "-wal", _file + "-shm", _file })   // the WAL first, so a fresh file never meets it
                for (int i = 0; ; i++)
                {
                    try { File.Delete(f); break; }
                    catch (IOException) when (i < 40) { Thread.Sleep(250); SqliteConnection.ClearAllPools(); }
                }
            Init();
        }
    }

    private T Guard<T>(Func<T> work, T fallback)
    {
        try { return work(); }
        catch (Exception ex) when (MetaDb.IsCorrupt(ex)) { Reset(); return fallback; }
        catch (Exception ex) { Log.Warn("thumbs.db: " + ex.Message); return fallback; }
    }

    /// <summary>The stored thumbnail for this version of the file; null if none, empty if the file has no thumbnail.</summary>
    public byte[]? Get(string path, long modified) => Guard(() =>
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT img FROM thumbs WHERE path=$p AND modified=$m";
        cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$m", modified);
        return cmd.ExecuteScalar() as byte[];
    }, null);

    public void Put(IReadOnlyCollection<(string path, long modified, byte[] img)> rows)
    {
        if (rows.Count == 0) return;
        Guard(() =>
        {
            lock (_writeLock)
            {
                using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO thumbs(path, modified, img) VALUES($p,$m,$i) ON CONFLICT(path) DO UPDATE SET modified=excluded.modified, img=excluded.img";
                var p = cmd.Parameters.Add("$p", SqliteType.Text); var m = cmd.Parameters.Add("$m", SqliteType.Integer); var i = cmd.Parameters.Add("$i", SqliteType.Blob);
                foreach (var r in rows) { p.Value = r.path; m.Value = r.modified; i.Value = r.img; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            return true;
        }, false);
    }

    /// <summary>"path|modified" of every stored thumbnail, so a pass only makes the missing or outdated ones.</summary>
    public HashSet<string> LoadStamps() => Guard(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path || '|' || modified FROM thumbs";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Drops a file's thumbnail, or a folder's and everything under it.</summary>
    public void Remove(string path) => Guard(() =>
    {
        lock (_writeLock)
        {
            using var c = Open(); using var cmd = c.CreateCommand();
            // ']' follows '\' in sort order, so the range is exactly the paths inside the folder
            cmd.CommandText = "DELETE FROM thumbs WHERE path=$p OR (path >= $p || '\\' AND path < $p || ']')";
            cmd.Parameters.AddWithValue("$p", path.TrimEnd('\\'));
            return cmd.ExecuteNonQuery();
        }
    }, 0);

    /// <summary>Drops thumbnails of files that no longer exist (deleted while FileHub wasn't running).</summary>
    public int Prune(Func<string, bool> stillThere) => Guard(() =>
    {
        var gone = new List<string>();
        using (var c = Open()) using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT path FROM thumbs";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var p = r.GetString(0); if (!stillThere(p)) gone.Add(p); }
        }
        if (gone.Count == 0) return 0;
        lock (_writeLock)
        {
            using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM thumbs WHERE path=$p"; var p = cmd.Parameters.Add("$p", SqliteType.Text);
            foreach (var g in gone) { p.Value = g; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
        return gone.Count;
    }, 0);

    /// <summary>Re-keys every thumbnail under path prefix <paramref name="from"/> to <paramref name="to"/> (a drive under a new letter).</summary>
    public long MovePrefix(string from, string to, Action<long>? progress = null) =>
        Guard(() => MetaDb.MoveRows(Open, _writeLock, ["thumbs"], from, to, progress), 0L);

    public long Count() => Guard(() =>
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM thumbs WHERE length(img) > 0";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }, 0L);

    public long FileSize => File.Exists(_file) ? new FileInfo(_file).Length : 0;

    public void Clear() => Guard(() => { lock (_writeLock) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM thumbs"; cmd.ExecuteNonQuery(); } Maintain(); return true; }, false);

    /// <summary>Gives free space back to the disk (8 MB a step, keeping 16 MB for upcoming writes) and empties the WAL.</summary>
    public void Maintain() => Guard(() =>
    {
        using var c = Open();
        long Scalar(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }
        while (Scalar("PRAGMA freelist_count") > 4096)
            lock (_writeLock) { using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA incremental_vacuum(2048)"; cmd.ExecuteNonQuery(); }
        lock (_writeLock) { using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cmd.ExecuteNonQuery(); }
        return true;
    }, false);

    public void Dispose() => SqliteConnection.ClearAllPools();
}
