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
    }

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
