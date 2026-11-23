using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileHub.Platform;

namespace FileHub.Core.Indexing;

/// <summary>A drive FileHub has indexed, known by its partition (not its letter), so it's recognised on any PC.</summary>
public sealed class DriveRecord
{
    /// <summary>Partition ID | file system | size in bytes: all three must match for "the same drive".</summary>
    public string Key { get; set; } = "";
    public string Letter { get; set; } = "";      // last seen as
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
    /// <summary>Letter its files are listed under in the live index; null while set aside (drives\&lt;key&gt;.fhdb).</summary>
    public string? IndexAt { get; set; }
    /// <summary>Letter its meta.db / thumbs.db rows are stored under; null while parked under <see cref="DriveRegistry.Parked"/>.</summary>
    public string? RowsAt { get; set; }
    [JsonIgnore] public bool Online { get; set; }
}

/// <summary>The drives FileHub knows (drives.json in the data folder), and how to tell which one sits under a letter.</summary>
public sealed class DriveRegistry
{
    public List<DriveRecord> Drives { get; set; } = new();

    private static string File_ => Path.Combine(Paths.DataDir, "drives.json");
    private static string SetAsideDir => Path.Combine(Paths.DataDir, "drives");

    public static DriveRegistry Load()
    {
        try { if (File.Exists(File_)) return JsonSerializer.Deserialize<DriveRegistry>(File.ReadAllText(File_)) ?? new(); }
        catch (Exception ex) { Log.Warn("drives.json unreadable: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try
        {
            string tmp = File_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, File_, true);
        }
        catch (Exception ex) { Log.Warn("drives.json not saved: " + ex.Message); }
    }

    /// <summary>Path prefix a drive's database rows are parked under while another drive has its letter.</summary>
    public static string Parked(DriveRecord d) => "::" + d.Key + "\\";

    /// <summary>Where a drive's file list waits while it's unplugged.</summary>
    public static string SetAsideFile(DriveRecord d) =>
        Path.Combine(SetAsideDir, string.Concat(d.Key.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_')) + ".fhdb");

    /// <summary>The drive under <paramref name="letter"/>: its key, label and kind; null if Windows can't identify it.</summary>
    public static (string key, string label, string kind)? Identify(char letter)
    {
        try
        {
            var di = new DriveInfo(letter.ToString());
            if (!di.IsReady) return null;
            string? id = Native.PartitionId(letter);
            if (id == null) return null;
            return ($"{id}|{di.DriveFormat}|{di.TotalSize}", di.VolumeLabel, ContentIndexer.KindName(letter + ":\\"));
        }
        catch (Exception ex) { Log.Warn($"Drive {letter}: not identified ({ex.Message})"); return null; }
    }

    public DriveRecord? Find(string key) => Drives.FirstOrDefault(d => d.Key == key);
}
