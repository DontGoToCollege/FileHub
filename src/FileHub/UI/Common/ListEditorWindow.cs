using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

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
    public ImageSource? Icon;
}

/// <summary>
/// A large "choose and order" list like Explorer's Choose Details: tick to show, select rows (click, Ctrl, Shift),
/// drag them or move them with the buttons, optional hotkey per row, optional scope switch (which kind of thing the
/// list applies to) and optional separators the owner adds. Used for the Sort by options and the context menu editor.
/// </summary>
public sealed class ListEditorWindow : Window
{
    /// <summary>Ids of separators added in the editor (the menu's own are "---1", "---2"…).</summary>
    public const string AddedSeparatorPrefix = "---+";
    public const string SeparatorLabel = "──────── separator ────────";

    private List<EditRow> _rows;
    private readonly StackPanel _list = new();
    private readonly Grid _host = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Rectangle _marker = new() { Height = 2, Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _scroll;
    private readonly List<Border> _borders = new();
    private readonly List<CheckBox?> _checks = new();
    private readonly HashSet<EditRow> _selected = new();
    private EditRow? _anchor;
    private readonly Func<string?, List<EditRow>> _load, _defaults;
    private readonly bool _hotkeys, _from, _separators;

    // dragging rows
    private EditRow? _pressRow;
    private Point _pressPoint;
    private bool _clickSelectsOnly, _dragging;
    private int _dropIndex;
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };

    public string? Scope { get; private set; }
    public List<EditRow> Rows => _rows;
    /// <summary>Checked on OK; returns an error to show, or null to accept.</summary>
    public Func<List<EditRow>, string?>? Validate { get; init; }

    public ListEditorWindow(string title, string intro, (string Key, string Label)[] scopes, string? scope,
        Func<string?, List<EditRow>> load, Func<string?, List<EditRow>> defaults, bool hotkeys, bool separators = false)
    {
        _load = load; _defaults = defaults; _hotkeys = hotkeys; _separators = separators; Scope = scope;
        _rows = load(scope);
        _from = _rows.Any(r => r.From != null);
        Title = title; Width = hotkeys || _from ? 660 : 540; Height = 720; MinWidth = 460; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (FontFamily)FindResource("UIFont"); FontSize = 13;
        UseLayoutRounding = true;

        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        top.Children.Add(new TextBlock
        {
            Text = "Click a row to select it; Ctrl+click and Shift+click select more. Drag the selected rows to move them, or use the buttons below (Alt+Up / Alt+Down).",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 10),
        });
        if (scopes.Length > 1)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(new TextBlock { Text = "Applies to:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            foreach (var (key, label) in scopes)
            {
                var rb = new RadioButton { Content = label, GroupName = "scope", IsChecked = key == scope, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                var k = key;
                rb.Checked += (_, _) => { Scope = k; _rows = _load(k); _selected.Clear(); _anchor = null; Rebuild(); };
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
        reset.Click += (_, _) => { _rows = _defaults(Scope); _selected.Clear(); _anchor = null; Rebuild(); };
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

        // what the selected rows can do
        var tools = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Tool(string text, string tip, Action act) { var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4) }; b.Click += (_, _) => { act(); _scroll.Focus(); }; tools.Children.Add(b); }   // keys (Delete, arrows, Alt+Up) keep working on the list
        Tool("Move to top", "Move the selected rows to the top", () => MoveSelectedTo(FirstMovableIndex()));
        Tool("Move up", "Alt+Up", () => MoveSelectedBy(-1));
        Tool("Move down", "Alt+Down", () => MoveSelectedBy(+1));
        Tool("Move to bottom", "Move the selected rows to the bottom", () => MoveSelectedTo(LastMovableIndex() + 1));
        if (_separators)
        {
            Tool("Add separator", "Adds a separator line below the selected row (or at the end)", AddSeparator);
            Tool("Remove separator", "Removes the selected separators you added (Delete); Windows' own ones are unticked instead", RemoveSeparators);
        }
        DockPanel.SetDock(tools, Dock.Bottom);
        root.Children.Add(tools);

        _marker.SetResourceReference(Shape.FillProperty, "AccentBrush");
        _overlay.Children.Add(_marker);
        _host.Children.Add(_list);
        _host.Children.Add(_overlay);
        _host.Background = Brushes.Transparent;
        _host.MouseMove += Host_MouseMove;
        _host.MouseLeftButtonUp += Host_MouseUp;
        _host.LostMouseCapture += (_, _) => EndDrag(apply: false);
        _scroll = new ScrollViewer { Content = _host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true, FocusVisualStyle = null };
        _autoScroll.Tick += (_, _) => AutoScrollTick();
        root.Children.Add(_scroll);
        Content = root;
        Rebuild();
    }

    private Grid NewRowGrid()
    {
        var g = new Grid();
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
        g.Margin = new Thickness(4, 0, 4, 0);
        void Cell(string text, int col) { var t = new TextBlock { Text = text, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) }; Grid.SetColumn(t, col); g.Children.Add(t); }
        Cell("Show", 0); Cell("Name", 1); if (_hotkeys) Cell("Hotkey", 2); if (_from) Cell("From", 3); Cell("Order", 4);
        return g;
    }

    private void Rebuild()
    {
        _list.Children.Clear(); _borders.Clear(); _checks.Clear();
        _selected.IntersectWith(_rows);
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var g = NewRowGrid();
            CheckBox? cb = null;
            if (row.CanHide)
            {
                // the whole cell is clickable, not just the little box
                cb = new CheckBox { IsChecked = row.Shown, VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
                cb.Checked += (_, _) => row.Shown = true;     // any way it's ticked: click, Space, accessibility tools
                cb.Unchecked += (_, _) => row.Shown = false;
                cb.Click += (_, _) => { if (_selected.Contains(row) && _selected.Count > 1) SetShown(_selected, cb.IsChecked == true); };   // ticking one of several selected ticks them all
                g.Children.Add(cb);
            }
            var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = row.Id.StartsWith("---") ? 0.6 : 1 };
            var name = new DockPanel();
            if (_from) name.Children.Add(new Image { Source = row.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(label);
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
                moves.Children.Add(MoveButton("", "Move up", row, -1));
                moves.Children.Add(MoveButton("", "Move down", row, +1));
                Grid.SetColumn(moves, 4); g.Children.Add(moves);
            }
            var border = new Border { Child = g, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, 1, 0, 1), Tag = row };
            border.PreviewMouseLeftButtonDown += Row_MouseDown;
            border.MouseEnter += (_, _) => Paint(border);
            border.MouseLeave += (_, _) => Paint(border);
            _borders.Add(border); _checks.Add(cb);
            Paint(border);
            _list.Children.Add(border);
        }
    }

    /// <summary>Selected rows in the theme's selection colour, the row under the mouse in the hover colour.</summary>
    private void Paint(Border b)
    {
        bool sel = b.Tag is EditRow r && _selected.Contains(r);
        if (sel) b.SetResourceReference(Border.BackgroundProperty, b.IsMouseOver ? "SelectedHoverBrush" : "SelectedBrush");
        else if (b.IsMouseOver && !_dragging) b.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        else b.Background = Brushes.Transparent;
    }

    private void RepaintAll() { foreach (var b in _borders) Paint(b); }

    private Button MoveButton(string glyph, string tip, EditRow row, int dir)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("IconButton"), FontFamily = (FontFamily)FindResource("IconFont"), Width = 32, Height = 28 };
        b.Click += (_, _) => { if (!_selected.Contains(row)) SelectOnly(row); MoveSelectedBy(dir); };
        return b;
    }

    // ---------------------------------------------------------------- selection

    private void SelectOnly(EditRow row) { _selected.Clear(); _selected.Add(row); _anchor = _cursor = row; RepaintAll(); }

    private void SelectRange(EditRow to, bool add)
    {
        int a = _anchor != null ? _rows.IndexOf(_anchor) : -1, b = _rows.IndexOf(to);
        if (a < 0) a = b;
        if (!add) _selected.Clear();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _selected.Add(_rows[i]);
        RepaintAll();
    }

    private void SetShown(IEnumerable<EditRow> rows, bool shown)
    {
        foreach (var r in rows.ToList())
        {
            if (!r.CanHide) continue;
            r.Shown = shown;
            int i = _rows.IndexOf(r);
            if (i >= 0 && _checks[i] is { } c) c.IsChecked = shown;
        }
    }

    private static bool IsInside<T>(DependencyObject? d, DependencyObject stop) where T : DependencyObject
    {
        for (; d != null && d != stop; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    private void Row_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not EditRow row) return;
        var src = e.OriginalSource as DependencyObject;
        if (IsInside<ButtonBase>(src, b) || IsInside<TextBox>(src, b)) return;   // tick box, arrows and hotkey box work as before
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _clickSelectsOnly = false;
        if (shift) SelectRange(row, ctrl);
        else if (ctrl) { if (!_selected.Remove(row)) _selected.Add(row); _anchor = row; RepaintAll(); }
        else if (_selected.Contains(row) && _selected.Count > 1) { _clickSelectsOnly = true; _anchor = row; }   // keep the group for a drag; a plain click selects just this row
        else SelectOnly(row);
        _cursor = row;
        _pressRow = row; _pressPoint = e.GetPosition(_host);
        _scroll.Focus();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- dragging

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressRow == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _pressRow = null; return; }
        var pos = e.GetPosition(_host);
        if (!_dragging)
        {
            if (Math.Abs(pos.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(pos.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            if (!_selected.Contains(_pressRow)) SelectOnly(_pressRow);
            if (!_selected.Any(r => r.Movable)) { _pressRow = null; return; }
            _dragging = true; _clickSelectsOnly = false;
            _host.CaptureMouse();
            _autoScroll.Start();
            RepaintAll();
        }
        UpdateDrop(pos.Y);
    }

    private void Host_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) { EndDrag(apply: true); e.Handled = true; }
        else if (_clickSelectsOnly && _pressRow != null) SelectOnly(_pressRow);
        _pressRow = null; _clickSelectsOnly = false;
    }

    private void EndDrag(bool apply)
    {
        if (!_dragging) return;
        _dragging = false;
        _autoScroll.Stop();
        _marker.Visibility = Visibility.Collapsed;
        int drop = _dropIndex;
        if (_host.IsMouseCaptured) _host.ReleaseMouseCapture();
        _pressRow = null;
        if (apply) MoveSelectedTo(drop); else RepaintAll();
    }

    /// <summary>Where the dragged rows would land: before the first row whose middle is below the mouse.</summary>
    private void UpdateDrop(double y)
    {
        int idx = _rows.Count;
        double lineY = 0;
        for (int i = 0; i < _borders.Count; i++)
        {
            double t = _borders[i].TranslatePoint(new Point(0, 0), _host).Y, h = _borders[i].ActualHeight;
            lineY = t + h;
            if (y < t + h / 2) { idx = i; lineY = t; break; }
        }
        _dropIndex = idx;
        _marker.Width = _host.ActualWidth;
        Canvas.SetTop(_marker, Math.Max(0, lineY - 1));
        _marker.Visibility = Visibility.Visible;
    }

    /// <summary>While dragging near the top or bottom edge, scroll the list (faster nearer the edge).</summary>
    private void AutoScrollTick()
    {
        if (!_dragging) return;
        double y = Mouse.GetPosition(_scroll).Y, h = _scroll.ActualHeight, zone = 36, step = 0;
        if (y < zone) step = -(zone - Math.Max(y, -zone)) / 2;
        else if (y > h - zone) step = (Math.Min(y, h + zone) - (h - zone)) / 2;
        if (step == 0) return;
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + step);
        _scroll.UpdateLayout();
        UpdateDrop(Mouse.GetPosition(_host).Y);
    }

    // ---------------------------------------------------------------- moving

    private int FirstMovableIndex() => Math.Max(0, _rows.FindIndex(r => r.Movable));
    private int LastMovableIndex() => _rows.FindLastIndex(r => r.Movable);

    /// <summary>
    /// Puts the selected movable rows, in their order, before row <paramref name="insertIndex"/>. Fixed rows
    /// (not movable) keep their places: only the movable slots are reshuffled.
    /// </summary>
    private void MoveSelectedTo(int insertIndex)
    {
        var moving = _rows.Where(r => r.Movable && _selected.Contains(r)).ToList();
        if (moving.Count == 0) return;
        int before = _rows.Take(insertIndex).Count(r => r.Movable && !_selected.Contains(r));
        var rest = _rows.Where(r => r.Movable && !_selected.Contains(r)).ToList();
        rest.InsertRange(before, moving);
        Refill(rest);
        Rebuild();
        ShowRow(moving[0]);
    }

    /// <summary>Moves every selected row one place up or down, as a block where they touch (Alt+Up / Alt+Down).</summary>
    private void MoveSelectedBy(int dir)
    {
        var seq = _rows.Where(r => r.Movable).ToList();
        if (dir < 0) { for (int i = 1; i < seq.Count; i++) if (_selected.Contains(seq[i]) && !_selected.Contains(seq[i - 1])) (seq[i], seq[i - 1]) = (seq[i - 1], seq[i]); }
        else { for (int i = seq.Count - 2; i >= 0; i--) if (_selected.Contains(seq[i]) && !_selected.Contains(seq[i + 1])) (seq[i], seq[i + 1]) = (seq[i + 1], seq[i]); }
        Refill(seq);
        Rebuild();
        var first = dir < 0 ? _rows.FirstOrDefault(_selected.Contains) : _rows.LastOrDefault(_selected.Contains);
        if (first != null) ShowRow(first);
    }

    /// <summary>Writes the movable rows back into the movable slots, in the new order.</summary>
    private void Refill(List<EditRow> movable)
    {
        int k = 0;
        for (int i = 0; i < _rows.Count; i++) if (_rows[i].Movable) _rows[i] = movable[k++];
    }

    private void ShowRow(EditRow row)
    {
        int i = _rows.IndexOf(row);
        if (i >= 0) Dispatcher.BeginInvoke(() => { if (i < _borders.Count) _borders[i].BringIntoView(); }, DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- separators

    private void AddSeparator()
    {
        int n = 1;
        while (_rows.Any(r => r.Id == AddedSeparatorPrefix + n)) n++;
        var sep = new EditRow { Id = AddedSeparatorPrefix + n, Label = SeparatorLabel, From = _from ? "" : null };
        int last = _rows.FindLastIndex(_selected.Contains);
        int at = last >= 0 ? last + 1 : LastMovableIndex() + 1;
        _rows.Insert(at, sep);
        SelectOnly(sep);
        Rebuild();
        ShowRow(sep);
    }

    private void RemoveSeparators()
    {
        var seps = _selected.Where(r => r.Id.StartsWith("---")).ToList();
        _rows.RemoveAll(r => seps.Contains(r) && r.Id.StartsWith(AddedSeparatorPrefix));
        SetShown(seps.Where(r => !r.Id.StartsWith(AddedSeparatorPrefix)), false);   // the menu's own: hidden, so Reset can bring them back
        Rebuild();
    }

    // ---------------------------------------------------------------- keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_dragging && key == Key.Escape) { EndDrag(apply: false); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox || !_scroll.IsKeyboardFocusWithin) { base.OnPreviewKeyDown(e); return; }   // the list's keys only while it has focus
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (alt && key is Key.Up or Key.Down) { MoveSelectedBy(key == Key.Up ? -1 : 1); e.Handled = true; }
        else if (ctrl && key == Key.A) { _selected.UnionWith(_rows); RepaintAll(); e.Handled = true; }
        else if (key is Key.Up or Key.Down && _rows.Count > 0)
        {
            var from = _cursor ?? _anchor;
            int cur = from != null ? _rows.IndexOf(from) : -1;
            int next = Math.Clamp(cur < 0 ? 0 : cur + (key == Key.Up ? -1 : 1), 0, _rows.Count - 1);
            if (shift) { _anchor ??= _rows[next]; SelectRange(_rows[next], false); }   // anchor stays, the range follows the arrow
            else SelectOnly(_rows[next]);
            _cursor = _rows[next];
            ShowRow(_rows[next]);
            e.Handled = true;
        }
        else if (key == Key.Space && _selected.Count > 0 && Keyboard.FocusedElement is not ButtonBase)
        {
            var first = _rows.First(_selected.Contains);
            SetShown(_selected, !first.Shown);
            e.Handled = true;
        }
        else if (key == Key.Delete && _separators && _selected.Any(r => r.Id.StartsWith("---"))) { RemoveSeparators(); e.Handled = true; }
        if (!e.Handled) base.OnPreviewKeyDown(e);
    }
    private EditRow? _cursor;   // the row the arrow keys are on (Shift+arrows grow the range from the anchor to it)

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
