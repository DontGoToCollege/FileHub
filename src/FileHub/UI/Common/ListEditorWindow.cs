using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FileHub.UI.Common;

/// <summary>One line in a <see cref="ListEditorWindow"/>.</summary>
public sealed class EditRow
{
    public string Id = "";
    public string Label = "";
    public bool Shown = true;
    public bool CanHide = true;
    public bool Movable = true;
    /// <summary>null = this row has no hotkey box.</summary>
    public string? Hotkey;
    /// <summary>The program the entry comes from (context menu editor); the "From" column shows when any row has one.</summary>
    public string? From;
    public System.Windows.Media.ImageSource? Icon;
}

/// <summary>
/// A large "choose and order" list like Explorer's Choose Details: tick to show, move up/down, optional hotkey
/// per row, optional scope switch (which kind of thing the list applies to). Used for the Sort by options and
/// the context menu editor.
/// </summary>
public sealed class ListEditorWindow : Window
{
    private List<EditRow> _rows;
    private readonly StackPanel _list = new();
    private readonly Func<string?, List<EditRow>> _load, _defaults;
    private readonly bool _hotkeys, _from;

    public string? Scope { get; private set; }
    public List<EditRow> Rows => _rows;
    /// <summary>Checked on OK; returns an error to show, or null to accept.</summary>
    public Func<List<EditRow>, string?>? Validate { get; init; }

    public ListEditorWindow(string title, string intro, (string Key, string Label)[] scopes, string? scope,
        Func<string?, List<EditRow>> load, Func<string?, List<EditRow>> defaults, bool hotkeys)
    {
        _load = load; _defaults = defaults; _hotkeys = hotkeys; Scope = scope;
        _rows = load(scope);
        _from = _rows.Any(r => r.From != null);
        Title = title; Width = hotkeys || _from ? 640 : 520; Height = 680; MinWidth = 420; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UIFont"); FontSize = 13;
        UseLayoutRounding = true;

        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (scopes.Length > 1)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(new TextBlock { Text = "Applies to:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            foreach (var (key, label) in scopes)
            {
                var rb = new RadioButton { Content = label, GroupName = "scope", IsChecked = key == scope, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                var k = key;
                rb.Checked += (_, _) => { Scope = k; _rows = _load(k); Rebuild(); };
                sp.Children.Add(rb);
            }
            top.Children.Add(sp);
        }
        else if (scopes.Length == 1) top.Children.Add(new TextBlock { Text = "Applies to: " + scopes[0].Label, Margin = new Thickness(0, 0, 0, 10) });
        top.Children.Add(HeaderRow());
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var reset = new Button { Content = "Reset to defaults", Padding = new Thickness(12, 4, 12, 4) };
        reset.Click += (_, _) => { _rows = _defaults(Scope); Rebuild(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(20, 4, 20, 4), Style = (Style)FindResource("AccentButton") };
        ok.Click += (_, _) =>
        {
            if (Validate?.Invoke(_rows) is { } err) { MessageBox.Show(this, err, Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true;
        };
        DockPanel.SetDock(reset, Dock.Left); DockPanel.SetDock(cancel, Dock.Right); DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(reset); buttons.Children.Add(cancel); buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Rebuild();
    }

    private Grid NewRowGrid()
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_hotkeys ? 170 : 0) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_from ? 160 : 0) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        return g;
    }

    private Grid HeaderRow()
    {
        var g = NewRowGrid();
        void Cell(string text, int col) { var t = new TextBlock { Text = text, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) }; Grid.SetColumn(t, col); g.Children.Add(t); }
        Cell("Show", 0); Cell("Name", 1); if (_hotkeys) Cell("Hotkey", 2); if (_from) Cell("From", 3); Cell("Order", 4);
        return g;
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var g = NewRowGrid();
            CheckBox? cb = null;
            if (row.CanHide)
            {
                // the whole cell is clickable, not just the little box
                cb = new CheckBox { IsChecked = row.Shown, VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Background = System.Windows.Media.Brushes.Transparent };
                cb.Checked += (_, _) => row.Shown = true;     // any way it's ticked: click, Space, accessibility tools
                cb.Unchecked += (_, _) => row.Shown = false;
                g.Children.Add(cb);
            }
            var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = row.Id.StartsWith("---") ? 0.6 : 1 };
            var name = new DockPanel { Background = System.Windows.Media.Brushes.Transparent };
            if (_from) name.Children.Add(new Image { Source = row.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(label);
            if (cb != null) name.MouseLeftButtonUp += (_, _) => cb.IsChecked = cb.IsChecked != true;   // clicking the name ticks too
            Grid.SetColumn(name, 1); g.Children.Add(name);
            if (_from && !string.IsNullOrEmpty(row.From))
            {
                var from = new TextBlock { Text = row.From, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0), ToolTip = row.From };
                Grid.SetColumn(from, 3); g.Children.Add(from);
            }
            if (_hotkeys && row.Hotkey != null)
            {
                var box = new TextBox { Text = row.Hotkey, IsReadOnlyCaretVisible = false, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Click, then press the key combination. Backspace or Delete clears it." };
                box.PreviewKeyDown += (_, e) =>
                {
                    var key = e.Key == Key.System ? e.SystemKey : e.Key;
                    if (key is Key.Tab or Key.Escape) return;   // keep moving between boxes / closing the window
                    e.Handled = true;
                    if (IsModifier(key)) return;
                    row.Hotkey = (key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None ? "" : FormatGesture(Keyboard.Modifiers, key);
                    box.Text = row.Hotkey;
                };
                box.PreviewTextInput += (_, e) => e.Handled = true;
                Grid.SetColumn(box, 2); g.Children.Add(box);
            }
            if (row.Movable)
            {
                var moves = new StackPanel { Orientation = Orientation.Horizontal };
                int idx = i;
                moves.Children.Add(MoveButton("", "Move up", () => Move(idx, -1)));
                moves.Children.Add(MoveButton("", "Move down", () => Move(idx, +1)));
                Grid.SetColumn(moves, 4); g.Children.Add(moves);
            }
            _list.Children.Add(g);
        }
    }

    private Button MoveButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconButton"), FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), Width = 32, Height = 28 };
        b.Click += (_, _) => click();
        return b;
    }

    private void Move(int i, int dir)
    {
        int j = i + dir;
        if (j < 0 || j >= _rows.Count || !_rows[j].Movable) return;
        (_rows[i], _rows[j]) = (_rows[j], _rows[i]);
        Rebuild();
    }

    private static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    /// <summary>"Ctrl+Alt+1", "Ctrl+Shift+PageDown"…: the form hotkeys are stored and compared in.</summary>
    public static string FormatGesture(ModifierKeys m, Key k)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(k switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (k - Key.NumPad0),
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Enter => "Enter",
            _ => new KeyConverter().ConvertToInvariantString(k) ?? k.ToString(),
        });
        return string.Join("+", parts);
    }
}
