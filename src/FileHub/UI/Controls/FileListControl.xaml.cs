using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.UI.Common;

namespace FileHub.UI.Controls;

/// <summary>
/// The shared file list used by the Explorer panes and the Hub: details (columns) or tile views,
/// virtualization, inline rename, type-ahead, drag &amp; drop, keyboard commands and context menu hooks.
/// </summary>
public partial class FileListControl : UserControl
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(FileListControl), new PropertyMetadata(110.0));
    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(FileListControl), new PropertyMetadata(110.0));
    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }

    private IList<FileItem>? _items;
    private ViewMode _viewMode = ViewMode.Details;
    private List<ColumnSpec> _columns = new();
    private bool _showLocation;
    private string _sortBy = "Name";
    private bool _sortDesc;
    private FileItem? _renaming;
    private Point _dragStart;
    private FileItem? _pressedItem;
    private bool _deferredSelect;
    private readonly System.Text.StringBuilder _typeAhead = new();
    private DateTime _typeAheadAt;
    private int _iconPx = 20;

    public event Action<FileItem>? ItemActivated;
    public event Action<IReadOnlyList<FileItem>, bool>? ContextMenuRequested;
    public event Action? SelectionChanged;
    public event Action<string>? SortRequested;
    public event Action<FileItem, string>? RenameCommitted;
    public event Action<string[], string, bool>? FilesDropped;   // paths, target folder, move
    public event Action<string>? CommandRequested;
    public event Action<List<ColumnSpec>>? ColumnsChanged;

    public string? CurrentFolder { get; set; }
    public ListView Inner => List;

    public FileListControl()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyView();
    }

    // ---------------------------------------------------------------- items

    public IList<FileItem>? Items
    {
        get => _items;
        set
        {
            _items = value;
            ApplyIconSize();
            List.ItemsSource = value;
            EmptyText.Visibility = value == null || value.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (List.Items.Count > 0) List.ScrollIntoView(List.Items[0]);
        }
    }

    /// <summary>Swaps in a newer listing of the same folder without losing the selection or scroll position.</summary>
    public void ReplaceItems(IList<FileItem> value)
    {
        var selected = new HashSet<string>(List.SelectedItems.Cast<FileItem>().Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
        var sv = FindScrollViewer(List);
        double offset = sv?.VerticalOffset ?? 0;
        _items = value;
        ApplyIconSize();
        List.ItemsSource = value;
        EmptyText.Visibility = value.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected.Count > 0)
            foreach (var it in value) if (selected.Contains(it.FullPath)) List.SelectedItems.Add(it);
        if (sv != null && offset > 0) Dispatcher.BeginInvoke(() => sv.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScrollViewer(c) is { } found) return found;
        }
        return null;
    }

    public string EmptyMessage { set => EmptyText.Text = value; }

    public IReadOnlyList<FileItem> SelectedItems => List.SelectedItems.Cast<FileItem>().ToList();
    public FileItem? SelectedItem => List.SelectedItem as FileItem;
    public int Count => List.Items.Count;

    public void SelectAll() => List.SelectAll();
    public void ClearSelection() => List.UnselectAll();
    public void FocusList()
    {
        List.Focus();
        if (List.SelectedItem == null && List.Items.Count > 0) { List.SelectedIndex = 0; }
        if (List.SelectedItem != null && List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListViewItem c) c.Focus();
    }

    public void SelectItem(FileItem item, bool scroll = true)
    {
        List.SelectedItem = item;
        if (scroll) List.ScrollIntoView(item);
    }

    public void SelectPath(string path)
    {
        var item = _items?.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item != null) { SelectItem(item); FocusItem(item); }
    }

    private void FocusItem(FileItem item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem c) c.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- view / columns

    public ViewMode ViewMode
    {
        get => _viewMode;
        set { _viewMode = value; ApplyView(); }
    }

    public List<ColumnSpec> Columns
    {
        get => _columns;
        set { _columns = value; BuildColumns(); }
    }

    public bool ShowLocationColumn
    {
        get => _showLocation;
        set { _showLocation = value; BuildColumns(); }
    }

    public void SetSort(string sortBy, bool descending)
    {
        _sortBy = sortBy; _sortDesc = descending;
        UpdateHeaderArrows();
    }

    private void ApplyView()
    {
        bool details = _viewMode is ViewMode.Details or ViewMode.List;
        (_iconPx, TileWidth, TileHeight) = _viewMode switch
        {
            ViewMode.SmallIcons => (32, 96.0, 84.0),
            ViewMode.MediumIcons => (64, 116.0, 116.0),
            ViewMode.LargeIcons => (128, 170.0, 182.0),
            ViewMode.ExtraLargeIcons => (256, 300.0, 316.0),
            _ => (20, 110.0, 110.0),
        };
        if (details)
        {
            List.View = Grid;
            List.ItemTemplate = null;
            List.ItemContainerStyle = (Style)FindResource("DetailsItemStyle");
            List.ItemsPanel = (ItemsPanelTemplate)FindResource("DetailsPanel");
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto);
            BuildColumns();
        }
        else
        {
            List.View = null;
            List.ItemContainerStyle = (Style)FindResource("TileItemStyle");
            List.ItemTemplate = (DataTemplate)FindResource("TileTemplate");
            List.ItemsPanel = (ItemsPanelTemplate)FindResource("TilePanel");
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        }
        ApplyIconSize();
    }

    private void ApplyIconSize()
    {
        if (_items == null) return;
        bool thumb = _viewMode is ViewMode.MediumIcons or ViewMode.LargeIcons or ViewMode.ExtraLargeIcons;
        foreach (var it in _items) it.SetIconSize(_iconPx, thumb);
    }

    public static readonly (string Key, string Header, double Width)[] AllColumns =
    {
        ("Name", "Name", 340), ("Modified", "Date modified", 150), ("Type", "Type", 150), ("Size", "Size", 90),
        ("Created", "Date created", 150), ("Extension", "Extension", 80), ("Location", "Location", 320), ("Attributes", "Attributes", 80),
    };

    private void BuildColumns()
    {
        if (List.View != Grid) return;
        Grid.Columns.Clear();
        var specs = _columns.Count > 0 ? _columns : AllColumns.Take(4).Select(c => new ColumnSpec { Key = c.Key, Width = c.Width }).ToList();
        if (_viewMode == ViewMode.List) specs = new List<ColumnSpec> { new() { Key = "Name", Width = 600 } };
        var list = specs.ToList();
        if (_showLocation && !list.Any(c => c.Key == "Location")) list.Insert(1, new ColumnSpec { Key = "Location", Width = 320 });
        foreach (var spec in list)
        {
            var def = AllColumns.FirstOrDefault(c => c.Key == spec.Key);
            if (def.Key == null) continue;
            var header = new GridViewColumnHeader { Content = def.Header, Tag = spec.Key };
            header.Click += Header_Click;
            var col = new GridViewColumn { Header = header, Width = spec.Width > 20 ? spec.Width : def.Width };
            switch (spec.Key)
            {
                case "Name": col.CellTemplate = (DataTemplate)FindResource("NameCell"); break;
                case "Modified": col.DisplayMemberBinding = new System.Windows.Data.Binding("ModifiedText"); break;
                case "Created": col.DisplayMemberBinding = new System.Windows.Data.Binding("CreatedText"); break;
                case "Type": col.DisplayMemberBinding = new System.Windows.Data.Binding("TypeName"); break;
                case "Size":
                    col.DisplayMemberBinding = new System.Windows.Data.Binding("SizeText");
                    break;
                case "Extension": col.DisplayMemberBinding = new System.Windows.Data.Binding("Extension"); break;
                case "Location": col.DisplayMemberBinding = new System.Windows.Data.Binding("Location"); break;
                case "Attributes": col.DisplayMemberBinding = new System.Windows.Data.Binding("AttributesText"); break;
            }
            Grid.Columns.Add(col);
        }
        UpdateHeaderArrows();
    }

    private void UpdateHeaderArrows()
    {
        foreach (var col in Grid.Columns)
        {
            if (col.Header is not GridViewColumnHeader h) continue;
            string key = (string)h.Tag;
            string text = AllColumns.First(c => c.Key == key).Header;
            h.Content = key.Equals(_sortBy, StringComparison.OrdinalIgnoreCase) ? text + (_sortDesc ? "  ˅" : "  ˄") : text;
        }
    }

    public List<ColumnSpec> SnapshotColumns()
        => Grid.Columns.Where(c => c.Header is GridViewColumnHeader).Select(c => new ColumnSpec { Key = (string)((GridViewColumnHeader)c.Header).Tag, Width = c.ActualWidth }).Where(c => c.Key != "Location" || _showLocation).ToList();

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is GridViewColumnHeader h && h.Tag is string key) SortRequested?.Invoke(key);
    }

    // ---------------------------------------------------------------- mouse

    private FileItem? ItemAt(Point p)
    {
        var hit = List.InputHitTest(p) as DependencyObject;
        while (hit != null && hit is not ListViewItem && hit != List) hit = VisualTreeHelper.GetParent(hit);
        return (hit as ListViewItem)?.DataContext as FileItem;
    }

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RenamePopup.IsOpen) CommitRename();
        _dragStart = e.GetPosition(List);
        _pressedItem = ItemAt(_dragStart);
        _deferredSelect = false;
        if (_pressedItem == null)
        {
            // click on empty space: clear selection (unless on header / scrollbar)
            var hit = List.InputHitTest(_dragStart) as DependencyObject;
            bool onHeader = false, onScroll = false;
            while (hit != null && hit != List) { if (hit is GridViewColumnHeader) onHeader = true; if (hit is ScrollBar) onScroll = true; hit = VisualTreeHelper.GetParent(hit); }
            if (!onHeader && !onScroll && Keyboard.Modifiers == ModifierKeys.None) List.UnselectAll();
            return;
        }
        // keep multi-selection intact when pressing on an already-selected item (Explorer behavior)
        if (List.SelectedItems.Contains(_pressedItem) && Keyboard.Modifiers == ModifierKeys.None && List.SelectedItems.Count > 1)
        {
            _deferredSelect = true;
            e.Handled = true;
            if (List.ItemContainerGenerator.ContainerFromItem(_pressedItem) is ListViewItem c) c.Focus();
        }
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelect && _pressedItem != null)
        {
            var item = ItemAt(e.GetPosition(List));
            if (item == _pressedItem) { List.SelectedItem = _pressedItem; }
        }
        _deferredSelect = false;
        _pressedItem = null;
    }

    private void List_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedItem == null) return;
        var pos = e.GetPosition(List);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!List.SelectedItems.Contains(_pressedItem)) List.SelectedItem = _pressedItem;
        var paths = SelectedItems.Where(i => !i.IsVirtual).Select(i => i.FullPath).ToArray();
        _pressedItem = null; _deferredSelect = false;
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        try { DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); } catch { }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var item = ItemAt(e.GetPosition(List));
        if (item != null) ItemActivated?.Invoke(item);
    }

    private void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.GetPosition(List));
        if (item != null && !List.SelectedItems.Contains(item)) List.SelectedItem = item;
        if (item == null) List.UnselectAll();
        ContextMenuRequested?.Invoke(SelectedItems, item == null);
        e.Handled = true;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => SelectionChanged?.Invoke();

    // ---------------------------------------------------------------- keyboard

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (RenamePopup.IsOpen) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;   // with Alt held WPF reports Key.System
        string? cmd = key switch
        {
            Key.Enter when alt => "properties",
            Key.Enter => "open",
            Key.Delete when shift => "permanentdelete",
            Key.Delete => "delete",
            Key.F2 => "rename",
            Key.F5 => "refresh",
            Key.Back => "up",
            Key.C when ctrl => "copy",
            Key.X when ctrl => "cut",
            Key.V when ctrl => "paste",
            Key.A when ctrl => "selectall",
            Key.N when ctrl && shift => "newfolder",
            Key.Up when alt => "up",
            Key.Left when alt => "back",
            Key.Right when alt => "forward",
            _ => null,
        };
        if (cmd == null) return;
        e.Handled = true;
        if (cmd == "open") { if (SelectedItem != null) ItemActivated?.Invoke(SelectedItem); return; }
        if (cmd == "rename") { BeginRename(SelectedItem); return; }
        if (cmd == "selectall") { SelectAll(); return; }
        CommandRequested?.Invoke(cmd);
    }

    private void List_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (RenamePopup.IsOpen || _items == null || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if ((DateTime.UtcNow - _typeAheadAt).TotalMilliseconds > 900) _typeAhead.Clear();
        _typeAheadAt = DateTime.UtcNow;
        _typeAhead.Append(e.Text);
        string prefix = _typeAhead.ToString();
        int start = _typeAhead.Length == 1 ? List.SelectedIndex + 1 : 0;
        for (int n = 0; n < _items.Count; n++)
        {
            int i = (start + n) % _items.Count;
            if (_items[i].DisplayName.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                List.SelectedItem = _items[i]; List.ScrollIntoView(_items[i]); FocusItem(_items[i]);
                break;
            }
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- rename

    public void BeginRename(FileItem? item)
    {
        item ??= SelectedItem;
        if (item == null || item.IsVirtual) return;
        List.ScrollIntoView(item);
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container) return;
        _renaming = item;
        RenamePopup.PlacementTarget = container;
        RenamePopup.HorizontalOffset = _viewMode is ViewMode.Details or ViewMode.List ? 36 : 4;
        RenamePopup.VerticalOffset = _viewMode is ViewMode.Details or ViewMode.List ? 1 : container.ActualHeight - 44;
        RenameBox.MinWidth = _viewMode is ViewMode.Details or ViewMode.List ? Math.Max(160, (Grid.Columns.FirstOrDefault()?.ActualWidth ?? 300) - 40) : Math.Max(100, TileWidth - 8);
        RenameBox.Text = item.Name;
        RenamePopup.IsOpen = true;
        RenameBox.Focus();
        int dot = item.IsDirectory ? -1 : item.Name.LastIndexOf('.');
        RenameBox.Select(0, dot > 0 ? dot : item.Name.Length);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; CommitRename(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CancelRename(); }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (RenamePopup.IsOpen) CommitRename(); }

    private void CommitRename()
    {
        var item = _renaming; _renaming = null;
        RenamePopup.IsOpen = false;
        string newName = RenameBox.Text.Trim();
        if (item != null && newName.Length > 0 && !string.Equals(newName, item.Name, StringComparison.Ordinal)) RenameCommitted?.Invoke(item, newName);
        FocusList();
    }

    private void CancelRename() { _renaming = null; RenamePopup.IsOpen = false; FocusList(); }

    // ---------------------------------------------------------------- drop

    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || CurrentFolder == null || CurrentFolder.StartsWith("::"))
        {
            e.Effects = DragDropEffects.None; e.Handled = true; return;
        }
        var target = DropTarget(e);
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        bool sameVolume = files.Length > 0 && FileOperations.SameVolume(files[0], target);
        bool ctrl = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        e.Effects = ctrl ? DragDropEffects.Copy : sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
        // dropping into the folder it came from is a no-op
        if (files.Any(f => string.Equals(Path.GetDirectoryName(f), target, StringComparison.OrdinalIgnoreCase)) && !ctrl) e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private string DropTarget(DragEventArgs e)
    {
        var item = ItemAt(e.GetPosition(List));
        return item != null && item.IsDirectory && !item.IsVirtual ? item.FullPath : CurrentFolder!;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || CurrentFolder == null) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        string target = DropTarget(e);
        bool move = e.Effects.HasFlag(DragDropEffects.Move) && !e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        if (e.Effects == DragDropEffects.None) return;
        FilesDropped?.Invoke(files, target, move);
        e.Handled = true;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // Ctrl+wheel cycles view sizes like Explorer
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            CommandRequested?.Invoke(e.Delta > 0 ? "zoomin" : "zoomout");
            e.Handled = true;
        }
        base.OnPreviewMouseWheel(e);
    }
}
