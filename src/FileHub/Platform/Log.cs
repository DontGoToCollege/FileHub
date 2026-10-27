using System.IO;

namespace FileHub.Platform;

public static class Log
{
    private static readonly object _lock = new();
    public static string Directory { get; } = Path.Combine(Paths.DataDir, "logs");
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
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileHub");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string IndexFile => Path.Combine(DataDir, "index.fhdb");
    public static string MetaDbFile => Path.Combine(DataDir, "meta.db");
    static Paths() { try { Directory.CreateDirectory(DataDir); } catch { } }
}
