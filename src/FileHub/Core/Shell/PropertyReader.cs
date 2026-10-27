using System.Runtime.InteropServices;
using FileHub.Platform;

namespace FileHub.Core.Shell;

/// <summary>Reads Windows Property System metadata (title, author, dimensions, duration, camera, …) for a file.</summary>
public static class PropertyReader
{
    private static readonly Guid SummaryInfo = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid DocSummary = new("D5CDD502-2E9C-101B-9397-08002B2CF9AE");
    private static readonly Guid ImageSummary = new("6444048F-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid MediaFile = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid Video = new("64440491-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid Music = new("56A3372E-CE9C-11D2-9F0E-006097C686F6");
    private static readonly Guid Photo = new("14B81DA1-0135-4D31-96D9-6CBFC9671A99");
    private static readonly Guid ItemType = new("28636AA6-953D-11D2-B5D6-00C04FD918D0");
    private static readonly Guid Rating = new("64440492-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid Audio = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid Software = new("0CEF7D53-FA64-11D1-A203-0000F81FEDEE");

    internal static readonly (string Name, Native.PROPERTYKEY Key)[] Keys =
    {
        ("Title", new(SummaryInfo, 2)),
        ("Subject", new(SummaryInfo, 3)),
        ("Author", new(SummaryInfo, 4)),
        ("Tags", new(SummaryInfo, 5)),
        ("Comment", new(SummaryInfo, 6)),
        ("Company", new(DocSummary, 15)),
        ("Dimensions", new(ImageSummary, 13)),
        ("Width", new(ImageSummary, 3)),
        ("Height", new(ImageSummary, 4)),
        ("Duration", new(MediaFile, 3)),
        ("Bitrate", new(Audio, 4)),
        ("FrameWidth", new(Video, 3)),
        ("FrameHeight", new(Video, 4)),
        ("Artist", new(Music, 2)),
        ("Album", new(Music, 4)),
        ("Genre", new(Music, 11)),
        ("Year", new(new Guid("56A3372E-CE9C-11D2-9F0E-006097C686F6"), 5)),
        ("DateTaken", new(Photo, 36867)),
        ("CameraModel", new(Photo, 272)),
        ("CameraMaker", new(Photo, 271)),
        ("Rating", new(Rating, 9)),
        ("ItemType", new(ItemType, 4)),
        ("ProductName", new(Software, 7)),
        ("FileVersion", new(new Guid("0CEF7D53-FA64-11D1-A203-0000F81FEDEE"), 4)),
    };

    /// <summary>Returns name/value pairs for the properties the shell knows about this file. Empty when nothing is available.</summary>
    public static Dictionary<string, string> Read(string path, bool fast = true)
    {
        var result = new Dictionary<string, string>();
        Native.IPropertyStore? store = null;
        try
        {
            var iid = Native.IID_IPropertyStore;
            int flags = Native.GPS_BESTEFFORT | (fast ? Native.GPS_FASTPROPERTIESONLY : 0);
            int hr = Native.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, flags, ref iid, out store);
            if (hr != 0 || store == null) return result;
            foreach (var (name, key) in Keys)
            {
                var k = key;
                if (store.GetValue(ref k, out var pv) != 0) continue;
                try
                {
                    if (pv.vt == 0 /*VT_EMPTY*/ || pv.vt == 1 /*VT_NULL*/) continue;
                    string? text = null;
                    if (name == "Duration" && (pv.vt == 21 /*VT_UI8*/ || pv.vt == 20))
                    {
                        var ts = TimeSpan.FromTicks((long)pv.ul);
                        text = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
                    }
                    else if (Native.PropVariantToStringAlloc(ref pv, out var psz) == 0 && psz != IntPtr.Zero)
                    {
                        try { text = Marshal.PtrToStringUni(psz); } finally { Native.CoTaskMemFree(psz); }
                    }
                    if (!string.IsNullOrWhiteSpace(text)) result[name] = text.Trim();
                }
                finally { Native.PropVariantClear(ref pv); }
            }
        }
        catch (Exception ex) { Log.Warn("Property read failed for " + path + ": " + ex.Message); }
        finally { if (store != null) { try { Marshal.ReleaseComObject(store); } catch { } } }
        return result;
    }
}
