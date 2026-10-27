using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.Platform;
using FileHub.UI.Common;

namespace FileHub.UI.Explorer;

/// <summary>State of one Explorer tab: location, listing, history, view/sort, and in-folder search.</summary>
public sealed class ExplorerTab : ObservableObject
{
    public const string HomePath = "::Home";
    public const string ThisPcPath = "::ThisPC";

    private string _path = HomePath;
    private string _title = "Home";
    private List<FileItem> _items = new();
    private bool _isLoading;
    private string _status = "";
    private ViewMode _view = AppSettings.Current.Explorer.DefaultView;
    private string _sortBy = AppSettings.Current.Explorer.DefaultSortBy;
    private bool _sortDesc = AppSettings.Current.Explorer.DefaultSortDescending;
    private List<ColumnSpec> _columns = AppSettings.Current.Explorer.DefaultColumns.Select(c => new ColumnSpec { Key = c.Key, Width = c.Width }).ToList();
    private bool _isSearch;
    private string _searchText = "";
    private readonly List<string> _back = new(), _forward = new();
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private bool _hasCustomView;

    public string Path { get => _path; private set { if (Set(ref _path, value)) { OnPropertyChanged(nameof(IsVirtual)); OnPropertyChanged(nameof(CanGoUp)); } } }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public List<FileItem> Items { get => _items; private set => Set(ref _items, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ViewMode View { get => _view; private set => Set(ref _view, value); }
    public string SortBy { get => _sortBy; private set => Set(ref _sortBy, value); }
    public bool SortDescending { get => _sortDesc; private set => Set(ref _sortDesc, value); }
    public List<ColumnSpec> Columns { get => _columns; private set => Set(ref _columns, value); }
    public bool IsSearch { get => _isSearch; private set => Set(ref _isSearch, value); }
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public bool IsVirtual => _path.StartsWith("::");
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => !IsVirtual || _path == ThisPcPath;

    public event Action? Navigated;
    public event Action<string>? SelectAfterLoad;

    /// <summary>Asks the pane to select this path once the current listing has loaded.</summary>
    public void RequestSelect(string path) => SelectAfterLoad?.Invoke(path);

    public ExplorerTab(string? path = null) => Navigate(path ?? AppSettings.Current.Explorer.StartLocation, false);

    // ------------------------------------------------------------------ navigation

    public void Navigate(string path, bool record = true)
    {
        path = Normalize(path);
        if (record && !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            _back.Add(_path); _forward.Clear();
            if (_back.Count > 100) _back.RemoveAt(0);
        }
        Path = path;
        Title = TitleFor(path);
        IsSearch = false; SearchText = "";
        LoadFolderView();
        _ = LoadAsync();
        OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward));
        Navigated?.Invoke();
    }

    public void Back()
    {
        if (_back.Count == 0) return;
        var p = _back[^1]; _back.RemoveAt(_back.Count - 1);
        _forward.Add(_path);
        Navigate(p, false);
    }

    public void Forward()
    {
        if (_forward.Count == 0) return;
        var p = _forward[^1]; _forward.RemoveAt(_forward.Count - 1);
        _back.Add(_path);
        Navigate(p, false);
    }

    public void Up()
    {
        if (_path == ThisPcPath) { Navigate(HomePath); return; }
        if (IsVirtual) return;
        var parent = System.IO.Path.GetDirectoryName(_path.TrimEnd('\\'));
        string child = _path;
        Navigate(parent ?? ThisPcPath);
        SelectAfterLoad?.Invoke(child);
    }

    public void Refresh()
    {
        if (IsSearch) { _ = SearchAsync(SearchText); return; }
        _ = LoadAsync();
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return HomePath;
        path = path.Trim();
        if (path.StartsWith("::")) return path;
        if (path.Length == 2 && path[1] == ':') path += "\\";
        try { path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); } catch { }
        if (path.Length > 3) path = path.TrimEnd('\\');
        return path;
    }

    private static string TitleFor(string path) => path switch
    {
        HomePath => "Home",
        ThisPcPath => "This PC",
        _ => path.Length <= 3 ? DriveLabel(path) : System.IO.Path.GetFileName(path),
    };

    public static string DriveLabel(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            string label = d.IsReady && !string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.VolumeLabel : d.DriveType == DriveType.Fixed ? "Local Disk" : d.DriveType.ToString();
            return $"{label} ({root.TrimEnd('\\')})";
        }
        catch { return root; }
    }

    // ------------------------------------------------------------------ loading

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        string path = _path;
        try
        {
            var items = await Task.Run(() => Enumerate(path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            SortItems(items);
            Items = items;
            Status = IsVirtual ? $"{items.Count} items" : $"{items.Count:N0} items";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) { Items = new List<FileItem>(); Status = ex.Message; }
        }
        finally { if (_loadCts == cts) IsLoading = false; }
    }

    private static List<FileItem> Enumerate(string path, CancellationToken ct)
    {
        var list = new List<FileItem>();
        if (path == HomePath)
        {
            foreach (var (name, p) in KnownFolders.UserFolders()) list.Add(FileItem.FromInfo(new DirectoryInfo(p)));
            foreach (var p in AppSettings.Current.Explorer.QuickAccess)
                if (Directory.Exists(p) && !list.Any(i => string.Equals(i.FullPath, p, StringComparison.OrdinalIgnoreCase))) list.Add(FileItem.FromInfo(new DirectoryInfo(p)));
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady) list.Add(DriveItem(d)); } catch { }
            }
            return list;
        }
        if (path == ThisPcPath)
        {
            foreach (var d in DriveInfo.GetDrives()) { try { if (d.IsReady) list.Add(DriveItem(d)); } catch { } }
            return list;
        }
        var ex = AppSettings.Current.Explorer;
        var skip = FileAttributes.None;
        if (!ex.ShowHidden) skip |= FileAttributes.Hidden;
        if (!ex.ShowSystem) skip |= FileAttributes.System;
        var options = new EnumerationOptions { AttributesToSkip = skip, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException($"Folder not found: {path}");
        int n = 0;
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
        {
            list.Add(FileItem.FromInfo(info));
            if ((++n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
        }
        return list;
    }

    private static FileItem DriveItem(DriveInfo d)
    {
        string root = d.RootDirectory.FullName;
        string sub;
        try { sub = $"{SizeRange.Format(d.AvailableFreeSpace)} free of {SizeRange.Format(d.TotalSize)}"; } catch { sub = d.DriveType.ToString(); }
        return FileItem.DriveVirtual(DriveLabel(root), root, sub);
    }

    // ------------------------------------------------------------------ sorting / view

    public void SortItems(List<FileItem> items)
    {
        bool foldersFirst = AppSettings.Current.Explorer.FoldersFirst;
        int sign = _sortDesc ? -1 : 1;
        Comparison<FileItem> cmp = _sortBy switch
        {
            "Modified" => (a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
            "Created" => (a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc),
            "Size" => (a, b) => a.Size.CompareTo(b.Size),
            "Type" => (a, b) => string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase),
            "Extension" => (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            "Location" => (a, b) => string.Compare(a.Location, b.Location, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => SearchEngine.CompareNames(a.Name, b.Name),
        };
        items.Sort((a, b) =>
        {
            if (foldersFirst && a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int r = cmp(a, b);
            if (r == 0) r = SearchEngine.CompareNames(a.Name, b.Name);
            return sign * r;
        });
    }

    public void SetSort(string key, bool? descending = null)
    {
        if (descending == null) _sortDesc = string.Equals(key, _sortBy, StringComparison.OrdinalIgnoreCase) ? !_sortDesc : false;
        else _sortDesc = descending.Value;
        _sortBy = key;
        OnPropertyChanged(nameof(SortBy)); OnPropertyChanged(nameof(SortDescending));
        var items = _items.ToList();
        SortItems(items);
        Items = items;
        PersistView();
    }

    public void SetView(ViewMode mode) { View = mode; PersistView(); }

    public void SaveColumns(List<ColumnSpec> cols)
    {
        if (cols.Count == 0) return;
        _columns = cols;
        PersistView();
    }

    private void LoadFolderView()
    {
        var ex = AppSettings.Current.Explorer;
        _hasCustomView = false;
        try
        {
            var json = IsVirtual ? null : App.Db.GetFolderView(_path);
            if (json != null && JsonSerializer.Deserialize<FolderView>(json) is { } fv)
            {
                _hasCustomView = true;
                View = fv.View; _sortBy = fv.SortBy; _sortDesc = fv.SortDescending;
                Columns = fv.Columns.Count > 0 ? fv.Columns : ex.DefaultColumns.Select(c => new ColumnSpec { Key = c.Key, Width = c.Width }).ToList();
                OnPropertyChanged(nameof(SortBy)); OnPropertyChanged(nameof(SortDescending));
                return;
            }
        }
        catch (Exception e) { Log.Warn("Folder view load failed: " + e.Message); }
        View = ex.DefaultView; _sortBy = ex.DefaultSortBy; _sortDesc = ex.DefaultSortDescending;
        Columns = ex.DefaultColumns.Select(c => new ColumnSpec { Key = c.Key, Width = c.Width }).ToList();
        OnPropertyChanged(nameof(SortBy)); OnPropertyChanged(nameof(SortDescending));
    }

    private void PersistView()
    {
        if (IsVirtual) return;
        var fv = new FolderView { View = _view, SortBy = _sortBy, SortDescending = _sortDesc, Columns = _columns };
        string path = _path;
        Task.Run(() => { try { App.Db.SetFolderView(path, JsonSerializer.Serialize(fv)); } catch (Exception e) { Log.Warn("Folder view save failed: " + e.Message); } });
    }

    // ------------------------------------------------------------------ search

    public async Task SearchAsync(string text)
    {
        _searchCts?.Cancel();
        SearchText = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (IsSearch) { IsSearch = false; await LoadAsync(); }
            return;
        }
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearch = true; IsLoading = true;
        var s = AppSettings.Current.Search;
        var options = new SearchOptions
        {
            MaxResults = s.MaxResults, RegexMode = s.RegexMode, MatchCase = s.MatchCase, WholeWord = s.WholeWord, MatchPath = s.MatchPath,
            ScopePath = IsVirtual ? null : _path, Sort = SortKey.None,
        };
        try
        {
            var result = await Task.Run(() => App.Search.Search(text, options, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            var items = new List<FileItem>(result.Ids.Length);
            foreach (var id in result.Ids) items.Add(FileItem.FromEntry(result.Index, id));
            if (!IsVirtual)
            {
                // live fallback: the index may not include very recent files in this folder
                SortItems(items);
            }
            Items = items;
            Status = result.Truncated
                ? $"{result.TotalMatches:N0} results (showing {result.Ids.Length:N0}) · {result.Elapsed.TotalMilliseconds:F0} ms"
                : $"{result.TotalMatches:N0} results · {result.Elapsed.TotalMilliseconds:F0} ms";
            if (result.Error != null) Status += " · " + result.Error;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "Search failed: " + ex.Message; }
        finally { if (_searchCts == cts) IsLoading = false; }
    }

    public void CancelLoads() { _loadCts?.Cancel(); _searchCts?.Cancel(); }
}

/// <summary>Node of the navigation tree (left pane).</summary>
public sealed class NavNode : ObservableObject
{
    private bool _isExpanded, _isSelected, _loaded;
    private ImageSource? _icon;

    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Glyph { get; init; }
    public bool IsHeader { get; init; }
    public ObservableCollection<NavNode> Children { get; } = new();
    public bool HasChildrenOnDisk { get; init; } = true;

    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && Glyph == null && !Path.StartsWith("::"))
                _icon = App.Images.Request(Path, true, 20, false, 0, img => { _icon = img; OnPropertyChanged(nameof(Icon)); });
            return _icon;
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value) && value) EnsureLoaded(); }
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public void AddPlaceholder() { if (HasChildrenOnDisk && !Path.StartsWith("::")) Children.Add(new NavNode { Name = "…", Path = "", IsHeader = true, HasChildrenOnDisk = false }); }

    public void EnsureLoaded()
    {
        if (_loaded || Path.StartsWith("::") || Path.Length == 0) return;
        _loaded = true;
        string path = Path;
        Task.Run(() =>
        {
            var dirs = new List<NavNode>();
            try
            {
                var skip = FileAttributes.Hidden | FileAttributes.System;
                if (AppSettings.Current.Explorer.ShowHidden) skip &= ~FileAttributes.Hidden;
                foreach (var d in new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = skip, IgnoreInaccessible = true }))
                    dirs.Add(new NavNode { Name = d.Name, Path = d.FullName });
                dirs.Sort((a, b) => SearchEngine.CompareNames(a.Name, b.Name));
            }
            catch { }
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                Children.Clear();
                foreach (var d in dirs) { d.AddPlaceholder(); Children.Add(d); }
            });
        });
    }

    public void Reload() { _loaded = false; if (_isExpanded) EnsureLoaded(); }
}
