using System.IO;
using System.Windows.Media;
using FileHub.Core.Indexing;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;

namespace FileHub.UI.Common;

/// <summary>Row / tile view-model for a file or folder used by every list in the app.</summary>
public sealed class FileItem : ObservableObject
{
    public string Name { get; private set; } = "";
    public string FullPath { get; private set; } = "";
    public string Extension { get; private set; } = "";
    public bool IsDirectory { get; private set; }
    public long Size { get; private set; }
    public DateTime ModifiedUtc { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public FileAttributes Attributes { get; private set; }
    /// <summary>Virtual items (Home, This PC, drives) carry a special path starting with "::" or a drive root.</summary>
    public bool IsVirtual { get; init; }
    public string? Subtitle { get; init; }
    public int IndexId { get; init; } = -1;

    private string? _typeName;
    private ImageSource? _icon;
    private bool _requested;
    private int _iconPx = 20;
    private bool _thumb;

    public string Location => IsVirtual ? "" : (Path.GetDirectoryName(FullPath) ?? "");
    public string DisplayName => !IsDirectory && !IsVirtual && !AppSettings.Current.Explorer.ShowExtensions && Extension.Length > 0 ? Name[..^(Extension.Length + 1)] : Name;
    public string TypeName => _typeName ??= Subtitle ?? (IsVirtual ? "" : ShellImaging.GetTypeName(Name, IsDirectory));

    /// <summary>A drive root shown with its volume label and free-space subtitle (real path, so it can be opened/dropped on).</summary>
    public static FileItem DriveVirtual(string label, string root, string subtitle)
        => new() { Name = label, FullPath = root, IsDirectory = true, Attributes = FileAttributes.Directory, Subtitle = subtitle };
    public string SizeText => IsDirectory || IsVirtual ? "" : SizeRange.Format(Size);
    public string ModifiedText => IsVirtual || ModifiedUtc == default ? "" : ModifiedUtc.ToLocalTime().ToString("g");
    public string CreatedText => IsVirtual || CreatedUtc == default ? "" : CreatedUtc.ToLocalTime().ToString("g");
    public string AttributesText => IsVirtual ? "" : AttrString(Attributes);
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public double Opacity => IsHidden ? 0.55 : 1.0;
    public int IconPx => _iconPx;

    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_requested && !IsVirtual)
            {
                _requested = true;
                int px = _iconPx;
                bool thumb = _thumb;
                _icon = App.Images.Request(FullPath, IsDirectory, px, thumb, ModifiedUtc.Ticks, img =>
                {
                    if (_iconPx != px || _thumb != thumb) return; // stale
                    _icon = img;
                    OnPropertyChanged(nameof(Icon));
                });
            }
            return _icon;
        }
    }

    public void SetIconSize(int px, bool thumbnail)
    {
        if (_iconPx == px && _thumb == thumbnail) return;
        _iconPx = px; _thumb = thumbnail; _icon = null; _requested = false;
        OnPropertyChanged(nameof(Icon)); OnPropertyChanged(nameof(IconPx));
    }

    public void SetIcon(ImageSource? icon) { _icon = icon; _requested = true; OnPropertyChanged(nameof(Icon)); }

    public static FileItem FromInfo(FileSystemInfo info)
    {
        bool dir = (info.Attributes & FileAttributes.Directory) != 0;
        return new FileItem
        {
            Name = info.Name, FullPath = info.FullName, IsDirectory = dir,
            Extension = dir ? "" : FileTypes.ExtensionOf(info.Name),
            Size = dir ? 0 : (info as FileInfo)?.Length ?? 0,
            ModifiedUtc = SafeUtc(() => info.LastWriteTimeUtc), CreatedUtc = SafeUtc(() => info.CreationTimeUtc),
            Attributes = info.Attributes,
        };
    }

    public static FileItem FromPath(string path)
    {
        if (Directory.Exists(path)) return FromInfo(new DirectoryInfo(path));
        if (File.Exists(path)) return FromInfo(new FileInfo(path));
        return new FileItem { Name = Path.GetFileName(path), FullPath = path, Extension = FileTypes.ExtensionOf(path) };
    }

    public static FileItem FromEntry(FileIndex index, int id)
    {
        ref var e = ref index[id];
        string name = e.Name ?? "";
        return new FileItem
        {
            Name = name, FullPath = index.PathOf(id), IsDirectory = e.IsDirectory,
            Extension = e.IsDirectory ? "" : FileTypes.ExtensionOf(name),
            Size = e.Size, ModifiedUtc = new DateTime(Math.Max(0, e.Modified), DateTimeKind.Utc), CreatedUtc = new DateTime(Math.Max(0, e.Created), DateTimeKind.Utc),
            Attributes = e.Attributes, IndexId = id,
        };
    }

    public static FileItem Virtual(string name, string path, string? subtitle = null, ImageSource? icon = null)
    {
        var item = new FileItem { Name = name, FullPath = path, IsDirectory = true, IsVirtual = true, Subtitle = subtitle };
        if (icon != null) item.SetIcon(icon);
        return item;
    }

    public void Refresh()
    {
        try
        {
            FileSystemInfo info = IsDirectory ? new DirectoryInfo(FullPath) : new FileInfo(FullPath);
            if (!info.Exists) return;
            Name = info.Name; Size = (info as FileInfo)?.Length ?? 0; ModifiedUtc = info.LastWriteTimeUtc; Attributes = info.Attributes;
            OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(ModifiedText));
        }
        catch { }
    }

    private static DateTime SafeUtc(Func<DateTime> f) { try { return f(); } catch { return default; } }

    private static string AttrString(FileAttributes a)
    {
        var sb = new System.Text.StringBuilder(6);
        if ((a & FileAttributes.ReadOnly) != 0) sb.Append('R');
        if ((a & FileAttributes.Hidden) != 0) sb.Append('H');
        if ((a & FileAttributes.System) != 0) sb.Append('S');
        if ((a & FileAttributes.Archive) != 0) sb.Append('A');
        if ((a & FileAttributes.ReparsePoint) != 0) sb.Append('L');
        if ((a & FileAttributes.Compressed) != 0) sb.Append('C');
        if ((a & FileAttributes.Encrypted) != 0) sb.Append('E');
        return sb.ToString();
    }

    public override string ToString() => FullPath;
}
