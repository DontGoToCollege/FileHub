using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.UI.Common;

namespace FileHub.UI.Hub;

public partial class SearchPage : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private CancellationTokenSource? _cts;
    private string _typeFilter = "";
    private SortKey _sort = SortKey.None;
    private bool _desc;
    private ViewMode _view = ViewMode.Details;

    public SearchPage()
    {
        InitializeComponent();
        var s = AppSettings.Current.Search;
        RegexToggle.IsChecked = s.RegexMode; CaseToggle.IsChecked = s.MatchCase; WordToggle.IsChecked = s.WholeWord; PathToggle.IsChecked = s.MatchPath;
        SortCombo.SelectedIndex = 0;
        _timer.Tick += (_, _) => { _timer.Stop(); RunSearch(); };
        Results.ShowLocationColumn = true;
        Results.Columns = new List<ColumnSpec> { new() { Key = "Name", Width = 320 }, new() { Key = "Location", Width = 380 }, new() { Key = "Size", Width = 90 }, new() { Key = "Modified", Width = 150 }, new() { Key = "Type", Width = 140 } };
        Results.EmptyMessage = "Type to search the whole index.";
        Results.ItemActivated += Open;
        Results.ContextMenuRequested += OnContextMenu;
        Results.SelectionChanged += OnSelection;
        Results.SortRequested += key => { SetSort(key); };
        Results.RenameCommitted += (item, name) => { if (FileOperations.Rename(Hwnd, item.FullPath, name)) RunSearch(); };
        Results.CommandRequested += Execute;
        App.Index.IndexChanged += () => Dispatcher.BeginInvoke(() => { if (SearchBox.Text.Length > 0) RunSearch(); });
        RunSearch();
    }

    private IntPtr Hwnd => Window.GetWindow(this) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;

    public void SetQuery(string q) { SearchBox.Text = q; SearchBox.CaretIndex = q.Length; }
    public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
    public bool HandleEscape()
    {
        if (SearchBox.Text.Length > 0) { SearchBox.Text = ""; FocusSearch(); return true; }
        return false;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool has = SearchBox.Text.Length > 0;
        Hint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ClearBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _timer.Stop(); _timer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _timer.Stop(); RunSearch(); Results.FocusList(); e.Handled = true; }
        else if (e.Key == Key.Down) { Results.FocusList(); e.Handled = true; }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; FocusSearch(); }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current.Search;
        s.RegexMode = RegexToggle.IsChecked == true; s.MatchCase = CaseToggle.IsChecked == true; s.WholeWord = WordToggle.IsChecked == true; s.MatchPath = PathToggle.IsChecked == true;
        RunSearch();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        foreach (var child in Chips.Children.OfType<ToggleButton>()) child.IsChecked = child == tb;
        tb.IsChecked = true;
        _typeFilter = (string)tb.Tag;
        RunSearch();
    }

    private void Sort_Changed(object sender, RoutedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem ci && Enum.TryParse<SortKey>((string)ci.Tag, out var k)) _sort = k;
        _desc = DescToggle.IsChecked == true;
        Results.SetSort(_sort.ToString(), _desc);
        RunSearch();
    }

    private void SetSort(string key)
    {
        if (!Enum.TryParse<SortKey>(key, out var k)) return;
        if (k == _sort) { _desc = !_desc; DescToggle.IsChecked = _desc; }
        _sort = k;
        for (int i = 0; i < SortCombo.Items.Count; i++) if (((ComboBoxItem)SortCombo.Items[i]).Tag is string t && t == key) { SortCombo.SelectedIndex = i; break; }
        Results.SetSort(key, _desc);
        RunSearch();
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        foreach (var t in new[] { ViewDetails, ViewMedium, ViewLarge, ViewXL }) t.IsChecked = t == tb;
        tb.IsChecked = true;
        _view = Enum.Parse<ViewMode>((string)tb.Tag);
        Results.ViewMode = _view;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        bool on = PreviewToggle.IsChecked == true;
        Preview.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PreviewCol.Width = on ? new GridLength(320) : new GridLength(0);
        if (on) Preview.Show(Results.SelectedItem);
    }

    private void OnSelection()
    {
        var sel = Results.SelectedItems;
        SelText.Text = sel.Count == 0 ? "" : sel.Count == 1 ? sel[0].FullPath : $"{sel.Count} selected";
        if (Preview.Visibility == Visibility.Visible) Preview.Show(sel.Count == 1 ? sel[0] : null);
    }

    private void RunSearch()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        string text = SearchBox.Text;
        var s = AppSettings.Current.Search;
        var options = new SearchOptions
        {
            MaxResults = s.MaxResults, RegexMode = s.RegexMode, MatchCase = s.MatchCase, WholeWord = s.WholeWord, MatchPath = s.MatchPath,
            TypeFilter = _typeFilter.Length > 0 ? _typeFilter : null, Sort = _sort, SortDescending = _desc,
        };
        Busy.Visibility = Visibility.Visible;
        Task.Run(() =>
        {
            try
            {
                var result = App.Search.Search(text, options, cts.Token);
                var items = new List<FileItem>(result.Ids.Length);
                foreach (var id in result.Ids) items.Add(FileItem.FromEntry(result.Index, id));
                Dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    Results.Items = items;
                    Busy.Visibility = Visibility.Collapsed;
                    string status = result.Truncated
                        ? $"{result.TotalMatches:N0} matches (showing first {result.Ids.Length:N0}) · {result.Elapsed.TotalMilliseconds:F0} ms · {result.Index.LiveCount:N0} items indexed"
                        : $"{result.TotalMatches:N0} matches · {result.Elapsed.TotalMilliseconds:F0} ms · {result.Index.LiveCount:N0} items indexed";
                    if (result.Error != null) status += "  ·  ⚠ " + result.Error;
                    StatusText.Text = status;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested) { Busy.Visibility = Visibility.Collapsed; StatusText.Text = "Search failed: " + ex.Message; } });
            }
        }, cts.Token);
    }

    private void Open(FileItem item)
    {
        if (item.IsDirectory) App.ShowExplorer(item.FullPath);
        else ShellUtil.Open(item.FullPath, Hwnd);
    }

    private void OnContextMenu(IReadOnlyList<FileItem> items, bool background)
    {
        if (background) return;
        var menu = ContextMenuBuilder.Build(this, items, null, cmd => ExecuteFor(cmd, items));
        var first = new MenuItem { Header = "Open containing folder", InputGestureText = "Ctrl+Enter" };
        first.Click += (_, _) => ExecuteFor("openfolder", items);
        menu.Items.Insert(1, first);
        menu.PlacementTarget = Results; menu.Placement = PlacementMode.MousePoint; menu.IsOpen = true;
    }

    private void Execute(string cmd) => ExecuteFor(cmd, Results.SelectedItems);

    private void ExecuteFor(string cmd, IReadOnlyList<FileItem> sel)
    {
        var paths = sel.Select(i => i.FullPath).ToList();
        switch (cmd)
        {
            case "open": foreach (var i in sel.Take(10)) Open(i); break;
            case "openfolder": if (sel.Count > 0) App.ShowExplorer(sel[0].IsDirectory ? sel[0].FullPath : Path.GetDirectoryName(sel[0].FullPath), selectPath: sel[0].FullPath); break;
            case "openwith": if (paths.Count > 0) ShellUtil.OpenWith(paths[0], Hwnd); break;
            case "newtab": if (sel.Count > 0) App.ShowExplorer(sel[0].FullPath); break;
            case "cut": if (paths.Count > 0) FileOperations.CopyToClipboard(paths, true); break;
            case "copy": if (paths.Count > 0) FileOperations.CopyToClipboard(paths, false); break;
            case "pasteinto": if (sel.Count == 1 && sel[0].IsDirectory) FileOperations.Paste(Hwnd, sel[0].FullPath); break;
            case "delete": if (paths.Count > 0 && FileOperations.Delete(Hwnd, paths)) RunSearch(); break;
            case "permanentdelete": if (paths.Count > 0 && FileOperations.Delete(Hwnd, paths, true)) RunSearch(); break;
            case "rename": Results.BeginRename(sel.Count == 1 ? sel[0] : null); break;
            case "properties": if (paths.Count > 0) ShellUtil.ShowProperties(paths[0], Hwnd); break;
            case "selectall": Results.SelectAll(); break;
            case "refresh": RunSearch(); break;
            case "copypath": if (paths.Count > 0) FileOperations.CopyText(string.Join(Environment.NewLine, paths.Select(p => "\"" + p + "\""))); break;
            case "showinexplorer": if (paths.Count > 0) ShellUtil.OpenInWindowsExplorer(paths[0]); break;
            case "openterminal": if (paths.Count > 0) ShellUtil.OpenTerminal(sel[0].IsDirectory ? paths[0] : Path.GetDirectoryName(paths[0]) ?? paths[0]); break;
            case "openhub": if (sel.Count > 0 && sel[0].IsDirectory) SetQuery($"infolder:\"{sel[0].FullPath}\" "); break;
            case "zoomin": ZoomView(1); break;
            case "zoomout": ZoomView(-1); break;
            case "up": case "back": case "forward": break;
        }
    }

    private void ZoomView(int delta)
    {
        var order = new[] { ViewDetails, ViewMedium, ViewLarge, ViewXL };
        int i = Array.FindIndex(order, t => t.IsChecked == true);
        int n = Math.Clamp(i + delta, 0, order.Length - 1);
        if (n != i) View_Click(order[n], new RoutedEventArgs());
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && Results.SelectedItem != null) { Execute("openfolder"); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
