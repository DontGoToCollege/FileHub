using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileHub.Core.Settings;
using FileHub.UI.Common;

namespace FileHub.UI.Explorer;

public sealed class TabEntry : ObservableObject
{
    private bool _isActive;
    public ExplorerTab Tab { get; }
    public ExplorerPane Pane { get; }
    public string Title => Tab.Title;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public TabEntry(ExplorerTab tab)
    {
        Tab = tab;
        Pane = new ExplorerPane(tab);
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ExplorerTab.Title)) OnPropertyChanged(nameof(Title)); };
    }
}

public partial class ExplorerWindow : Window
{
    public ObservableCollection<TabEntry> Tabs { get; } = new();
    private TabEntry? _active;

    public ExplorerWindow(string? path = null)
    {
        InitializeComponent();
        DataContext = this;
        ThemeManager.ApplyToWindow(this);
        OpenTab(path);
        Closing += (_, _) => { foreach (var t in Tabs) { t.Pane.PersistColumns(); t.Tab.CancelLoads(); } AppSettings.Current.Save(); };
    }

    public TabEntry OpenTab(string? path = null, bool activate = true)
    {
        var entry = new TabEntry(new ExplorerTab(path));
        Tabs.Add(entry);
        if (activate) Activate(entry);
        return entry;
    }

    public void NavigateActive(string path)
    {
        if (_active == null) OpenTab(path); else { _active.Tab.Navigate(path); }
        Activate(); Focus();
    }

    private void Activate(TabEntry entry)
    {
        if (_active != null) { _active.IsActive = false; _active.Pane.PersistColumns(); }
        _active = entry;
        entry.IsActive = true;
        Host.Content = entry.Pane;
        Title = entry.Tab.Title + " - FileHub";
        Dispatcher.BeginInvoke(() => entry.Pane.FocusList(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CloseTab(TabEntry entry)
    {
        int idx = Tabs.IndexOf(entry);
        if (idx < 0) return;
        entry.Pane.PersistColumns();
        entry.Tab.CancelLoads();
        Tabs.Remove(entry);
        if (Tabs.Count == 0) { Close(); return; }
        if (entry == _active) Activate(Tabs[Math.Min(idx, Tabs.Count - 1)]);
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement f && f.Tag is TabEntry t) Activate(t);
    }

    private void Tab_MiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement f && f.Tag is TabEntry t) { CloseTab(t); e.Handled = true; }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement f && f.Tag is TabEntry t) CloseTab(t);
        e.Handled = true;
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => OpenTab(_active?.Tab.Path);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var pane = _active?.Pane;
        if (ctrl && e.Key == Key.T) { OpenTab(_active?.Tab.Path); e.Handled = true; }
        else if (ctrl && e.Key == Key.W) { if (_active != null) CloseTab(_active); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab)
        {
            if (Tabs.Count > 1 && _active != null)
            {
                int i = Tabs.IndexOf(_active);
                Activate(Tabs[(i + (shift ? Tabs.Count - 1 : 1)) % Tabs.Count]);
            }
            e.Handled = true;
        }
        else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int n = e.Key - Key.D1;
            if (n < Tabs.Count) Activate(Tabs[n]);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N) { App.ShowExplorer(_active?.Tab.Path, newWindow: true); e.Handled = true; }
        else if (ctrl && e.Key == Key.L || alt && e.Key == Key.D) { pane?.BeginAddressEdit(); e.Handled = true; }
        else if (ctrl && (e.Key == Key.F || e.Key == Key.E)) { pane?.FocusSearch(); e.Handled = true; }
        else if (alt && e.Key == Key.P) { pane?.TogglePreview(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.N) { pane?.Execute("newfolder"); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.C) { pane?.Execute("copypath"); e.Handled = true; }
        else if (e.Key == Key.F5) { pane?.Execute("refresh"); e.Handled = true; }
        else if (e.Key == Key.F1) { App.ShowHub(null, "help"); e.Handled = true; }
        else if (alt && e.Key == Key.Left) { pane?.Execute("back"); e.Handled = true; }
        else if (alt && e.Key == Key.Right) { pane?.Execute("forward"); e.Handled = true; }
        else if (alt && e.Key == Key.Up) { pane?.Execute("up"); e.Handled = true; }
        else if (e.Key == Key.BrowserBack) { pane?.Execute("back"); e.Handled = true; }
        else if (e.Key == Key.BrowserForward) { pane?.Execute("forward"); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { _active?.Pane.Execute("back"); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { _active?.Pane.Execute("forward"); e.Handled = true; }
        base.OnPreviewMouseDown(e);
    }
}
