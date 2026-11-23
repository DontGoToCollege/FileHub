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

    public static string DataDir { get; private set; } = Override ?? (Directory.Exists(Portable) || !Directory.Exists(Legacy) ? Portable : Legacy);
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string IndexFile => Path.Combine(DataDir, "index.fhdb");
    public static string MetaDbFile => Path.Combine(DataDir, "meta.db");

    static Paths()
    {
        try { Directory.CreateDirectory(DataDir); }
        catch { DataDir = Legacy; try { Directory.CreateDirectory(DataDir); } catch { } }   // exe folder not writable (e.g. Program Files)
    }

    /// <summary>Primary instance only, before anything opens the data: moves the old AppData folder next to the exe.</summary>
    public static void MoveLegacyData()
    {
        if (DataDir != Legacy || Override != null) return;
        // shortcut: a move across drives fails and the data stays in AppData; copy it if the exe ever lives on another drive
        try { Directory.Move(Legacy, Portable); DataDir = Portable; Log.Info($"Data moved from {Legacy} to {Portable}"); }
        catch (Exception ex) { Log.Warn($"Data stays in {Legacy}; moving it next to the exe failed: {ex.Message}"); }
    }
}
