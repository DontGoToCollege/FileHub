using System.Windows;
using System.Windows.Controls;
using FileHub.Core.Settings;

namespace FileHub.UI.Hub;

public partial class FolderDefaultsPage : UserControl, ISavablePage, IRefreshablePage
{
    public FolderDefaultsPage()
    {
        InitializeComponent();
        var s = AppSettings.Current.Explorer;
        Select(ViewCombo, s.DefaultView.ToString()); Select(SortCombo, s.DefaultSortBy);
        SortDesc.IsChecked = s.DefaultSortDescending;
        var cols = s.DefaultColumns.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ColModified.IsChecked = cols.Contains("Modified"); ColType.IsChecked = cols.Contains("Type"); ColSize.IsChecked = cols.Contains("Size");
        ColCreated.IsChecked = cols.Contains("Created"); ColExt.IsChecked = cols.Contains("Extension"); ColAttr.IsChecked = cols.Contains("Attributes");
        StartBox.Text = s.StartLocation; FoldersFirst.IsChecked = s.FoldersFirst; ShowHidden.IsChecked = s.ShowHidden; ShowSystem.IsChecked = s.ShowSystem; ShowExt.IsChecked = s.ShowExtensions;
        TerminalBox.Text = s.TerminalCommand; QuickBox.Text = string.Join(Environment.NewLine, s.QuickAccess);
    }

    private static void Select(ComboBox cb, string tag)
    {
        foreach (ComboBoxItem ci in cb.Items) if (string.Equals((string)ci.Tag, tag, StringComparison.OrdinalIgnoreCase)) { cb.SelectedItem = ci; return; }
        cb.SelectedIndex = 0;
    }

    public void RefreshPage() => Task.Run(() => App.Db.ListFolderViews()).ContinueWith(t => Dispatcher.BeginInvoke(() => SavedList.ItemsSource = t.Result));

    public void SavePage()
    {
        var s = AppSettings.Current.Explorer;
        if (ViewCombo.SelectedItem is ComboBoxItem v && Enum.TryParse<ViewMode>((string)v.Tag, out var vm)) s.DefaultView = vm;
        if (SortCombo.SelectedItem is ComboBoxItem so) s.DefaultSortBy = (string)so.Tag;
        s.DefaultSortDescending = SortDesc.IsChecked == true;
        var cols = new List<ColumnSpec> { new() { Key = "Name", Width = 340 } };
        void Add(CheckBox cb, string key, double w) { if (cb.IsChecked == true) cols.Add(new ColumnSpec { Key = key, Width = w }); }
        Add(ColModified, "Modified", 150); Add(ColType, "Type", 150); Add(ColSize, "Size", 90); Add(ColCreated, "Created", 150); Add(ColExt, "Extension", 80); Add(ColAttr, "Attributes", 80);
        s.DefaultColumns = cols;
        s.StartLocation = string.IsNullOrWhiteSpace(StartBox.Text) ? "::Home" : StartBox.Text.Trim();
        s.FoldersFirst = FoldersFirst.IsChecked == true; s.ShowHidden = ShowHidden.IsChecked == true; s.ShowSystem = ShowSystem.IsChecked == true; s.ShowExtensions = ShowExt.IsChecked == true;
        s.TerminalCommand = TerminalBox.Text.Trim();
        s.QuickAccess = QuickBox.Text.Split('\n').Select(x => x.Trim().TrimEnd('\r')).Where(x => x.Length > 0).ToList();
        AppSettings.Current.Save();
    }

    private void BrowseStart_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Start location" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) StartBox.Text = dlg.FolderName;
    }

    private void ResetOne_Click(object sender, RoutedEventArgs e)
    {
        if (SavedList.SelectedItem is string p) { App.Db.SetFolderView(p, null); RefreshPage(); }
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Forget all per-folder views?", "FileHub", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { App.Db.ClearFolderViews(); RefreshPage(); }
    }

    private void RefreshList_Click(object sender, RoutedEventArgs e) => RefreshPage();
}
