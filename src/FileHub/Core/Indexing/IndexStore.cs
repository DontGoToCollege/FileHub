using System.IO;
using System.IO.Compression;
using System.Text;
using FileHub.Platform;

namespace FileHub.Core.Indexing;

/// <summary>Binary snapshot of the index ("index.fhdb") so the app is searchable instantly on startup.</summary>
public static class IndexStore
{
    private const uint Magic = 0x46484442; // "FHDB"
    private const int Version = 1;

    public static void Save(FileIndex index, string path)
    {
        var compact = index.Compact();
        string tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8))
        {
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(compact.BuiltUtc.Ticks);
            bw.Write(compact.Count);
            var entries = compact.Entries;
            for (int i = 0; i < compact.Count; i++)
            {
                ref var e = ref entries[i];
                bw.Write(e.Name ?? string.Empty);
                bw.Write(e.Parent);
                bw.Write(e.Size);
                bw.Write(e.Modified);
                bw.Write(e.Created);
                bw.Write((int)e.Attributes);
            }
        }
        File.Move(tmp, path, true);
    }

    public static FileIndex? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var br = new BinaryReader(fs, Encoding.UTF8);
            if (br.ReadUInt32() != Magic) return null;
            int ver = br.ReadInt32();
            if (ver != Version) return null;
            long built = br.ReadInt64();
            int count = br.ReadInt32();
            var index = new FileIndex(count + 1024) { BuiltUtc = new DateTime(built, DateTimeKind.Utc) };
            for (int i = 0; i < count; i++)
            {
                string name = br.ReadString();
                int parent = br.ReadInt32();
                long size = br.ReadInt64();
                long modified = br.ReadInt64();
                long created = br.ReadInt64();
                var attrs = (FileAttributes)br.ReadInt32();
                if (parent < 0) index.AddRoot(name);
                else
                {
                    int id = index.AppendRaw(name, parent, size, modified, created, attrs);
                    if ((attrs & FileAttributes.Directory) != 0) index.RegisterDirectory(index.PathOf(id), id);
                }
            }
            return index;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load index snapshot", ex);
            return null;
        }
    }
}
