using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileHub.Platform;

namespace FileHub.Core.Settings;

public abstract class SettingsBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum ThemeMode { System, Light, Dark }
public enum ViewMode { Details, List, SmallIcons, MediumIcons, LargeIcons, ExtraLargeIcons }
public enum ShellMenuMode { Inline, Submenu, Hidden }
public enum StartMenuPosition { Left, Center }

public sealed class GeneralSettings : SettingsBase
{
    private ThemeMode _theme = ThemeMode.System;
    private bool _startWithWindows;
    private string _hubHotkey = "Ctrl+Alt+Space";
    private bool _showTrayIcon = true;
    private bool _keepRunningInBackground = true;

    public ThemeMode Theme { get => _theme; set => Set(ref _theme, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public string HubHotkey { get => _hubHotkey; set => Set(ref _hubHotkey, value); }
    public bool ShowTrayIcon { get => _showTrayIcon; set => Set(ref _showTrayIcon, value); }
    public bool KeepRunningInBackground { get => _keepRunningInBackground; set => Set(ref _keepRunningInBackground, value); }
}

public sealed class IndexSettings : SettingsBase
{
    private bool _indexAllFixedDrives = true;
    private bool _indexRemovableDrives;
    private bool _rebuildOnStartup;
    private int _rebuildIntervalHours = 24;
    private bool _contentIndexing = true;
    private int _contentMaxMB = 4;
    private bool _metadataIndexing = true;
    private bool _thumbnailCaching = true;
    private bool _realTimeIndexing = true;

    /// <summary>Apply file-system changes to the index as they happen (FileSystemWatcher). Off = index only changes on rebuild.</summary>
    public bool RealTimeIndexing { get => _realTimeIndexing; set => Set(ref _realTimeIndexing, value); }
    public bool IndexAllFixedDrives { get => _indexAllFixedDrives; set => Set(ref _indexAllFixedDrives, value); }
    public bool IndexRemovableDrives { get => _indexRemovableDrives; set => Set(ref _indexRemovableDrives, value); }
    /// <summary>Explicit roots (used when IndexAllFixedDrives is false, or in addition to drives).</summary>
    public List<string> Roots { get; set; } = new();
    /// <summary>Folder names (e.g. "node_modules") or full paths to skip.</summary>
    public List<string> Excludes { get; set; } = new() { "$Recycle.Bin", "System Volume Information" };
    public bool RebuildOnStartup { get => _rebuildOnStartup; set => Set(ref _rebuildOnStartup, value); }
    public int RebuildIntervalHours { get => _rebuildIntervalHours; set => Set(ref _rebuildIntervalHours, value); }
    public bool ContentIndexing { get => _contentIndexing; set => Set(ref _contentIndexing, value); }
    public int ContentMaxMB { get => _contentMaxMB; set => Set(ref _contentMaxMB, value); }
    public List<string> ContentExtensions { get; set; } = new()
    {
        "txt","md","log","json","xml","yaml","yml","ini","cfg","conf","toml","csv","rtf","html","htm","css","js","ts","jsx","tsx",
        "cs","csproj","sln","vb","fs","py","java","kt","c","cpp","h","hpp","rs","go","rb","php","swift","sql","ps1","psm1","bat","cmd","sh",
        "docx","xlsx","pptx","reg","nfo","srt","vtt"
    };
    public bool MetadataIndexing { get => _metadataIndexing; set => Set(ref _metadataIndexing, value); }
    public List<string> MetadataExtensions { get; set; } = new()
    {
        "jpg","jpeg","png","gif","bmp","tif","tiff","heic","webp","mp4","mkv","avi","mov","wmv","m4v","mp3","flac","wav","m4a","wma","ogg","aac",
        "docx","xlsx","pptx","pdf","doc","xls","ppt","exe","dll"
    };
    public bool ThumbnailCaching { get => _thumbnailCaching; set => Set(ref _thumbnailCaching, value); }
}

public sealed class SearchSettings : SettingsBase
{
    private int _maxResults = 5000;
    private bool _matchPath;
    private bool _matchCase;
    private bool _regexMode;
    private bool _wholeWord;

    public int MaxResults { get => _maxResults; set => Set(ref _maxResults, value); }
    public bool MatchPath { get => _matchPath; set => Set(ref _matchPath, value); }
    public bool MatchCase { get => _matchCase; set => Set(ref _matchCase, value); }
    public bool RegexMode { get => _regexMode; set => Set(ref _regexMode, value); }
    public bool WholeWord { get => _wholeWord; set => Set(ref _wholeWord, value); }
}

public sealed class ColumnSpec
{
    public string Key { get; set; } = "Name";
    public double Width { get; set; } = 300;
}

public sealed class FolderView
{
    public ViewMode View { get; set; } = ViewMode.Details;
    public string SortBy { get; set; } = "Name";
    public bool SortDescending { get; set; }
    public List<ColumnSpec> Columns { get; set; } = new();
}

public sealed class ExplorerSettings : SettingsBase
{
    private ViewMode _defaultView = ViewMode.Details;
    private bool _showHidden;
    private bool _showSystem;
    private bool _showExtensions = true;
    private bool _foldersFirst = true;
    private string _startLocation = "::Home";
    private bool _showNavigationPane = true;
    private bool _showPreviewPane;
    private bool _useIndexForSearch = true;
    private string _terminalCommand = "wt.exe -d \"%1\"";

    public ViewMode DefaultView { get => _defaultView; set => Set(ref _defaultView, value); }
    public string DefaultSortBy { get; set; } = "Name";
    public bool DefaultSortDescending { get; set; }
    public List<ColumnSpec> DefaultColumns { get; set; } = new()
    {
        new() { Key = "Name", Width = 340 }, new() { Key = "Modified", Width = 150 }, new() { Key = "Type", Width = 150 }, new() { Key = "Size", Width = 90 }
    };
    public bool ShowHidden { get => _showHidden; set => Set(ref _showHidden, value); }
    public bool ShowSystem { get => _showSystem; set => Set(ref _showSystem, value); }
    public bool ShowExtensions { get => _showExtensions; set => Set(ref _showExtensions, value); }
    public bool FoldersFirst { get => _foldersFirst; set => Set(ref _foldersFirst, value); }
    public string StartLocation { get => _startLocation; set => Set(ref _startLocation, value); }
    public bool ShowNavigationPane { get => _showNavigationPane; set => Set(ref _showNavigationPane, value); }
    public bool ShowPreviewPane { get => _showPreviewPane; set => Set(ref _showPreviewPane, value); }
    public bool UseIndexForSearch { get => _useIndexForSearch; set => Set(ref _useIndexForSearch, value); }
    public string TerminalCommand { get => _terminalCommand; set => Set(ref _terminalCommand, value); }
    public List<string> QuickAccess { get; set; } = new();
    /// <summary>Per-folder saved views keyed by full path.</summary>
    public Dictionary<string, FolderView> FolderViews { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CustomMenuItem : SettingsBase
{
    private string _label = "New item";
    private string _command = "";
    private string _arguments = "\"%1\"";
    private string _extensions = "";
    private bool _forFiles = true;
    private bool _forFolders = true;
    private bool _forBackground;
    private bool _enabled = true;
    private bool _runAsAdmin;

    public string Label { get => _label; set => Set(ref _label, value); }
    /// <summary>Program to run. %1 = selected path, %D = directory, %N = name, %* = all selected paths quoted.</summary>
    public string Command { get => _command; set => Set(ref _command, value); }
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
    /// <summary>Semicolon/comma separated extensions (no dot). Empty = all.</summary>
    public string Extensions { get => _extensions; set => Set(ref _extensions, value); }
    public bool ForFiles { get => _forFiles; set => Set(ref _forFiles, value); }
    public bool ForFolders { get => _forFolders; set => Set(ref _forFolders, value); }
    public bool ForBackground { get => _forBackground; set => Set(ref _forBackground, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool RunAsAdmin { get => _runAsAdmin; set => Set(ref _runAsAdmin, value); }
}

public sealed class ContextMenuSettings : SettingsBase
{
    private ShellMenuMode _shellMode = ShellMenuMode.Inline;
    private bool _showBuiltIns = true;
    private bool _showOpenTerminal = true;
    private bool _showCopyPath = true;
    private bool _showOpenInHub = true;

    public ShellMenuMode ShellMode { get => _shellMode; set => Set(ref _shellMode, value); }
    public bool ShowBuiltIns { get => _showBuiltIns; set => Set(ref _showBuiltIns, value); }
    public bool ShowOpenTerminal { get => _showOpenTerminal; set => Set(ref _showOpenTerminal, value); }
    public bool ShowCopyPath { get => _showCopyPath; set => Set(ref _showCopyPath, value); }
    public bool ShowOpenInHub { get => _showOpenInHub; set => Set(ref _showOpenInHub, value); }
    /// <summary>Shell items whose label contains one of these strings are hidden.</summary>
    public List<string> HiddenShellItems { get; set; } = new();
    public List<CustomMenuItem> Items { get; set; } = new()
    {
        new CustomMenuItem { Label = "Open in Notepad", Command = "notepad.exe", Arguments = "\"%1\"", ForFolders = false, Extensions = "txt;log;md;json;xml;ini;cfg;cs;js;ps1;bat" },
    };
}

public sealed class PinnedItem : SettingsBase
{
    private string _label = "";
    private string _path = "";
    private string _arguments = "";
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Path { get => _path; set => Set(ref _path, value); }
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
}

public sealed class StartMenuSettings : SettingsBase
{
    private bool _replaceWinKey = true;
    private bool _replaceWinE = true;
    private StartMenuPosition _position = StartMenuPosition.Left;
    private int _maxSearchResults = 8;
    private bool _showPowerButtons = true;
    private bool _showUserName = true;
    private bool _searchContent;

    public bool ReplaceWinKey { get => _replaceWinKey; set => Set(ref _replaceWinKey, value); }
    public bool ReplaceWinE { get => _replaceWinE; set => Set(ref _replaceWinE, value); }
    public StartMenuPosition Position { get => _position; set => Set(ref _position, value); }
    public int MaxSearchResults { get => _maxSearchResults; set => Set(ref _maxSearchResults, value); }
    public bool ShowPowerButtons { get => _showPowerButtons; set => Set(ref _showPowerButtons, value); }
    public bool ShowUserName { get => _showUserName; set => Set(ref _showUserName, value); }
    public bool SearchContent { get => _searchContent; set => Set(ref _searchContent, value); }
    /// <summary>Which Windows known folders to show as shortcuts.</summary>
    public List<string> WindowsFolders { get; set; } = new() { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "This PC" };
    public List<PinnedItem> Pinned { get; set; } = new()
    {
        new PinnedItem { Label = "File Hub", Path = "::Hub" },
        new PinnedItem { Label = "Explorer", Path = "::Explorer" },
        new PinnedItem { Label = "Settings", Path = "ms-settings:" },
        new PinnedItem { Label = "Terminal", Path = "wt.exe" },
        new PinnedItem { Label = "Notepad", Path = "notepad.exe" },
        new PinnedItem { Label = "Calculator", Path = "calc.exe" },
    };
}

public sealed class AppSettings : SettingsBase
{
    public GeneralSettings General { get; set; } = new();
    public IndexSettings Index { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public ExplorerSettings Explorer { get; set; } = new();
    public ContextMenuSettings ContextMenu { get; set; } = new();
    public StartMenuSettings StartMenu { get; set; } = new();
    /// <summary>FileHub-level "open with" overrides: extension (no dot) -> program path.</summary>
    public Dictionary<string, string> Associations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public static AppSettings Current { get; private set; } = new();
    public static event Action? Saved;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var json = File.ReadAllText(Paths.SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s != null) { Current = s; return s; }
            }
        }
        catch (Exception ex) { Log.Error("Failed to load settings; using defaults", ex); }
        Current = new AppSettings();
        return Current;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(Paths.SettingsFile, json);
            Saved?.Invoke();
        }
        catch (Exception ex) { Log.Error("Failed to save settings", ex); }
    }
}
