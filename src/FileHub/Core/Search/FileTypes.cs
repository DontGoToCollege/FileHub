namespace FileHub.Core.Search;

/// <summary>Extension groups used by the type: filter, the filter chips and icon/thumbnail decisions.</summary>
public static class FileTypes
{
    public static readonly Dictionary<string, HashSet<string>> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["picture"] = Set("jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "ico", "svg", "psd", "raw", "cr2", "nef", "arw", "dng", "avif", "jfif"),
        ["video"] = Set("mp4", "mkv", "avi", "mov", "wmv", "m4v", "webm", "flv", "mpg", "mpeg", "3gp", "ts", "m2ts", "vob", "ogv"),
        ["audio"] = Set("mp3", "flac", "wav", "m4a", "wma", "ogg", "aac", "opus", "aiff", "alac", "mid", "midi", "ape"),
        ["document"] = Set("doc", "docx", "pdf", "txt", "rtf", "odt", "xls", "xlsx", "ppt", "pptx", "csv", "md", "epub", "mobi", "one", "pages", "numbers", "key", "tex", "log"),
        ["code"] = Set("cs", "js", "ts", "tsx", "jsx", "py", "java", "kt", "c", "cpp", "h", "hpp", "rs", "go", "rb", "php", "swift", "sql", "ps1", "bat", "cmd", "sh", "html", "htm", "css", "scss", "json", "xml", "yaml", "yml", "toml", "ini", "cfg", "csproj", "sln", "vb", "fs", "lua", "pl", "r", "m", "dart", "vue", "svelte"),
        ["archive"] = Set("zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "tgz", "zst", "lz", "wim"),
        ["executable"] = Set("exe", "msi", "bat", "cmd", "com", "scr", "ps1", "lnk", "appx", "msix"),
        ["font"] = Set("ttf", "otf", "woff", "woff2", "fon"),
    };

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    public static readonly string[] Aliases = { "pic", "picture", "pictures", "image", "images", "img", "photo", "vid", "video", "videos", "movie", "audio", "music", "sound", "doc", "docs", "document", "documents", "code", "source", "archive", "zip", "exe", "executable", "app", "font", "folder", "dir", "file" };

    public static string? Canonical(string alias) => alias.ToLowerInvariant() switch
    {
        "pic" or "picture" or "pictures" or "image" or "images" or "img" or "photo" or "photos" => "picture",
        "vid" or "video" or "videos" or "movie" or "movies" => "video",
        "audio" or "music" or "sound" or "song" or "songs" => "audio",
        "doc" or "docs" or "document" or "documents" => "document",
        "code" or "source" or "src" => "code",
        "archive" or "archives" or "zip" or "compressed" => "archive",
        "exe" or "executable" or "app" or "apps" or "program" => "executable",
        "font" or "fonts" => "font",
        "folder" or "folders" or "dir" or "directory" => "folder",
        "file" or "files" => "file",
        _ => null,
    };

    public static bool IsIn(string group, string ext) => Groups.TryGetValue(group, out var set) && set.Contains(ext);

    public static bool HasThumbnail(string ext) => IsIn("picture", ext) || IsIn("video", ext) || ext.Equals("pdf", StringComparison.OrdinalIgnoreCase);

    public static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return string.Empty;
        return name[(dot + 1)..];
    }
}
