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
        if (!App.TestMode) { Activate(); Focus(); }
    }

    private void Activate(TabEntry entry)
    {
        if (_active != null) { _active.IsActive = false; _active.Pane.PersistColumns(); }
        _active = entry;
        entry.IsActive = true;
        Host.Content = entry.Pane;
        Title = entry.Tab.Title + App.TitleSuffix;
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

    /// <summary>The owner's sort hotkeys (Sort options): "sort:&lt;key&gt;" or "sortdir:asc/desc", or null.</summary>
    private static string? SortHotkeyCommand(Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return null;
        // in a text box, Ctrl+Alt is AltGr (typing characters) and plain keys are typing
        var mods = Keyboard.Modifiers;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase && (mods == ModifierKeys.None || mods.HasFlag(ModifierKeys.Control | ModifierKeys.Alt) || mods == ModifierKeys.Shift)) return null;
        string g = ListEditorWindow.FormatGesture(mods, key);
        foreach (var (k, v) in AppSettings.Current.Explorer.SortHotkeys)
            if (string.Equals(v, g, StringComparison.OrdinalIgnoreCase))
                return k == "Ascending" ? "sortdir:asc" : k == "Descending" ? "sortdir:desc" : "sort:" + k;
        return null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;   // with Alt held WPF reports Key.System
        var pane = _active?.Pane;
        if (pane != null && SortHotkeyCommand(key) is { } sortCmd) { pane.Execute(sortCmd); e.Handled = true; }
        else if (ctrl && key == Key.T) { OpenTab(_active?.Tab.Path); e.Handled = true; }
        else if (ctrl && key == Key.W) { if (_active != null) CloseTab(_active); e.Handled = true; }
        else if (ctrl && key == Key.Tab)
        {
            if (Tabs.Count > 1 && _active != null)
            {
                int i = Tabs.IndexOf(_active);
                Activate(Tabs[(i + (shift ? Tabs.Count - 1 : 1)) % Tabs.Count]);
            }
            e.Handled = true;
        }
        // Explorer's view keys: Ctrl+Shift+1 extra large icons … Ctrl+Shift+6 details (before Ctrl+1…9, which switches tabs)
        else if (ctrl && shift && key >= Key.D1 && key <= Key.D6) { pane?.Execute("view:" + ContextMenuBuilder.ViewModes[key - Key.D1].Mode); e.Handled = true; }
        else if (ctrl && shift && key == Key.N) { pane?.Execute("newfolder"); e.Handled = true; }   // before Ctrl+N (new window)
        else if (ctrl && key >= Key.D1 && key <= Key.D9)
        {
            int n = key - Key.D1;
            if (n < Tabs.Count) Activate(Tabs[n]);
            e.Handled = true;
        }
        else if (ctrl && key == Key.N) { App.ShowExplorer(_active?.Tab.Path, newWindow: true); e.Handled = true; }
        else if (ctrl && key == Key.L || alt && key == Key.D) { pane?.BeginAddressEdit(); e.Handled = true; }
        else if (ctrl && (key == Key.F || key == Key.E)) { pane?.FocusSearch(); e.Handled = true; }
        else if (alt && key == Key.P) { pane?.TogglePreview(); e.Handled = true; }
        else if (ctrl && shift && key == Key.C) { pane?.Execute("copypath"); e.Handled = true; }
        else if (key == Key.F5) { pane?.Execute("refresh"); e.Handled = true; }
        else if (key == Key.F1) { App.ShowHub(null, "help"); e.Handled = true; }
        else if (alt && key == Key.Left) { pane?.Execute("back"); e.Handled = true; }
        else if (alt && key == Key.Right) { pane?.Execute("forward"); e.Handled = true; }
        else if (alt && key == Key.Up) { pane?.Execute("up"); e.Handled = true; }
        else if (key == Key.BrowserBack) { pane?.Execute("back"); e.Handled = true; }
        else if (key == Key.BrowserForward) { pane?.Execute("forward"); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { _active?.Pane.Execute("back"); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { _active?.Pane.Execute("forward"); e.Handled = true; }
        base.OnPreviewMouseDown(e);
    }
}
