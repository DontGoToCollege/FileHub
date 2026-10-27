using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FileHub.Core.Indexing;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.UI.Common;

namespace FileHub.UI.Explorer;

public partial class ExplorerPane : UserControl
{
    public ExplorerTab Tab { get; }
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly ObservableCollection<NavNode> _navRoots = new();
    private bool _suppressNav;
    private string? _pendingSelect;
    private double _navWidth = 220;

    private sealed record Crumb(string Name, string Path, bool HasNext);

    public ExplorerPane(ExplorerTab tab)
    {
        InitializeComponent();
        Tab = tab;
        DataContext = tab;
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = Tab.SearchAsync(SearchBox.Text); };

        FileList.ItemActivated += OnItemActivated;
        FileList.ContextMenuRequested += OnContextMenu;
        FileList.SelectionChanged += UpdateSelectionStatus;
        FileList.SortRequested += key => Tab.SetSort(key);
        FileList.RenameCommitted += OnRename;
        FileList.FilesDropped += OnFilesDropped;
        FileList.CommandRequested += Execute;

        Tab.PropertyChanged += Tab_PropertyChanged;
        Tab.Navigated += () => { UpdateCrumbs(); UpdateNavSelection(); };
        Tab.SelectAfterLoad += p => _pendingSelect = p;

        BuildNavTree();
        NavTree.ItemsSource = _navRoots;
        UpdateCrumbs();
        ApplyTabState();
        App.Index.StatusChanged += OnIndexStatus;
        OnIndexStatus();
        Loaded += (_, _) => { UpdateNavSelection(); if (AppSettings.Current.Explorer.ShowPreviewPane) { PreviewToggle.IsChecked = true; TogglePreview(true); } if (!AppSettings.Current.Explorer.ShowNavigationPane) { NavToggle.IsChecked = false; ToggleNav(false); } };
        Unloaded += (_, _) => App.Index.StatusChanged -= OnIndexStatus;
    }

    private void OnIndexStatus()
    {
        Dispatcher.BeginInvoke(() => IndexStatusText.Text = "Index: " + App.Index.StatusText);
    }

    // ------------------------------------------------------------------ tab state -> UI

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ExplorerTab.Items):
                FileList.Items = Tab.Items;
                FileList.CurrentFolder = Tab.IsVirtual ? null : Tab.Path;
                FileList.ShowLocationColumn = Tab.IsSearch;
                UpdateSelectionStatus();
                if (_pendingSelect != null) { FileList.SelectPath(_pendingSelect); _pendingSelect = null; }
                break;
            case nameof(ExplorerTab.IsLoading):
                Busy.Visibility = Tab.IsLoading ? Visibility.Visible : Visibility.Collapsed; break;
            case nameof(ExplorerTab.Status):
                StatusText.Text = Tab.Status; break;
            case nameof(ExplorerTab.View):
                FileList.ViewMode = Tab.View; break;
            case nameof(ExplorerTab.Columns):
                FileList.Columns = Tab.Columns; break;
            case nameof(ExplorerTab.SortBy):
            case nameof(ExplorerTab.SortDescending):
                FileList.SetSort(Tab.SortBy, Tab.SortDescending); break;
            case nameof(ExplorerTab.IsSearch):
                FileList.ShowLocationColumn = Tab.IsSearch;
                FileList.EmptyMessage = Tab.IsSearch ? "No results." : "This folder is empty.";
                if (!Tab.IsSearch && SearchBox.Text.Length > 0) { SearchBox.Text = ""; }
                break;
        }
    }

    private void ApplyTabState()
    {
        FileList.ViewMode = Tab.View;
        FileList.Columns = Tab.Columns;
        FileList.SetSort(Tab.SortBy, Tab.SortDescending);
        FileList.Items = Tab.Items;
        FileList.CurrentFolder = Tab.IsVirtual ? null : Tab.Path;
        StatusText.Text = Tab.Status;
    }

    private void UpdateCrumbs()
    {
        BackBtn.IsEnabled = Tab.CanGoBack; FwdBtn.IsEnabled = Tab.CanGoForward; UpBtn.IsEnabled = Tab.CanGoUp;
        var list = new List<Crumb>();
        if (Tab.IsVirtual) list.Add(new Crumb(Tab.Title, Tab.Path, false));
        else
        {
            list.Add(new Crumb("This PC", ExplorerTab.ThisPcPath, true));
            var parts = Tab.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            string acc = "";
            for (int i = 0; i < parts.Length; i++)
            {
                acc = i == 0 ? parts[0] + "\\" : Path.Combine(acc, parts[i]);
                string name = i == 0 ? ExplorerTab.DriveLabel(acc) : parts[i];
                list.Add(new Crumb(name, acc, i < parts.Length - 1));
            }
        }
        Crumbs.ItemsSource = list;
        CrumbScroll.ScrollToRightEnd();
        Window.GetWindow(this)?.SetValue(Window.TitleProperty, Tab.Title + " - FileHub");
    }

    private void UpdateSelectionStatus()
    {
        var sel = FileList.SelectedItems;
        if (sel.Count == 0) { SelectionText.Text = ""; }
        else
        {
            long size = sel.Where(i => !i.IsDirectory).Sum(i => i.Size);
            SelectionText.Text = sel.Count == 1 ? $"1 item selected  {sel[0].SizeText}" : $"{sel.Count} items selected  {(size > 0 ? Core.Search.SizeRange.Format(size) : "")}";
        }
        if (Preview.Visibility == Visibility.Visible) Preview.Show(sel.Count == 1 ? sel[0] : null);
    }

    // ------------------------------------------------------------------ toolbar

    private void Back_Click(object sender, RoutedEventArgs e) => Tab.Back();
    private void Forward_Click(object sender, RoutedEventArgs e) => Tab.Forward();
    private void Up_Click(object sender, RoutedEventArgs e) => Tab.Up();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        Tab.Refresh();
        foreach (var n in _navRoots) ReloadNode(n);
    }

    private static void ReloadNode(NavNode n) { n.Reload(); foreach (var c in n.Children) if (c.IsExpanded) ReloadNode(c); }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string p) Tab.Navigate(p);
        e.Handled = true;
    }

    private void Address_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
        BeginAddressEdit();
    }

    public void BeginAddressEdit()
    {
        CrumbScroll.Visibility = Visibility.Collapsed;
        AddressBox.Text = Tab.IsVirtual ? Tab.Title : Tab.Path;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        CrumbScroll.Visibility = Visibility.Visible;
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string text = AddressBox.Text.Trim();
            EndAddressEdit();
            NavigateText(text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { EndAddressEdit(); FileList.FocusList(); e.Handled = true; }
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (AddressBox.Visibility == Visibility.Visible) EndAddressEdit(); }

    private void NavigateText(string text)
    {
        if (text.Length == 0) return;
        if (text.Equals("Home", StringComparison.OrdinalIgnoreCase)) { Tab.Navigate(ExplorerTab.HomePath); return; }
        if (text.Equals("This PC", StringComparison.OrdinalIgnoreCase)) { Tab.Navigate(ExplorerTab.ThisPcPath); return; }
        string expanded = Environment.ExpandEnvironmentVariables(text);
        if (Directory.Exists(expanded)) { Tab.Navigate(expanded); FileList.FocusList(); }
        else if (File.Exists(expanded)) ShellUtil.Open(expanded, Hwnd);
        else
        {
            // treat as a command or URL (like Explorer's address bar)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(expanded) { UseShellExecute = true }); }
            catch { MessageBox.Show(Window.GetWindow(this), $"Windows can't find '{text}'.", "FileHub", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }

    public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool has = SearchBox.Text.Length > 0;
        SearchHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop(); _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; FileList.FocusList(); e.Handled = true; }
        else if (e.Key == Key.Enter) { _searchTimer.Stop(); _ = Tab.SearchAsync(SearchBox.Text); FileList.FocusList(); e.Handled = true; }
        else if (e.Key == Key.Down) { FileList.FocusList(); e.Handled = true; }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; FileList.FocusList(); }

    // ------------------------------------------------------------------ command bar

    private void Cmd_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement f && f.Tag is string cmd) Execute(cmd); }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var f = new MenuItem { Header = "Folder", InputGestureText = "Ctrl+Shift+N" }; f.Click += (_, _) => Execute("newfolder"); menu.Items.Add(f);
        var t = new MenuItem { Header = "Text Document" }; t.Click += (_, _) => Execute("newfile"); menu.Items.Add(t);
        OpenFlyout(menu, (FrameworkElement)sender);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (label, key) in new[] { ("Name", "Name"), ("Date modified", "Modified"), ("Type", "Type"), ("Size", "Size"), ("Date created", "Created") })
        {
            var mi = new MenuItem { Header = label, IsChecked = Tab.SortBy == key }; var k = key; mi.Click += (_, _) => Tab.SetSort(k, Tab.SortDescending); menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var asc = new MenuItem { Header = "Ascending", IsChecked = !Tab.SortDescending }; asc.Click += (_, _) => Tab.SetSort(Tab.SortBy, false); menu.Items.Add(asc);
        var desc = new MenuItem { Header = "Descending", IsChecked = Tab.SortDescending }; desc.Click += (_, _) => Tab.SetSort(Tab.SortBy, true); menu.Items.Add(desc);
        OpenFlyout(menu, (FrameworkElement)sender);
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (label, mode) in new[] { ("Extra large icons", ViewMode.ExtraLargeIcons), ("Large icons", ViewMode.LargeIcons), ("Medium icons", ViewMode.MediumIcons), ("Small icons", ViewMode.SmallIcons), ("List", ViewMode.List), ("Details", ViewMode.Details) })
        {
            var mi = new MenuItem { Header = label, IsChecked = Tab.View == mode }; var m = mode; mi.Click += (_, _) => Tab.SetView(m); menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var cols = new MenuItem { Header = "Columns" };
        foreach (var c in Controls.FileListControl.AllColumns)
        {
            if (c.Key == "Location") continue;
            var mi = new MenuItem { Header = c.Header, IsCheckable = true, IsChecked = Tab.Columns.Any(x => x.Key == c.Key), IsEnabled = c.Key != "Name" };
            var key = c.Key; var width = c.Width;
            mi.Click += (_, _) =>
            {
                var list = FileList.SnapshotColumns();
                if (list.Any(x => x.Key == key)) list.RemoveAll(x => x.Key == key); else list.Add(new ColumnSpec { Key = key, Width = width });
                Tab.SaveColumns(list); FileList.Columns = list;
            };
            cols.Items.Add(mi);
        }
        menu.Items.Add(cols);
        var hidden = new MenuItem { Header = "Show hidden items", IsCheckable = true, IsChecked = AppSettings.Current.Explorer.ShowHidden };
        hidden.Click += (_, _) => { AppSettings.Current.Explorer.ShowHidden = hidden.IsChecked; AppSettings.Current.Save(); Refresh(); };
        menu.Items.Add(hidden);
        var ext = new MenuItem { Header = "Show file extensions", IsCheckable = true, IsChecked = AppSettings.Current.Explorer.ShowExtensions };
        ext.Click += (_, _) => { AppSettings.Current.Explorer.ShowExtensions = ext.IsChecked; AppSettings.Current.Save(); Refresh(); };
        menu.Items.Add(ext);
        OpenFlyout(menu, (FrameworkElement)sender);
    }

    private static void OpenFlyout(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void NavToggle_Click(object sender, RoutedEventArgs e) => ToggleNav(NavToggle.IsChecked == true);
    private void ToggleNav(bool show)
    {
        if (show) NavCol.Width = new GridLength(_navWidth);
        else { if (NavCol.Width.Value > 0) _navWidth = NavCol.Width.Value; NavCol.Width = new GridLength(0); }
        AppSettings.Current.Explorer.ShowNavigationPane = show;
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => TogglePreview(PreviewToggle.IsChecked == true);
    public void TogglePreview(bool? show = null)
    {
        bool s = show ?? Preview.Visibility != Visibility.Visible;
        PreviewToggle.IsChecked = s;
        Preview.Visibility = s ? Visibility.Visible : Visibility.Collapsed;
        PreviewCol.Width = s ? new GridLength(300) : new GridLength(0);
        AppSettings.Current.Explorer.ShowPreviewPane = s;
        if (s) UpdateSelectionStatus();
    }

    private void Hub_Click(object sender, RoutedEventArgs e) => App.ShowHub(Tab.IsVirtual ? null : $"infolder:\"{Tab.Path}\" ");

    // ------------------------------------------------------------------ nav tree

    private void BuildNavTree()
    {
        _navRoots.Clear();
        _navRoots.Add(new NavNode { Name = "Home", Path = ExplorerTab.HomePath, Glyph = "", HasChildrenOnDisk = false });
        foreach (var (name, path) in KnownFolders.UserFolders())
        {
            var n = new NavNode { Name = name, Path = path }; n.AddPlaceholder(); _navRoots.Add(n);
        }
        foreach (var p in AppSettings.Current.Explorer.QuickAccess)
        {
            if (!Directory.Exists(p)) continue;
            var n = new NavNode { Name = Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } nm ? nm : p, Path = p }; n.AddPlaceholder(); _navRoots.Add(n);
        }
        var pc = new NavNode { Name = "This PC", Path = ExplorerTab.ThisPcPath, Glyph = "" };
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var n = new NavNode { Name = ExplorerTab.DriveLabel(d.RootDirectory.FullName), Path = d.RootDirectory.FullName }; n.AddPlaceholder(); pc.Children.Add(n);
            }
            catch { }
        }
        pc.IsExpanded = true;
        _navRoots.Add(pc);
    }

    private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressNav || e.NewValue is not NavNode n || n.IsHeader || n.Path.Length == 0) return;
        if (!string.Equals(n.Path, Tab.Path, StringComparison.OrdinalIgnoreCase)) Tab.Navigate(n.Path);
    }

    private void UpdateNavSelection()
    {
        _suppressNav = true;
        try
        {
            var node = FindNode(_navRoots, Tab.Path);
            foreach (var n in AllNodes(_navRoots)) if (n.IsSelected && n != node) n.IsSelected = false;
            if (node != null) node.IsSelected = true;
        }
        finally { _suppressNav = false; }
    }

    private static NavNode? FindNode(IEnumerable<NavNode> nodes, string path)
    {
        foreach (var n in nodes)
        {
            if (string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)) return n;
            var f = FindNode(n.Children, path);
            if (f != null) return f;
        }
        return null;
    }

    private static IEnumerable<NavNode> AllNodes(IEnumerable<NavNode> nodes)
    {
        foreach (var n in nodes) { yield return n; foreach (var c in AllNodes(n.Children)) yield return c; }
    }

    private void NavTree_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        var tvi = FindAncestor<TreeViewItem>(d);
        if (tvi?.DataContext is not NavNode n || n.Path.StartsWith("::") || n.Path.Length == 0) return;
        _suppressNav = true; n.IsSelected = true; _suppressNav = false;
        var item = FileItem.FromPath(n.Path);
        var menu = ContextMenuBuilder.Build(this, new[] { item }, Path.GetDirectoryName(n.Path), cmd => ExecuteFor(cmd, new[] { item }));
        menu.PlacementTarget = tvi; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint; menu.IsOpen = true;
        e.Handled = true;
    }

    private void NavTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.OriginalSource is DependencyObject d && FindAncestor<TreeViewItem>(d)?.DataContext is NavNode n && !n.Path.StartsWith("::") && n.Path.Length > 0)
            e.Effects = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void NavTree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.OriginalSource is not DependencyObject d || FindAncestor<TreeViewItem>(d)?.DataContext is not NavNode n || n.Path.StartsWith("::")) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        OnFilesDropped(files, n.Path, !e.KeyStates.HasFlag(DragDropKeyStates.ControlKey));
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null) { if (d is T t) return t; d = VisualTreeHelper.GetParent(d) ?? (d as FrameworkContentElement)?.Parent!; }
        return null;
    }

    // ------------------------------------------------------------------ list events

    private IntPtr Hwnd => Window.GetWindow(this) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;

    private void OnItemActivated(FileItem item)
    {
        if (item.IsDirectory)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && Window.GetWindow(this) is ExplorerWindow ew) ew.OpenTab(item.FullPath);
            else Tab.Navigate(item.FullPath);
        }
        else ShellUtil.Open(item.FullPath, Hwnd);
    }

    private void OnContextMenu(IReadOnlyList<FileItem> items, bool background)
    {
        var sel = background ? Array.Empty<FileItem>() : items;
        var menu = ContextMenuBuilder.Build(this, sel, Tab.IsVirtual ? null : Tab.Path, cmd => ExecuteFor(cmd, sel));
        menu.PlacementTarget = FileList; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint; menu.IsOpen = true;
    }

    private void OnRename(FileItem item, string newName)
    {
        if (FileOperations.Rename(Hwnd, item.FullPath, newName))
        {
            string newPath = Path.Combine(Path.GetDirectoryName(item.FullPath) ?? "", newName);
            _pendingSelect = newPath;
            Tab.Refresh();
        }
    }

    private void OnFilesDropped(string[] files, string target, bool move)
    {
        if (move) FileOperations.Move(Hwnd, files, target); else FileOperations.Copy(Hwnd, files, target);
        Tab.Refresh();
    }

    public void Execute(string cmd) => ExecuteFor(cmd, FileList.SelectedItems);

    private void ExecuteFor(string cmd, IReadOnlyList<FileItem> sel)
    {
        var paths = sel.Where(i => !i.IsVirtual).Select(i => i.FullPath).ToList();
        string? folder = Tab.IsVirtual ? null : Tab.Path;
        switch (cmd)
        {
            case "open": foreach (var i in sel.Take(10)) OnItemActivated(i); break;
            case "openwith": if (paths.Count > 0) ShellUtil.OpenWith(paths[0], Hwnd); break;
            case "newtab": if (sel.Count > 0 && sel[0].IsDirectory && Window.GetWindow(this) is ExplorerWindow ew) ew.OpenTab(sel[0].FullPath); break;
            case "cut": if (paths.Count > 0) FileOperations.CopyToClipboard(paths, true); break;
            case "copy": if (paths.Count > 0) FileOperations.CopyToClipboard(paths, false); break;
            case "paste": if (folder != null && FileOperations.Paste(Hwnd, folder)) Tab.Refresh(); break;
            case "pasteinto": if (sel.Count == 1 && sel[0].IsDirectory && FileOperations.Paste(Hwnd, sel[0].FullPath)) Tab.Refresh(); break;
            case "delete": if (paths.Count > 0 && FileOperations.Delete(Hwnd, paths)) Tab.Refresh(); break;
            case "permanentdelete": if (paths.Count > 0 && FileOperations.Delete(Hwnd, paths, true)) Tab.Refresh(); break;
            case "rename": FileList.BeginRename(sel.Count == 1 ? sel[0] : null); break;
            case "properties":
                if (paths.Count > 0) ShellUtil.ShowProperties(paths[0], Hwnd);
                else if (folder != null) ShellUtil.ShowProperties(folder, Hwnd);
                break;
            case "selectall": FileList.SelectAll(); break;
            case "refresh": Refresh(); break;
            case "back": Tab.Back(); break;
            case "forward": Tab.Forward(); break;
            case "up": Tab.Up(); break;
            case "newfolder":
                if (folder != null && FileOperations.NewFolder(folder) is { } nf) { _pendingSelect = nf; Tab.Refresh(); Dispatcher.BeginInvoke(() => FileList.BeginRename(FileList.SelectedItem), DispatcherPriority.ApplicationIdle); }
                break;
            case "newfile":
                if (folder != null && FileOperations.NewFile(folder) is { } nfile) { _pendingSelect = nfile; Tab.Refresh(); Dispatcher.BeginInvoke(() => FileList.BeginRename(FileList.SelectedItem), DispatcherPriority.ApplicationIdle); }
                break;
            case "copypath":
                FileOperations.CopyText(paths.Count > 0 ? string.Join(Environment.NewLine, paths.Select(p => "\"" + p + "\"")) : folder ?? "");
                break;
            case "showinexplorer": if (paths.Count > 0) ShellUtil.OpenInWindowsExplorer(paths[0]); break;
            case "openterminal":
            {
                string? dir = sel.Count == 1 && sel[0].IsDirectory ? sel[0].FullPath : paths.Count > 0 ? Path.GetDirectoryName(paths[0]) : folder;
                if (dir != null) ShellUtil.OpenTerminal(dir);
                break;
            }
            case "openhub":
            {
                string? dir = sel.Count == 1 && sel[0].IsDirectory ? sel[0].FullPath : folder;
                App.ShowHub(dir != null ? $"infolder:\"{dir}\" " : null);
                break;
            }
            case "zoomin": ZoomView(1); break;
            case "zoomout": ZoomView(-1); break;
            default:
                if (cmd.StartsWith("view:") && Enum.TryParse<ViewMode>(cmd[5..], out var vm)) Tab.SetView(vm);
                else if (cmd.StartsWith("sort:")) Tab.SetSort(cmd[5..], Tab.SortDescending);
                else if (cmd == "sortdir:asc") Tab.SetSort(Tab.SortBy, false);
                else if (cmd == "sortdir:desc") Tab.SetSort(Tab.SortBy, true);
                break;
        }
    }

    private void ZoomView(int delta)
    {
        var order = new[] { ViewMode.Details, ViewMode.List, ViewMode.SmallIcons, ViewMode.MediumIcons, ViewMode.LargeIcons, ViewMode.ExtraLargeIcons };
        int i = Array.IndexOf(order, Tab.View);
        int n = Math.Clamp(i + delta, 0, order.Length - 1);
        if (n != i) Tab.SetView(order[n]);
    }

    public void PersistColumns()
    {
        if (FileList.ViewMode == ViewMode.Details && !Tab.IsSearch) Tab.SaveColumns(FileList.SnapshotColumns());
    }

    public void FocusList() => FileList.FocusList();
}
