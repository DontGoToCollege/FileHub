using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;

namespace FileHub.UI.Common;

/// <summary>
/// Builds the WPF context menu: FileHub built-ins + user custom items + the genuine Windows shell
/// menu (inline, as a submenu, or hidden — per settings). Commands are dispatched by name to the host.
/// </summary>
public static class ContextMenuBuilder
{
    private static readonly HashSet<string> DedupeVerbs = new(StringComparer.OrdinalIgnoreCase)
    { "open", "explore", "cut", "copy", "paste", "pastelink", "delete", "rename", "properties", "opennewwindow", "opencontaining" };
    private static readonly HashSet<string> DedupeLabels = new(StringComparer.OrdinalIgnoreCase)
    { "Open", "Cut", "Copy", "Paste", "Delete", "Rename", "Properties", "Refresh", "View", "Sort by", "Group by", "Paste shortcut", "Undo", "Open in new tab", "Open in new window", "Open in Terminal", "Copy as path" };

    /// <summary>The Sort by entries to show, in the owner's order (unknown keys ignored).</summary>
    public static IEnumerable<(string Label, string Key)> ShownSortKeys(IReadOnlyCollection<string> choices)
        => choices.Where(SortFields.IsKnown).Select(k => (SortFields.Label(k), k));

    /// <summary>The hotkey shown beside a sort (or "Ascending"/"Descending") in the menus.</summary>
    public static string? SortHotkey(string key) => AppSettings.Current.Explorer.SortHotkeys.TryGetValue(key, out var g) && g.Length > 0 ? g : null;

    /// <summary>Fills a Sort by menu: the chosen sorts and the order, with the current ones ticked like Explorer, then More… and Sort options.</summary>
    public static void FillSortMenu(ItemsControl sort, Action<string> command, IReadOnlyCollection<string> choices, string? sortBy, bool sortDesc)
    {
        foreach (var (label, key) in ShownSortKeys(choices))
        {
            var mi = new MenuItem { Header = label, InputGestureText = SortHotkey(key), IsChecked = string.Equals(key, sortBy, StringComparison.OrdinalIgnoreCase) };
            var k = key; mi.Click += (_, _) => command("sort:" + k); sort.Items.Add(mi);
        }
        sort.Items.Add(new Separator());
        foreach (var (l, c, desc) in new[] { ("Ascending", "sortdir:asc", false), ("Descending", "sortdir:desc", true) })
        {
            var mi = new MenuItem { Header = l, InputGestureText = SortHotkey(l), IsChecked = sortBy != null && sortDesc == desc };
            mi.Click += (_, _) => command(c); sort.Items.Add(mi);
        }
        sort.Items.Add(new Separator());
        var more = new MenuItem { Header = "More…" }; more.Click += (_, _) => command("sortwindow"); sort.Items.Add(more);
        sort.Items.Add(SortOptionsMenu(command));
    }

    /// <summary>The Explorer view hotkeys (Ctrl+Shift+1…6), in View menu order.</summary>
    public static readonly (string Label, ViewMode Mode, string Gesture)[] ViewModes =
    {
        ("Extra large icons", ViewMode.ExtraLargeIcons, "Ctrl+Shift+1"), ("Large icons", ViewMode.LargeIcons, "Ctrl+Shift+2"),
        ("Medium icons", ViewMode.MediumIcons, "Ctrl+Shift+3"), ("Small icons", ViewMode.SmallIcons, "Ctrl+Shift+4"),
        ("List", ViewMode.List, "Ctrl+Shift+5"), ("Details", ViewMode.Details, "Ctrl+Shift+6"),
    };

    /// <summary>"Sort options" submenu: the sorts-and-hotkeys window, and whether sort changes stick to this folder or apply to all folders.</summary>
    public static MenuItem SortOptionsMenu(Action<string> command)
    {
        bool all = AppSettings.Current.Explorer.SortAllFolders;
        var opts = new MenuItem { Header = "Sort options" };
        var custom = new MenuItem { Header = "Choose sorts and hotkeys…" }; custom.Click += (_, _) => command("sortwindow"); opts.Items.Add(custom);
        opts.Items.Add(new Separator());
        var folder = new MenuItem { Header = "Remember sort for each folder", IsChecked = !all }; folder.Click += (_, _) => command("sortscope:folder"); opts.Items.Add(folder);
        var every = new MenuItem { Header = "Use the same sort for all folders", IsChecked = all }; every.Click += (_, _) => command("sortscope:all"); opts.Items.Add(every);
        opts.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset this folder to the default sort", IsEnabled = !all }; reset.Click += (_, _) => command("sortreset"); opts.Items.Add(reset);
        return opts;
    }

    public static ContextMenu Build(FrameworkElement owner, IReadOnlyList<FileItem> items, string? folder, Action<string> command,
        IReadOnlyCollection<string>? sortChoices = null, string? sortBy = null, bool sortDesc = false)
    {
        var s = AppSettings.Current.ContextMenu;
        var menu = new ContextMenu();
        bool background = items.Count == 0;
        bool single = items.Count == 1;
        bool allDirs = items.Count > 0 && items.All(i => i.IsDirectory);
        bool anyVirtual = items.Any(i => i.IsVirtual);
        var paths = items.Where(i => !i.IsVirtual).Select(i => i.FullPath).ToList();
        string? realFolder = folder != null && !folder.StartsWith("::") ? folder : null;

        // copies a view into the folder's and every subfolder's own saved view (Explorer only: it handles "viewtree:")
        MenuItem ViewTreeMenu()
        {
            var m = new MenuItem { Header = "Set view for this folder and subfolders" };
            foreach (var (label, mode, _) in ViewModes) { var mi = new MenuItem { Header = label }; mi.Click += (_, _) => command("viewtree:" + mode); m.Items.Add(mi); }
            return m;
        }

        MenuItem Add(string header, string cmd, string? gesture = null, bool bold = false, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            if (bold) mi.FontWeight = FontWeights.SemiBold;
            mi.Click += (_, _) => command(cmd);
            menu.Items.Add(mi);
            return mi;
        }

        if (background)
        {
            var view = new MenuItem { Header = "View" };
            foreach (var (label, mode, gesture) in ViewModes)
            { var mi = new MenuItem { Header = label, InputGestureText = gesture }; mi.Click += (_, _) => command("view:" + mode); view.Items.Add(mi); }
            menu.Items.Add(view);
            if (realFolder != null) menu.Items.Add(ViewTreeMenu());
            var sort = new MenuItem { Header = "Sort by" };
            FillSortMenu(sort, command, sortChoices ?? AppSettings.Current.Explorer.SortChoices, sortBy, sortDesc);
            menu.Items.Add(sort);
            Add("Refresh", "refresh", "F5");
            menu.Items.Add(new Separator());
            var nw = new MenuItem { Header = "New" };
            var nf = new MenuItem { Header = "Folder", InputGestureText = "Ctrl+Shift+N" }; nf.Click += (_, _) => command("newfolder"); nw.Items.Add(nf);
            var nt = new MenuItem { Header = "Text Document" }; nt.Click += (_, _) => command("newfile"); nw.Items.Add(nt);
            menu.Items.Add(nw);
            Add("Paste", "paste", "Ctrl+V", enabled: realFolder != null && FileOperations.ClipboardHasFiles());
            if (realFolder != null)
            {
                if (s.ShowOpenTerminal) Add("Open in Terminal", "openterminal");
                if (s.ShowOpenInHub) Add("Search this folder in Hub", "openhub");
                if (s.ShowCopyPath) Add("Copy folder path", "copypath");
            }
        }
        else
        {
            if (single && items[0].IsDirectory)
            {
                Add("Open", "open", "Enter", bold: true);
                if (!anyVirtual) Add("Open in new tab", "newtab");
                if (!anyVirtual && realFolder != null) menu.Items.Add(ViewTreeMenu());
            }
            else if (single)
            {
                Add("Open", "open", "Enter", bold: true);
                Add("Open with…", "openwith");
            }
            else Add("Open", "open", "Enter", bold: true);

            if (!anyVirtual)
            {
                if (s.ShowOpenTerminal && (allDirs || single)) Add("Open in Terminal", "openterminal");
                if (s.ShowOpenInHub && allDirs) Add("Search in Hub", "openhub");
                menu.Items.Add(new Separator());
                if (s.ShowBuiltIns)
                {
                    Add("Cut", "cut", "Ctrl+X");
                    Add("Copy", "copy", "Ctrl+C");
                    if (single && allDirs) Add("Paste into folder", "pasteinto", enabled: FileOperations.ClipboardHasFiles());
                    Add("Rename", "rename", "F2", enabled: single);
                    Add("Delete", "delete", "Del");
                }
                if (s.ShowCopyPath) Add("Copy as path", "copypath", "Ctrl+Shift+C");
                Add("Show in Windows Explorer", "showinexplorer");
                if (single)
                {
                    string p = items[0].FullPath;
                    bool pinned = App.IsPinnedToStart(p);
                    var pin = new MenuItem { Header = pinned ? "Unpin from FileHub Start" : "Pin to FileHub Start" };
                    pin.Click += (_, _) => { if (pinned) App.UnpinFromStart(p); else App.PinToStart(p); };
                    menu.Items.Add(pin);
                }
            }
        }

        // custom items
        var custom = s.Items.Where(ci => ci.Enabled && Applies(ci, items, background)).ToList();
        if (custom.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var ci in custom)
            {
                var mi = new MenuItem { Header = ci.Label, Tag = "Your custom item" };
                var captured = ci;
                mi.Click += (_, _) => ShellUtil.RunCustom(captured, paths, realFolder);
                menu.Items.Add(mi);
            }
        }

        // Windows shell items
        if (s.ShellMode != ShellMenuMode.Hidden && !anyVirtual && (paths.Count > 0 || realFolder != null) && !Responds(paths.Count > 0 ? paths[0] : realFolder!))
        {
            // Windows' menu would wait on the UI thread until the drive spins up (seconds, "not responding")
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Windows entries: the drive is waking up, right-click again", IsEnabled = false });
        }
        else if (s.ShellMode != ShellMenuMode.Hidden && !anyVirtual && (paths.Count > 0 || realFolder != null))
        {
            ShellContextMenu? shell = null;
            try
            {
                IntPtr hwnd = Window.GetWindow(owner) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
                bool extended = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
                shell = paths.Count > 0 ? ShellContextMenu.ForItems(hwnd, paths, extended) : ShellContextMenu.ForBackground(hwnd, realFolder!, extended);
            }
            catch (Exception ex) { Platform.Log.Warn("Shell menu unavailable: " + ex.Message); }

            if (shell != null)
            {
                var entries = shell.RootEntries().Where(e => !IsDuplicate(e, s.HiddenShellItems)).ToList();
                // collapse duplicate separators
                var cleaned = new List<ShellMenuEntry>();
                foreach (var e in entries) { if (e.IsSeparator && (cleaned.Count == 0 || cleaned[^1].IsSeparator)) continue; cleaned.Add(e); }
                while (cleaned.Count > 0 && cleaned[^1].IsSeparator) cleaned.RemoveAt(cleaned.Count - 1);
                if (cleaned.Count > 0)
                {
                    ItemsControl target = menu;
                    if (s.ShellMode == ShellMenuMode.Submenu)
                    {
                        menu.Items.Add(new Separator());
                        var more = new MenuItem { Header = "Show more options", Tag = ShellMenuSources.Windows };
                        menu.Items.Add(more);
                        target = more;
                    }
                    else menu.Items.Add(new Separator());
                    foreach (var e in cleaned) target.Items.Add(ToMenuItem(shell, e));
                }
                menu.Closed += (_, _) =>
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    t.Tick += (_, _) => { t.Stop(); shell.Dispose(); };
                    t.Start();
                };
            }
        }

        if (!anyVirtual && (paths.Count > 0 || realFolder != null))
        {
            menu.Items.Add(new Separator());
            Add("Properties", "properties", "Alt+Enter");
        }

        // the owner's layout for this kind of thing, then "Context menu…" (never hideable, so the editor stays reachable)
        var scopes = MenuScopes(items, background);
        var full = EntryIds(menu);
        var fullItems = menu.Items.Cast<object>().ToList();
        var layouts = s.Layouts;
        if (scopes.Select(sc => layouts.GetValueOrDefault(sc.Key)).FirstOrDefault(l => l != null) is { } layout) ApplyLayout(menu, layout);
        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Context menu…" };
        edit.Click += (_, _) => EditLayout(owner, scopes, full, Sources(full, fullItems, paths, background ? realFolder : null));
        menu.Items.Add(edit);
        return menu;
    }

    // ---------------------------------------------------------------- owner's menu layouts

    /// <summary>Which layouts can apply to this menu, most specific first (a file type before all files).</summary>
    private static (string Key, string Label)[] MenuScopes(IReadOnlyList<FileItem> items, bool background)
    {
        if (background) return new[] { ("background", "Empty space in a folder") };
        if (items.All(i => i.IsDirectory)) return new[] { ("folders", "Folders") };
        if (items.Any(i => i.IsDirectory)) return new[] { ("mixed", "Files and folders together") };
        var exts = items.Select(i => i.Extension.ToLowerInvariant()).Distinct().ToList();
        return exts.Count == 1 && exts[0].Length > 0
            ? new[] { ("ext:" + exts[0], "." + exts[0] + " files"), ("files", "All files") }
            : new[] { ("files", "All files") };
    }

    /// <summary>Stable names for the menu's top-level entries: the label (access-key marks removed), separators "---1", "---2"…</summary>
    private static List<string> EntryIds(ItemsControl menu)
    {
        var ids = new List<string>();
        int sep = 0;
        foreach (var o in menu.Items) ids.Add(o is Separator ? "---" + (++sep) : o is MenuItem mi ? (mi.Header?.ToString() ?? "").Replace("_", "") : "");
        return ids;
    }

    private static void ApplyLayout(ItemsControl menu, MenuLayout layout)
    {
        var items = menu.Items.Cast<object>().ToList();
        var ids = EntryIds(menu);
        var rank = new Dictionary<string, int>();
        for (int i = 0; i < layout.Order.Count; i++) rank.TryAdd(layout.Order[i], i);
        var hidden = new HashSet<string>(layout.Hidden);
        // entries the layout doesn't know yet (new shell items…) go after the ordered ones, in their own order
        var placed = items.Select((o, i) => (o, id: ids[i], r: rank.TryGetValue(ids[i], out var k) ? k : layout.Order.Count + i)).ToList();
        // separators the owner added in the editor
        placed.AddRange(layout.Order.Select((id, i) => (id, i)).Where(x => x.id.StartsWith(ListEditorWindow.AddedSeparatorPrefix))
            .Select(x => ((object)new Separator(), x.id, x.i)));
        var kept = placed.Where(x => !hidden.Contains(x.id)).OrderBy(x => x.r).Select(x => x.o).ToList();
        menu.Items.Clear();
        foreach (var o in kept)
        {
            if (o is Separator && (menu.Items.Count == 0 || menu.Items[^1] is Separator)) continue;
            menu.Items.Add(o);
        }
        while (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
    }

    /// <summary>Which program each top-level entry comes from (FileHub, Windows, WinRAR…) and its icon, by entry id.</summary>
    private static Dictionary<string, (string From, System.Windows.Media.ImageSource? Icon)> Sources(List<string> ids, List<object> items, List<string> paths, string? backgroundFolder)
    {
        var result = new Dictionary<string, (string, System.Windows.Media.ImageSource?)>();
        Dictionary<string, string>? shell = null;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not MenuItem mi) continue;
            string from;
            if (mi.Tag is ShellMenuEntry e)
            {
                shell ??= ShellMenuSources.Resolve(paths, backgroundFolder);
                from = e.Verb != null && shell.TryGetValue("verb:" + e.Verb.ToLowerInvariant(), out var p) || shell.TryGetValue(ShellMenuSources.Norm(e.Label), out p) ? p : ShellMenuSources.Windows;
            }
            else from = mi.Tag as string ?? "FileHub";
            result.TryAdd(ids[i], (from, (mi.Icon as System.Windows.Controls.Image)?.Source));
        }
        return result;
    }

    /// <summary>The "Context menu…" window: show/hide and reorder this menu's entries for this kind of thing.</summary>
    private static void EditLayout(FrameworkElement owner, (string Key, string Label)[] scopes, List<string> full,
        Dictionary<string, (string From, System.Windows.Media.ImageSource? Icon)> sources)
    {
        var s = AppSettings.Current.ContextMenu;
        EditRow Row(string id, bool shown = true)
        {
            bool sep = id.StartsWith("---");
            var src = sources.GetValueOrDefault(id);
            return new EditRow { Id = id, Label = sep ? ListEditorWindow.SeparatorLabel : id, Shown = shown, From = sep ? "" : src.From ?? "FileHub", Icon = src.Icon };
        }
        List<EditRow> Defaults(string? _) => full.Select(id => Row(id)).ToList();
        List<EditRow> Load(string? scope)
        {
            if (scope == null || !s.Layouts.TryGetValue(scope, out var l)) return Defaults(scope);
            var rank = l.Order.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
            var ids = full.Concat(l.Order.Where(id => id.StartsWith(ListEditorWindow.AddedSeparatorPrefix)).Distinct()).ToList();   // + separators added here
            return ids.Select((id, i) => (id, r: rank.TryGetValue(id, out var k) ? k : l.Order.Count + i)).OrderBy(x => x.r)
                .Select(x => Row(x.id, !l.Hidden.Contains(x.id))).ToList();
        }
        // open on the most specific scope that already has a layout, else the most specific one
        string start = scopes.FirstOrDefault(sc => s.Layouts.ContainsKey(sc.Key)).Key ?? scopes[0].Key;
        var win = new ListEditorWindow("Context menu", "Tick the entries to show in this menu and put them in the order you want. "
            + "\"From\" is the program that added each entry. Entries that appear later (new programs) go at the end.", scopes, start, Load, Defaults, hotkeys: false, separators: true)
        { Owner = Window.GetWindow(owner) };
        if (win.ShowDialog() != true || win.Scope == null) return;
        var rows = win.Rows;
        bool isDefault = rows.All(r => r.Shown) && rows.Select(r => r.Id).SequenceEqual(full);
        if (isDefault) s.Layouts.Remove(win.Scope);
        else s.Layouts[win.Scope] = new MenuLayout { Order = rows.Select(r => r.Id).ToList(), Hidden = rows.Where(r => !r.Shown).Select(r => r.Id).ToList() };
        AppSettings.Current.Save();
    }

    /// <summary>The item's drive answers within a quarter second (a sleeping hard drive takes seconds to spin up).</summary>
    private static bool Responds(string path) => Task.Run(() => File.Exists(path) || Directory.Exists(path)).Wait(250);

    private static bool IsDuplicate(ShellMenuEntry e, List<string> hidden)
    {
        if (e.IsSeparator) return false;
        if (e.Verb != null && DedupeVerbs.Contains(e.Verb)) return true;
        string label = e.Label.Replace("&", "");
        if (DedupeLabels.Contains(label)) return true;
        foreach (var h in hidden) if (!string.IsNullOrWhiteSpace(h) && label.Contains(h, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static object ToMenuItem(ShellContextMenu shell, ShellMenuEntry e)
    {
        if (e.IsSeparator) return new Separator();
        var mi = new MenuItem { Header = e.Label.Replace("__", "&&").Replace("&&", "").Replace("&", "_").Replace("", "&"), IsEnabled = e.IsEnabled };
        mi.Tag = e;   // the editor's "From" column looks up its program
        if (e.IsDefault) mi.FontWeight = FontWeights.SemiBold;
        if (e.Icon != null) mi.Icon = new System.Windows.Controls.Image { Source = e.Icon, Width = 16, Height = 16 };
        if (e.HasSubMenu)
        {
            mi.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
            bool loaded = false;
            mi.SubmenuOpened += (_, _) =>
            {
                if (loaded) return;
                loaded = true;
                mi.Items.Clear();
                foreach (var sub in shell.SubEntries(e)) mi.Items.Add(ToMenuItem(shell, sub));
                if (mi.Items.Count == 0) mi.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
            };
        }
        else mi.Click += (_, _) => shell.Invoke(e);
        return mi;
    }

    private static bool Applies(CustomMenuItem ci, IReadOnlyList<FileItem> items, bool background)
    {
        if (background) return ci.ForBackground;
        if (items.Any(i => i.IsVirtual)) return false;
        bool ok = items.All(i => i.IsDirectory ? ci.ForFolders : ci.ForFiles);
        if (!ok) return false;
        if (string.IsNullOrWhiteSpace(ci.Extensions)) return true;
        var exts = new HashSet<string>(ci.Extensions.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        return items.All(i => i.IsDirectory || exts.Contains(i.Extension));
    }
}
