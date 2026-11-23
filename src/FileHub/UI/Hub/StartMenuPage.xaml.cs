using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FileHub.Core.Settings;

namespace FileHub.UI.Hub;

public partial class StartMenuPage : UserControl, ISavablePage
{
    private readonly ObservableCollection<PinnedItem> _pins;
    private static readonly string[] FolderNames = { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "This PC" };

    public StartMenuPage()
    {
        InitializeComponent();
        var s = AppSettings.Current.StartMenu;
        ReplaceWin.IsChecked = s.ReplaceWinKey; ReplaceWinE.IsChecked = s.ReplaceWinE;
        foreach (ComboBoxItem ci in PositionCombo.Items) if ((string)ci.Tag == s.Position.ToString()) PositionCombo.SelectedItem = ci;
        MaxResultsBox.Text = s.MaxSearchResults.ToString();
        SearchContent.IsChecked = s.SearchContent; PowerButtons.IsChecked = s.ShowPowerButtons; UserName.IsChecked = s.ShowUserName;
        foreach (var f in FolderNames) FolderChecks.Children.Add(new CheckBox { Content = f, IsChecked = s.WindowsFolders.Contains(f), Margin = new Thickness(0, 0, 16, 4) });
        _pins = new ObservableCollection<PinnedItem>(s.Pinned);
        Grid.ItemsSource = _pins;
        LoadLayout(s.Style.ToString(), s.TileSize.ToString(), s.SearchAtBottom, s.MenuWidth, s.MenuHeight, s.ShowFolders);
    }

    private void LoadLayout(string style, string tileSize, bool searchAtBottom, int width, int height, bool showFolders)
    {
        Select(StyleCombo, style); Select(TileSizeCombo, tileSize); Select(SearchPosCombo, searchAtBottom ? "Bottom" : "Top");
        MenuWidthBox.Text = width.ToString(); MenuHeightBox.Text = height.ToString();
        ShowFoldersCheck.IsChecked = showFolders;
    }

    private static void Select(ComboBox combo, string tag) { foreach (ComboBoxItem ci in combo.Items) if ((string)ci.Tag == tag) combo.SelectedItem = ci; }
    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void ResetLayout_Click(object sender, RoutedEventArgs e) => LoadLayout("Tiles", "Medium", false, 640, 700, true);

    public void SavePage()
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        var s = AppSettings.Current.StartMenu;
        s.ReplaceWinKey = ReplaceWin.IsChecked == true; s.ReplaceWinE = ReplaceWinE.IsChecked == true;
        if (PositionCombo.SelectedItem is ComboBoxItem ci && Enum.TryParse<StartMenuPosition>((string)ci.Tag, out var p)) s.Position = p;
        if (int.TryParse(MaxResultsBox.Text, out int n)) s.MaxSearchResults = Math.Clamp(n, 3, 30);
        s.SearchContent = SearchContent.IsChecked == true; s.ShowPowerButtons = PowerButtons.IsChecked == true; s.ShowUserName = UserName.IsChecked == true;
        s.WindowsFolders = FolderChecks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
        s.Pinned = _pins.ToList();
        if (Enum.TryParse<StartMenuStyle>(TagOf(StyleCombo), out var style)) s.Style = style;
        if (Enum.TryParse<StartTileSize>(TagOf(TileSizeCombo), out var size)) s.TileSize = size;
        s.SearchAtBottom = TagOf(SearchPosCombo) == "Bottom";
        if (int.TryParse(MenuWidthBox.Text, out int w)) s.MenuWidth = w;
        if (int.TryParse(MenuHeightBox.Text, out int h)) s.MenuHeight = h;
        s.ShowFolders = ShowFoldersCheck.IsChecked == true;
        AppSettings.Current.Save();
        App.ApplySettings();
    }

    private void Preview_Click(object sender, RoutedEventArgs e) { SavePage(); App.ToggleStartMenu(); }
    private void Add_Click(object sender, RoutedEventArgs e) { var p = new PinnedItem { Label = "New shortcut" }; _pins.Add(p); Grid.SelectedItem = p; }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd|All files|*.*" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (Grid.SelectedItem is PinnedItem sel) { sel.Path = dlg.FileName; if (string.IsNullOrWhiteSpace(sel.Label) || sel.Label == "New shortcut") sel.Label = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName); }
        else _pins.Add(new PinnedItem { Label = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Path = dlg.FileName });
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (Grid.SelectedItem is PinnedItem p) _pins.Remove(p); }
    private void Up_Click(object sender, RoutedEventArgs e) { if (Grid.SelectedItem is PinnedItem p && _pins.IndexOf(p) is int i && i > 0) { _pins.Move(i, i - 1); Grid.SelectedItem = p; } }
    private void Down_Click(object sender, RoutedEventArgs e) { if (Grid.SelectedItem is PinnedItem p && _pins.IndexOf(p) is int i && i >= 0 && i < _pins.Count - 1) { _pins.Move(i, i + 1); Grid.SelectedItem = p; } }
}
