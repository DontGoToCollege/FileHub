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

    public static ContextMenu Build(FrameworkElement owner, IReadOnlyList<FileItem> items, string? folder, Action<string> command)
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
            foreach (var (label, mode) in new[] { ("Extra large icons", "ExtraLargeIcons"), ("Large icons", "LargeIcons"), ("Medium icons", "MediumIcons"), ("Small icons", "SmallIcons"), ("List", "List"), ("Details", "Details") })
            { var mi = new MenuItem { Header = label }; mi.Click += (_, _) => command("view:" + mode); view.Items.Add(mi); }
            menu.Items.Add(view);
            var sort = new MenuItem { Header = "Sort by" };
            foreach (var key in new[] { "Name", "Modified", "Type", "Size", "Created" })
            { var mi = new MenuItem { Header = key == "Modified" ? "Date modified" : key == "Created" ? "Date created" : key }; mi.Click += (_, _) => command("sort:" + key); sort.Items.Add(mi); }
            sort.Items.Add(new Separator());
            foreach (var (l, c) in new[] { ("Ascending", "sortdir:asc"), ("Descending", "sortdir:desc") }) { var mi = new MenuItem { Header = l }; mi.Click += (_, _) => command(c); sort.Items.Add(mi); }
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
