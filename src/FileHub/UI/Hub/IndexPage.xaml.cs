using System.IO;
using System.Windows;
using System.Windows.Controls;
using FileHub.UI.Common;
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
        IndexingStatus.Subscribe(OnStatus);
        Unloaded += (_, _) => IndexingStatus.Unsubscribe(OnStatus);
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
        ContentStatusText.Text = IndexingStatus.PassLines;
        Task.Run(() =>
        {
            try { var rows = DriveRows(); Dispatcher.BeginInvoke(() => DrivesGrid.ItemsSource = rows); } catch { }
            try
            {
                var st = App.Db.Stats(); long thumbs = App.Thumbs.Count(); string thumbSize = Core.Search.SizeRange.Format(App.Thumbs.FileSize);
                var dbSize = new FileInfo(Paths.MetaDbFile) is { Exists: true } f ? Core.Search.SizeRange.Format(f.Length) : "0";
                Dispatcher.BeginInvoke(() => ThumbStats.Text = $"{thumbs:N0} thumbnails ({thumbSize}) · {st.contentRows:N0} documents · {st.propsRows:N0} metadata rows · database {dbSize}");
            }
            catch { }
        });
    }

    public sealed record DriveRow(string Drive, string Name, string Kind, string Names, string Contents, string Thumbnails);

    // volume labels are read once per page (a sleeping hard drive can take seconds to answer), off the UI thread
    private readonly System.Collections.Concurrent.ConcurrentDictionary<char, string> _labels = new();

    /// <summary>One row per indexed drive: its name, kind, and how far each pass has got on it.</summary>
    private List<DriveRow> DriveRows()
    {
        var content = App.Content.DriveProgress(); var thumbs = App.ThumbPass.DriveProgress();
        var letters = App.Index.GetRoots().Where(r => r.Length >= 2 && r[1] == ':').Select(r => char.ToUpperInvariant(r[0]))
            .Concat(content.Select(p => p.drive)).Concat(thumbs.Select(p => p.drive)).Where(char.IsLetter).Distinct().OrderBy(c => c)
            .Where(c => Environment.GetLogicalDrives().Contains(c + ":\\"));   // an unplugged drive gets its offline row below instead
        // the pass works on the first drive in its order that still has files left
        char contentAt = content.Where(p => p.left > 0).Select(p => p.drive).FirstOrDefault(d => true, '\0');
        char thumbsAt = thumbs.Where(p => p.left > 0).Select(p => p.drive).FirstOrDefault(d => true, '\0');
        string contentWord = App.Content.Paused ? "paused" : App.Content.IsRunning ? "working" : "";
        string thumbWord = App.ThumbPass.Paused ? "paused" : App.ThumbPass.WaitingForContent ? "waiting for contents" : App.ThumbPass.IsRunning ? "working" : "";
        string names = App.Index.State == Core.Indexing.IndexState.Ready ? "100%" : App.Index.State.ToString();
        return letters.Select(d =>
        {
            string root = d + ":\\";
            string label = _labels.GetOrAdd(d, _ =>
            {
                try { var di = new DriveInfo(root); return di.IsReady ? di.VolumeLabel : "(not ready)"; } catch { return ""; }
            });
            return new DriveRow(d + ":", label, Core.Indexing.ContentIndexer.KindName(root), names,
                Cell(content, d, d == contentAt ? contentWord : ""), Cell(thumbs, d, d == thumbsAt ? thumbWord : ""));
        }).Concat(App.Index.Drives.Drives.ToArray().Where(x => !x.Online).OrderBy(x => x.Letter).Select(x =>
            new DriveRow(x.Letter + ":", x.Label, x.Kind, "offline", $"kept · last seen as {x.Letter}: {x.LastSeenUtc.ToLocalTime():g}", "kept"))).ToList();
    }

    /// <summary>"37% (12,345 / 33,000) · working"; "—" before the pass has run; never rounds up to 100% while files are left.</summary>
    private static string Cell(IReadOnlyList<(char drive, int total, int left)> progress, char d, string word)
    {
        if (progress.Count == 0) return "—";
        var p = progress.FirstOrDefault(x => x.drive == d);
        if (p.total == 0) return "nothing to do";
        int done = p.total - p.left;
        return $"{done * 100L / p.total}%  ({done:N0} / {p.total:N0})" + (p.left > 0 && word.Length > 0 ? " · " + word : "");
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
    private void ClearThumbs_Click(object sender, RoutedEventArgs e) { Task.Run(() => { App.Thumbs.Clear(); App.Imaging.ClearMemory(); }).ContinueWith(_ => Dispatcher.BeginInvoke(RefreshPage)); }
    private void ClearContent_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete all indexed file contents and metadata? They will be re-indexed in the background.", "FileHub", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Task.Run(() => { App.Db.ClearContent(); App.Content.RequestFullPass(); }).ContinueWith(_ => Dispatcher.BeginInvoke(RefreshPage));
    }
}
