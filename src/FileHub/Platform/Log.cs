using System.IO;

namespace FileHub.Platform;

public static class Log
{
    private static readonly object _lock = new();
    public static string Directory => Path.Combine(Paths.DataDir, "logs");   // follows DataDir if it moves at startup
    private static string File => Path.Combine(Directory, $"filehub-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(Directory);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

public static class Paths
{
    // data lives next to the exe ("FileHub-Data"); older versions used %LocalAppData%\FileHub, which is moved over on first start.
    // FILEHUB_DATA overrides both (used to run a debug build against the real data).
    private static readonly string Portable = Path.Combine(AppContext.BaseDirectory, "FileHub-Data");
    private static readonly string Legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileHub");
    private static readonly string? Override = Environment.GetEnvironmentVariable("FILEHUB_DATA") is { Length: > 0 } o ? o : null;
    // a data folder the owner moved elsewhere ("Move data folder" in Settings) is recorded next to the exe
    private static readonly string LocationFile = Path.Combine(AppContext.BaseDirectory, "FileHub-Data.location");
    private static readonly string MoveFile = Path.Combine(AppContext.BaseDirectory, "FileHub-Data.move");   // pending move, done at next start

    public static string DataDir { get; private set; } = Override ?? ReadLine(LocationFile) ?? (Directory.Exists(Portable) || !Directory.Exists(Legacy) ? Portable : Legacy);

    private static string? ReadLine(string file)
    {
        try { var s = File.Exists(file) ? File.ReadAllText(file).Trim() : ""; return s.Length > 0 ? s : null; } catch { return null; }
    }
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string IndexFile => Path.Combine(DataDir, "index.fhdb");
    public static string MetaDbFile => Path.Combine(DataDir, "meta.db");
    public static string ThumbDbFile => Path.Combine(DataDir, "thumbs.db");

    static Paths()
    {
        try { Directory.CreateDirectory(DataDir); }
        catch { DataDir = Legacy; try { Directory.CreateDirectory(DataDir); } catch { } }   // exe folder not writable (e.g. Program Files)
    }

    /// <summary>
    /// Primary instance only, before anything opens the data: moves the old AppData folder next to the exe, then
    /// carries out a move scheduled from Settings.
    /// </summary>
    public static void PrepareDataDir()
    {
        if (Override != null) return;
        if (DataDir == Legacy && !File.Exists(LocationFile))
        {
            try { MoveTree(Legacy, Portable); DataDir = Portable; Log.Info($"Data moved from {Legacy} to {Portable}"); }
            catch (Exception ex) { Log.Warn($"Data stays in {Legacy}; moving it next to the exe failed: {ex.Message}"); }
        }

        string? to = ReadLine(MoveFile);
        if (to == null) return;
        try { File.Delete(MoveFile); } catch { }
        string from = DataDir;
        try { MoveTree(from, to); }
        catch (Exception ex) { Log.Error($"Moving the data folder to {to} failed; it stays in {from}", ex); return; }
        DataDir = to;
        try
        {
            if (string.Equals(to, Portable, StringComparison.OrdinalIgnoreCase)) File.Delete(LocationFile);
            else File.WriteAllText(LocationFile, to);
        }
        catch (Exception ex) { Log.Error("Couldn't record the new data location next to FileHub.exe", ex); }
        Log.Info($"Data moved from {from} to {to}");
    }

    /// <summary>Validates a folder picked in Settings and records it for the move at the next start. Returns an error, or null.</summary>
    public static string? ScheduleMove(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder.Trim().Trim('"'));
            // the picked folder gets a FileHub-Data folder inside it, unless it already is one
            string to = string.Equals(Path.GetFileName(full.TrimEnd('\\')), "FileHub-Data", StringComparison.OrdinalIgnoreCase) ? full.TrimEnd('\\') : Path.Combine(full, "FileHub-Data");
            string cur = DataDir.TrimEnd('\\');
            if (string.Equals(to, cur, StringComparison.OrdinalIgnoreCase)) return "FileHub's data is already in that folder.";
            if (to.StartsWith(cur + "\\", StringComparison.OrdinalIgnoreCase)) return "Pick a folder outside the current data folder.";
            if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return $"{to} already has files in it. Pick another folder.";
            File.WriteAllText(MoveFile, to);
            return null;
        }
        catch (Exception ex) { return "Couldn't prepare the move: " + ex.Message; }
    }

    /// <summary>Moves a folder (renames on the same drive, copies then deletes across drives). 'to' must not exist or be empty.</summary>
    private static void MoveTree(string from, string to)
    {
        if (Directory.Exists(to)) Directory.Delete(to);   // empty (checked when scheduled); throws if not, leaving everything as is
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) { Directory.Move(from, to); return; }
        try { CopyTree(from, to); }
        catch { try { Directory.Delete(to, true); } catch { } throw; }   // half a copy: drop it, the original is untouched
        try { Directory.Delete(from, true); }
        catch (Exception ex) { Log.Warn($"Data copied to {to}, but the old folder {from} couldn't be fully removed: {ex.Message}"); }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
