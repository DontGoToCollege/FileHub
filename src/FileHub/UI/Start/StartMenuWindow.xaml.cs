using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.Platform;
using FileHub.UI.Common;

namespace FileHub.UI.Start;

public sealed class PinTile : ObservableObject
{
    private ImageSource? _icon;
    public string Label { get; init; } = "";
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string? Glyph { get; init; }
    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && Glyph == null && (File.Exists(Path) || Directory.Exists(Path)))
                _icon = App.Images.Request(Path, Directory.Exists(Path), 32, false, 0, img => { _icon = img; OnPropertyChanged(nameof(Icon)); });
            return _icon;
        }
    }
}

public partial class StartMenuWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private CancellationTokenSource? _cts;
    private bool _ignoreDeactivate;

    public StartMenuWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { _timer.Stop(); RunSearch(); };
        Deactivated += (_, _) => { if (!_ignoreDeactivate) HideMenu(); };
        SourceInitialized += (_, _) => { var h = new WindowInteropHelper(this).Handle; Native.SetRoundedCorners(h); Native.SetDarkTitleBar(h, ThemeManager.IsDark); };
        UserNameText.Text = Environment.UserName;
        UserInitial.Text = Environment.UserName.Length > 0 ? Environment.UserName[..1].ToUpperInvariant() : "?";
        BuildPinned();
    }

    public void BuildPinned()
    {
        var s = AppSettings.Current.StartMenu;
        PinnedItems.ItemsSource = s.Pinned.Select(p => new PinTile { Label = p.Label, Path = p.Path, Arguments = p.Arguments, Glyph = GlyphFor(p.Path) }).ToList();
        var folders = new List<PinTile>();
        foreach (var name in s.WindowsFolders)
        {
            if (name == "This PC") { folders.Add(new PinTile { Label = "This PC", Path = "::ThisPC", Glyph = "" }); continue; }
            var p = KnownFolders.Resolve(name);
            if (p != null && Directory.Exists(p)) folders.Add(new PinTile { Label = name, Path = p });
        }
        FolderItems.ItemsSource = folders;
        PowerPanel.Visibility = s.ShowPowerButtons ? Visibility.Visible : Visibility.Collapsed;
        UserNameText.Visibility = s.ShowUserName ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string? GlyphFor(string path) => path switch
    {
        "::Hub" => "", "::Explorer" => "", "::ThisPC" => "", "::Start" => "",
        _ => path.StartsWith("ms-settings:") ? "" : path.StartsWith("http") ? "" : File.Exists(path) || Directory.Exists(path) ? null : "",
    };

    // ------------------------------------------------------------------ show / hide

    public void ShowMenu()
    {
        BuildPinned();
        Position();
        SearchBox.Text = "";
        Show();
        Activate();
        var h = new WindowInteropHelper(this).Handle;
        Native.SetForegroundWindow(h);
        SearchBox.Focus();
    }

    public void HideMenu()
    {
        _cts?.Cancel();
        Hide();
    }

    public void Toggle() { if (IsVisible) HideMenu(); else ShowMenu(); }

    private void Position()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        var bounds = screen.Bounds; var work = screen.WorkingArea;
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? (System.Windows.Forms.Screen.PrimaryScreen != null ? GetDpiScale() : 1.0);
        double w = Width * scale, h = Height * scale;
        double x, y;
        bool taskbarBottom = work.Bottom < bounds.Bottom, taskbarTop = work.Top > bounds.Top, taskbarLeft = work.Left > bounds.Left;
        var pos = AppSettings.Current.StartMenu.Position;
        if (taskbarTop) { y = work.Top; x = pos == StartMenuPosition.Center ? work.Left + (work.Width - w) / 2 : work.Left; }
        else if (taskbarLeft) { x = work.Left; y = work.Bottom - h; }
        else { y = work.Bottom - h; x = pos == StartMenuPosition.Center ? work.Left + (work.Width - w) / 2 : work.Left; }
        if (!taskbarBottom && !taskbarTop && !taskbarLeft) { x = pos == StartMenuPosition.Center ? work.Left + (work.Width - w) / 2 : work.Left; y = work.Bottom - h; }
        Left = x / scale; Top = y / scale;
    }

    private static double GetDpiScale()
    {
        try { using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero); return g.DpiX / 96.0; } catch { return 1.0; }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideMenu(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    // ------------------------------------------------------------------ search

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool has = SearchBox.Text.Length > 0;
        Hint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        PinnedView.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ResultsView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _timer.Stop();
        if (has) _timer.Start(); else { _cts?.Cancel(); ResultsList.ItemsSource = null; }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ResultsList.Items.Count > 0) { ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex); OpenSelected(); }
            else if (SearchBox.Text.Length > 0) More_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = Math.Min(ResultsList.Items.Count - 1, ResultsList.SelectedIndex + 1);
            (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }

    private void RunSearch()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        string text = SearchBox.Text.Trim();
        if (text.Length == 0) return;
        var s = AppSettings.Current.StartMenu;
        int take = Math.Clamp(s.MaxSearchResults, 3, 30);
        Task.Run(() =>
        {
            try
            {
                var options = new SearchOptions { MaxResults = 20000, Sort = SortKey.None };
                var result = App.Search.Search(text, options, cts.Token);
                var ranked = SearchEngine.RankForQuickSearch(result, text, take).ToList();
                if (ranked.Count < take && s.SearchContent && !text.Contains(':'))
                {
                    var contentResult = App.Search.Search("content:\"" + text.Replace("\"", "") + "\"", new SearchOptions { MaxResults = take }, cts.Token);
                    foreach (var id in contentResult.Ids) if (!ranked.Contains(id) && ranked.Count < take) ranked.Add(id);
                }
                var items = ranked.Select(id => FileItem.FromEntry(result.Index, id)).ToList();
                foreach (var it in items) it.SetIconSize(32, false);
                Dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    ResultsList.ItemsSource = items;
                    ResultsHeader.Text = items.Count == 0 ? "No matches" : $"Best matches ({result.TotalMatches:N0} total)";
                    MoreBtn.Content = $"See all {result.TotalMatches:N0} results in File Hub  ›";
                    if (items.Count > 0) ResultsList.SelectedIndex = 0;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("Start search failed: " + ex.Message); }
        }, cts.Token);
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        else if (e.Key == Key.Up && ResultsList.SelectedIndex == 0) { SearchBox.Focus(); e.Handled = true; }
        else if (e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Back or Key.Space) { SearchBox.Focus(); SearchBox.CaretIndex = SearchBox.Text.Length; }
    }

    private void Results_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not FileItem item) return;
        _ignoreDeactivate = true;
        var menu = ContextMenuBuilder.Build(this, new[] { item }, null, cmd =>
        {
            switch (cmd)
            {
                case "open": OpenItem(item); break;
                case "openfolder": case "showinexplorer": App.ShowExplorer(item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath), selectPath: item.FullPath); HideMenu(); break;
                case "openwith": ShellUtil.OpenWith(item.FullPath); HideMenu(); break;
                case "properties": ShellUtil.ShowProperties(item.FullPath); break;
                case "copypath": FileOperations.CopyText(item.FullPath); break;
                case "copy": FileOperations.CopyToClipboard(new[] { item.FullPath }, false); break;
                case "openterminal": ShellUtil.OpenTerminal(item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath) ?? item.FullPath); HideMenu(); break;
            }
        });
        var of = new MenuItem { Header = "Open file location" }; of.Click += (_, _) => { App.ShowExplorer(item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath), selectPath: item.FullPath); HideMenu(); };
        menu.Items.Insert(1, of);
        menu.Closed += (_, _) => { _ignoreDeactivate = false; Activate(); };
        menu.PlacementTarget = ResultsList; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint; menu.IsOpen = true;
    }

    private void OpenSelected() { if (ResultsList.SelectedItem is FileItem item) OpenItem(item); }

    private void OpenItem(FileItem item)
    {
        HideMenu();
        if (item.IsDirectory) App.ShowExplorer(item.FullPath); else ShellUtil.Open(item.FullPath);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        string q = SearchBox.Text;
        HideMenu();
        App.ShowHub(q);
    }

    // ------------------------------------------------------------------ tiles / footer

    private void Pinned_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement f || f.Tag is not PinTile tile) return;
        HideMenu();
        Launch(tile.Path, tile.Arguments);
    }

    public static void Launch(string path, string args)
    {
        switch (path)
        {
            case "::Hub": App.ShowHub(); return;
            case "::Explorer": App.ShowExplorer(); return;
            case "::ThisPC": App.ShowExplorer("::ThisPC"); return;
            case "::Home": App.ShowExplorer("::Home"); return;
        }
        if (Directory.Exists(path)) { App.ShowExplorer(path); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ExpandEnvironmentVariables(path), args) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Launch failed for " + path + ": " + ex.Message); }
    }

    private void User_Click(object sender, RoutedEventArgs e) { HideMenu(); if (KnownFolders.Profile is { } p) App.ShowExplorer(p); }
    private void Settings_Click(object sender, RoutedEventArgs e) { HideMenu(); App.ShowHub(null, "general"); }

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        _ignoreDeactivate = true;
        var menu = new ContextMenu();
        foreach (var (label, act) in new[] { ("Lock", "lock"), ("Sleep", "sleep"), ("Hibernate", "hibernate"), ("Sign out", "signout"), ("Restart", "restart"), ("Shut down", "shutdown") })
        {
            var mi = new MenuItem { Header = label }; var a = act;
            mi.Click += (_, _) =>
            {
                HideMenu();
                if (a is "restart" or "shutdown" or "signout")
                {
                    if (MessageBox.Show($"{label} now?", "FileHub", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                }
                ShellUtil.Shutdown(a);
            };
            menu.Items.Add(mi);
        }
        menu.Closed += (_, _) => { _ignoreDeactivate = false; if (IsVisible) Activate(); };
        menu.PlacementTarget = (FrameworkElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; menu.IsOpen = true;
    }
}
