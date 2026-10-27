using System.IO;
using System.Windows;
using System.Windows.Controls;
using FileHub.Core.Settings;
using FileHub.Platform;

namespace FileHub.UI.Hub;

public partial class IndexPage : UserControl, ISavablePage, IRefreshablePage
{
    public IndexPage()
    {
        InitializeComponent();
        var s = AppSettings.Current.Index;
        AllFixed.IsChecked = s.IndexAllFixedDrives; Removable.IsChecked = s.IndexRemovableDrives;
        RootsBox.Text = string.Join(Environment.NewLine, s.Roots);
        ExcludesBox.Text = string.Join(Environment.NewLine, s.Excludes);
        RebuildStartup.IsChecked = s.RebuildOnStartup; IntervalBox.Text = s.RebuildIntervalHours.ToString();
        ContentOn.IsChecked = s.ContentIndexing; MaxMbBox.Text = s.ContentMaxMB.ToString();
        ContentExtBox.Text = string.Join(" ", s.ContentExtensions);
        MetaOn.IsChecked = s.MetadataIndexing; MetaExtBox.Text = string.Join(" ", s.MetadataExtensions);
        ThumbsOn.IsChecked = s.ThumbnailCaching;
        App.Index.StatusChanged += OnStatus; App.Content.StatusChanged += OnStatus;
        Unloaded += (_, _) => { App.Index.StatusChanged -= OnStatus; App.Content.StatusChanged -= OnStatus; };
        RefreshPage();
    }

    private void OnStatus() => Dispatcher.BeginInvoke(RefreshPage);

    public void RefreshPage()
    {
        var idx = App.Index;
        StatusText.Text = $"{idx.State}: {idx.StatusText}";
        try
        {
            var fi = new FileInfo(Paths.IndexFile);
            string snap = fi.Exists ? $"snapshot {Core.Search.SizeRange.Format(fi.Length)}, saved {fi.LastWriteTime:g}" : "no snapshot yet";
            StatsText.Text = $"{idx.Index.LiveCount:N0} files and folders in memory · built {idx.Index.BuiltUtc.ToLocalTime():g} · {snap} · roots: {string.Join(", ", idx.GetRoots())}";
        }
        catch { }
        ContentStatusText.Text = "Content indexer: " + App.Content.StatusText;
        Task.Run(() =>
        {
            try
            {
                var st = App.Db.Stats(); long thumbs = App.Db.ThumbCount();
                var dbSize = new FileInfo(Paths.MetaDbFile) is { Exists: true } f ? Core.Search.SizeRange.Format(f.Length) : "0";
                Dispatcher.BeginInvoke(() => ThumbStats.Text = $"{thumbs:N0} thumbnails · {st.contentRows:N0} documents · {st.propsRows:N0} metadata rows · database {dbSize}");
            }
            catch { }
        });
    }

    public void SavePage()
    {
        var s = AppSettings.Current.Index;
        s.IndexAllFixedDrives = AllFixed.IsChecked == true; s.IndexRemovableDrives = Removable.IsChecked == true;
        s.Roots = Lines(RootsBox.Text); s.Excludes = Lines(ExcludesBox.Text);
        s.RebuildOnStartup = RebuildStartup.IsChecked == true;
        if (int.TryParse(IntervalBox.Text, out int h)) s.RebuildIntervalHours = Math.Max(1, h);
        s.ContentIndexing = ContentOn.IsChecked == true;
        if (int.TryParse(MaxMbBox.Text, out int mb)) s.ContentMaxMB = Math.Max(1, mb);
        s.ContentExtensions = Words(ContentExtBox.Text); s.MetadataIndexing = MetaOn.IsChecked == true; s.MetadataExtensions = Words(MetaExtBox.Text);
        s.ThumbnailCaching = ThumbsOn.IsChecked == true;
        App.Imaging.DiskCacheEnabled = s.ThumbnailCaching;
        AppSettings.Current.Save();
    }

    private static List<string> Lines(string t) => t.Split('\n').Select(x => x.Trim().TrimEnd('\r')).Where(x => x.Length > 0).ToList();
    private static List<string> Words(string t) => t.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToList();

    private void Rebuild_Click(object sender, RoutedEventArgs e) { SavePage(); _ = App.Index.RebuildAsync(); }
    private void Save_Click(object sender, RoutedEventArgs e) => _ = App.Index.SaveAsync().ContinueWith(_ => Dispatcher.BeginInvoke(RefreshPage));
    private void Rescan_Click(object sender, RoutedEventArgs e) { SavePage(); App.Content.RequestFullPass(); }
    private void OpenData_Click(object sender, RoutedEventArgs e) => Core.Shell.ShellUtil.Open(Paths.DataDir);
    private void ClearThumbs_Click(object sender, RoutedEventArgs e) { Task.Run(() => { App.Db.ClearThumbs(); App.Imaging.ClearMemory(); }).ContinueWith(_ => Dispatcher.BeginInvoke(RefreshPage)); }
    private void ClearContent_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete all indexed file contents and metadata? They will be re-indexed in the background.", "FileHub", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Task.Run(() => { App.Db.ClearContent(); App.Content.RequestFullPass(); }).ContinueWith(_ => Dispatcher.BeginInvoke(RefreshPage));
    }
}
