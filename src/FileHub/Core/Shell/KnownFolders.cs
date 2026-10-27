using FileHub.Platform;

namespace FileHub.Core.Shell;

public static class KnownFolders
{
    private static readonly Guid DesktopId = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid DocumentsId = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid PicturesId = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid MusicId = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    private static readonly Guid VideosId = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    private static readonly Guid ProfileId = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
    private static readonly Guid StartMenuId = new("625B53C3-AB48-4EC1-BA1F-A1EF4146FC19");
    private static readonly Guid CommonStartMenuId = new("A4115719-D62E-491D-AA7C-E74B8BE3B067");

    public static string? Desktop => Native.GetKnownFolder(DesktopId) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    public static string? Documents => Native.GetKnownFolder(DocumentsId) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string? Downloads => Native.GetKnownFolder(DownloadsId);
    public static string? Pictures => Native.GetKnownFolder(PicturesId) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static string? Music => Native.GetKnownFolder(MusicId) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public static string? Videos => Native.GetKnownFolder(VideosId) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public static string? Profile => Native.GetKnownFolder(ProfileId) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string? StartMenu => Native.GetKnownFolder(StartMenuId);
    public static string? CommonStartMenu => Native.GetKnownFolder(CommonStartMenuId);

    /// <summary>Name -> path for the standard user folders (used by nav pane, Home and start menu).</summary>
    public static IReadOnlyList<(string Name, string Path)> UserFolders()
    {
        var list = new List<(string, string)>();
        void Add(string n, string? p) { if (!string.IsNullOrEmpty(p) && System.IO.Directory.Exists(p)) list.Add((n, p)); }
        Add("Desktop", Desktop); Add("Documents", Documents); Add("Downloads", Downloads);
        Add("Pictures", Pictures); Add("Music", Music); Add("Videos", Videos);
        return list;
    }

    public static string? Resolve(string name) => name switch
    {
        "Desktop" => Desktop, "Documents" => Documents, "Downloads" => Downloads, "Pictures" => Pictures,
        "Music" => Music, "Videos" => Videos, "Profile" or "User" => Profile, _ => null,
    };
}
