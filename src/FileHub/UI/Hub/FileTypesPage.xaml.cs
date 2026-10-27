using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.UI.Common;

namespace FileHub.UI.Hub;

public sealed class FileTypeRow : ObservableObject
{
    private string _openWith = "";
    public string Extension { get; init; } = "";
    public string TypeName { get; init; } = "";
    public long Count { get; init; }
    public long Size { get; init; }
    public string SizeText => SizeRange.Format(Size);
    public string Group { get; init; } = "";
    public string OpenWith { get => _openWith; set => Set(ref _openWith, value); }
}

public partial class FileTypesPage : UserControl, ISavablePage, IRefreshablePage
{
    private List<FileTypeRow> _rows = new();
    private bool _loaded;

    public FileTypesPage()
    {
        InitializeComponent();
    }

    public void RefreshPage() { if (!_loaded) Load(); }

    private void Load()
    {
        _loaded = true;
        CountText.Text = "Counting…";
        var assoc = AppSettings.Current.Associations;
        Task.Run(() =>
        {
            var index = App.Index.Index;
            var entries = index.Entries; int count = index.Count;
            var counts = new Dictionary<string, (long n, long size)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                ref var e = ref entries[i];
                if (e.Name is null || e.IsDirectory) continue;
                string ext = FileTypes.ExtensionOf(e.Name).ToLowerInvariant();
                if (ext.Length == 0 || ext.Length > 12) continue;
                counts.TryGetValue(ext, out var c);
                counts[ext] = (c.n + 1, c.size + e.Size);
            }
            var rows = counts.Select(kv => new FileTypeRow
            {
                Extension = kv.Key, Count = kv.Value.n, Size = kv.Value.size,
                TypeName = ShellImaging.GetTypeName("x." + kv.Key, false),
                Group = FileTypes.Groups.FirstOrDefault(g => g.Value.Contains(kv.Key)).Key ?? "",
                OpenWith = assoc.TryGetValue(kv.Key, out var p) ? p : "",
            }).OrderByDescending(r => r.Count).ToList();
            Dispatcher.BeginInvoke(() => { _rows = rows; ApplyFilter(); });
        });
    }

    private void ApplyFilter()
    {
        string f = FilterBox.Text.Trim().TrimStart('.');
        var view = f.Length == 0 ? _rows : _rows.Where(r => r.Extension.Contains(f, StringComparison.OrdinalIgnoreCase) || r.TypeName.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Group.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Grid.ItemsSource = new ObservableCollection<FileTypeRow>(view);
        CountText.Text = $"{_rows.Count:N0} extensions · {_rows.Sum(r => r.Count):N0} files";
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (_loaded) ApplyFilter(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) { SavePage(); Load(); }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not FileTypeRow row) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs|*.exe|All files|*.*", Title = "Open ." + row.Extension + " files with" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) row.OpenWith = dlg.FileName;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { if (Grid.SelectedItem is FileTypeRow row) row.OpenWith = ""; }

    private void WinDefaults_Click(object sender, RoutedEventArgs e) => ShellUtil.Execute("ms-settings:defaultapps", "open", null);

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is FileTypeRow row && Window.GetWindow(this) is HubWindow hub) hub.Navigate("search", "ext:" + row.Extension + " ");
    }

    public void SavePage()
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        var assoc = AppSettings.Current.Associations;
        foreach (var r in _rows)
        {
            if (string.IsNullOrWhiteSpace(r.OpenWith)) assoc.Remove(r.Extension);
            else assoc[r.Extension] = r.OpenWith.Trim();
        }
        AppSettings.Current.Save();
    }
}
