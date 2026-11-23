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

    /// <summary>Every sort the Sort by menus can offer, in menu order; which ones show is the owner's choice (Sort options).</summary>
    public static readonly (string Label, string Key)[] SortKeys =
        { ("Name", "Name"), ("Date", "Date"), ("Date modified", "Modified"), ("Date created", "Created"), ("Type", "Type"), ("Size", "Size"), ("Extension", "Extension") };

    /// <summary>The Sort by entries to show for this list of chosen keys (unknown keys ignored, menu order kept).</summary>
    public static IEnumerable<(string Label, string Key)> ShownSortKeys(IReadOnlyCollection<string> choices)
        => SortKeys.Where(k => choices.Contains(k.Key, StringComparer.OrdinalIgnoreCase));

    /// <summary>The Explorer view hotkeys (Ctrl+Shift+1…6), in View menu order.</summary>
    public static readonly (string Label, ViewMode Mode, string Gesture)[] ViewModes =
    {
        ("Extra large icons", ViewMode.ExtraLargeIcons, "Ctrl+Shift+1"), ("Large icons", ViewMode.LargeIcons, "Ctrl+Shift+2"),
        ("Medium icons", ViewMode.MediumIcons, "Ctrl+Shift+3"), ("Small icons", ViewMode.SmallIcons, "Ctrl+Shift+4"),
        ("List", ViewMode.List, "Ctrl+Shift+5"), ("Details", ViewMode.Details, "Ctrl+Shift+6"),
    };

    /// <summary>"Sort options" submenu: which sorts Sort by shows, and whether sort changes stick to this folder or apply to all folders.</summary>
    public static MenuItem SortOptionsMenu(Action<string> command, IReadOnlyCollection<string> choices)
    {
        bool all = AppSettings.Current.Explorer.SortAllFolders;
        var opts = new MenuItem { Header = "Sort options" };
        var shown = new MenuItem { Header = all ? "Show in Sort by (all folders)" : "Show in Sort by (this folder)" };
        foreach (var (label, key) in SortKeys)
        {
            // stays open so several can be ticked in one go
            var mi = new MenuItem { Header = label, IsCheckable = true, StaysOpenOnClick = true, IsChecked = choices.Contains(key, StringComparer.OrdinalIgnoreCase) };
            var k = key; mi.Click += (_, _) => command("sortchoice:" + k);
            shown.Items.Add(mi);
        }
        opts.Items.Add(shown);
        opts.Items.Add(new Separator());
        var folder = new MenuItem { Header = "Remember sort for each folder", IsChecked = !all }; folder.Click += (_, _) => command("sortscope:folder"); opts.Items.Add(folder);
        var every = new MenuItem { Header = "Use the same sort for all folders", IsChecked = all }; every.Click += (_, _) => command("sortscope:all"); opts.Items.Add(every);
        opts.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset this folder to the default sort", IsEnabled = !all }; reset.Click += (_, _) => command("sortreset"); opts.Items.Add(reset);
        return opts;
    }

    public static ContextMenu Build(FrameworkElement owner, IReadOnlyList<FileItem> items, string? folder, Action<string> command, IReadOnlyCollection<string>? sortChoices = null)
    {
        var s = AppSettings.Current.ContextMenu;
        var menu = new ContextMenu();
        bool background = items.Count == 0;
        bool single = items.Count == 1;
        bool allDirs = items.Count > 0 && items.All(i => i.IsDirectory);
        bool anyVirtual = items.Any(i => i.IsVirtual);
        var paths = items.Where(i => !i.IsVirtual).Select(i => i.FullPath).ToList();
        string? realFolder = folder != null && !folder.StartsWith("::") ? folder : null;

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
            var sort = new MenuItem { Header = "Sort by" };
            var choices = sortChoices ?? AppSettings.Current.Explorer.SortChoices;
            foreach (var (label, key) in ShownSortKeys(choices))
            { var mi = new MenuItem { Header = label }; mi.Click += (_, _) => command("sort:" + key); sort.Items.Add(mi); }
            sort.Items.Add(new Separator());
            foreach (var (l, c) in new[] { ("Ascending", "sortdir:asc"), ("Descending", "sortdir:desc") }) { var mi = new MenuItem { Header = l }; mi.Click += (_, _) => command(c); sort.Items.Add(mi); }
            sort.Items.Add(new Separator());
            sort.Items.Add(SortOptionsMenu(command, choices));
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
                var mi = new MenuItem { Header = ci.Label };
                var captured = ci;
                mi.Click += (_, _) => ShellUtil.RunCustom(captured, paths, realFolder);
                menu.Items.Add(mi);
            }
        }

        // Windows shell items
        if (s.ShellMode != ShellMenuMode.Hidden && !anyVirtual && (paths.Count > 0 || realFolder != null))
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
                        var more = new MenuItem { Header = "Show more options" };
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
        return menu;
    }

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
        if (e.IsDefault) mi.FontWeight = FontWeights.SemiBold;
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
