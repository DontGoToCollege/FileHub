using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FileHub.Core.Settings;
using FileHub.UI.Common;

namespace FileHub.UI.Hub;

public partial class HubWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private string _current = "";

    public HubWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyToWindow(this);
        App.Index.StatusChanged += OnStatus;
        App.Content.StatusChanged += OnStatus;
        OnStatus();
        App.IndexControlChanged += SyncIndexToggles;
        SyncIndexToggles();
        Closing += (_, _) => { SaveCurrent(); AppSettings.Current.Save(); App.ApplySettings(); };
        Closed += (_, _) => { App.Index.StatusChanged -= OnStatus; App.Content.StatusChanged -= OnStatus; App.IndexControlChanged -= SyncIndexToggles; };
        Navigate("search");
    }

    private void OnStatus() => Dispatcher.BeginInvoke(() =>
    {
        IndexStatus.Text = "Index: " + App.Index.StatusText;
        ContentStatus.Text = App.Content.StatusText;
    });

    public void Navigate(string page, string? query = null)
    {
        SaveCurrent();
        if (!_pages.TryGetValue(page, out var ctl))
        {
            ctl = page switch
            {
                "index" => new IndexPage(),
                "contextmenu" => new ContextMenuPage(),
                "filetypes" => new FileTypesPage(),
                "folders" => new FolderDefaultsPage(),
                "startmenu" => new StartMenuPage(),
                "general" => new GeneralPage(),
                "help" => new HelpPage(),
                _ => new SearchPage(),
            };
            _pages[page] = ctl;
        }
        _current = page;
        Host.Content = ctl;
        foreach (var tb in FindToggles(this)) tb.IsChecked = (string?)tb.Tag == page;
        if (ctl is SearchPage sp) { if (query != null) sp.SetQuery(query); sp.FocusSearch(); }
        if (ctl is IRefreshablePage rp) rp.RefreshPage();
    }

    private void SaveCurrent()
    {
        if (_pages.TryGetValue(_current, out var ctl) && ctl is ISavablePage sp) sp.SavePage();
    }

    private static IEnumerable<ToggleButton> FindToggles(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is ToggleButton t && t.Tag is string) yield return t;
            foreach (var x in FindToggles(c)) yield return x;
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton t && t.Tag is string page) Navigate(page);
    }

    private void OpenExplorer_Click(object sender, RoutedEventArgs e) => App.ShowExplorer();

    private void SyncIndexToggles()
    {
        PauseToggle.IsChecked = App.IndexingPaused;
        RealTimeToggle.IsChecked = App.RealTimeIndexing;
    }

    private void PauseToggle_Click(object sender, RoutedEventArgs e) => App.SetIndexingPaused(PauseToggle.IsChecked == true);
    private void RealTimeToggle_Click(object sender, RoutedEventArgs e) => App.SetRealTimeIndexing(RealTimeToggle.IsChecked == true);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && (e.Key == Key.F || e.Key == Key.E)) { Navigate("search"); e.Handled = true; }
        else if (e.Key == Key.F1) { Navigate("help"); e.Handled = true; }
        else if (e.Key == Key.Escape && _current == "search" && Host.Content is SearchPage sp && sp.HandleEscape()) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }
}

public interface ISavablePage { void SavePage(); }
public interface IRefreshablePage { void RefreshPage(); }
