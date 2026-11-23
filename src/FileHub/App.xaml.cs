using System.IO;
using System.Windows;
using System.Windows.Threading;
using FileHub.Core.Database;
using FileHub.Core.Indexing;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.Platform;
using FileHub.UI;
using FileHub.UI.Common;
using FileHub.UI.Explorer;
using FileHub.UI.Hub;
using FileHub.UI.Start;

namespace FileHub;

public partial class App : Application
{
    public static readonly string Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
    /// <summary>End of every window title: "Home - FileHub - ver. 0.3.33".</summary>
    public static readonly string TitleSuffix = " - FileHub - ver. " + Version;
    public static AppSettings Settings { get; private set; } = null!;
    public static IndexService Index { get; private set; } = null!;
    public static MetaDb Db { get; private set; } = null!;
    public static SearchEngine Search { get; private set; } = null!;
    public static ShellImaging Imaging { get; private set; } = null!;
    public static ImageLoader Images { get; private set; } = null!;
    public static ContentIndexer Content { get; private set; } = null!;
    public static ThumbDb Thumbs { get; private set; } = null!;
    public static ThumbIndexer ThumbPass { get; private set; } = null!;
    public static bool HubHotkeyRegistered { get; private set; }

    private static App Instance => (App)Current;
    private SingleInstance? _single;
    private KeyboardHook? _hook;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private int _hubHotkeyId;
    private readonly List<ExplorerWindow> _explorers = new();
    private HubWindow? _hub;
    private StartMenuWindow? _start;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        TestMode = args.Contains("--test", StringComparer.OrdinalIgnoreCase);

        _single = new SingleInstance();
        // --restart: the old instance is still shutting down (saving the index), so wait for it instead of forwarding
        bool restart = args.Contains("--restart", StringComparer.OrdinalIgnoreCase);
        if (!_single.TryAcquire(restart ? 60_000 : 0))
        {
            SingleInstance.SendToRunningInstance(args);
            Shutdown();
            return;
        }
        Paths.PrepareDataDir();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
            try { MessageBox.Show(ex.Exception.Message, "FileHub - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        Log.Info($"FileHub {Version} starting ({string.Join(" ", args)})");
        UiWatchdog.Start(Dispatcher);
        Settings = AppSettings.Load();
        ThemeManager.Apply(Settings.General.Theme);

        Db = new MetaDb(Paths.MetaDbFile);
        Thumbs = new ThumbDb(Paths.ThumbDbFile);
        Imaging = new ShellImaging(Thumbs) { DiskCacheEnabled = Settings.Index.ThumbnailCaching };
        Images = new ImageLoader(Imaging);
        Index = new IndexService(Settings);
        Index.MoveRows = (from, to, progress) => { long m = Db.MovePrefix(from, to, progress); Thumbs.MovePrefix(from, to, n => progress(m + n)); };
        Search = new SearchEngine(Index, Db);
        Content = new ContentIndexer(Index, Db, Settings);
        Index.Start();
        Content.Start();
        ThumbPass = new ThumbIndexer(Index, Thumbs, Settings, Content);
        ThumbPass.Start();

        _hook = new KeyboardHook(Dispatcher);
        _hook.StartMenuRequested += ToggleStartMenu;
        _hook.ExplorerRequested += () => ShowExplorer();
        _hook.HubRequested += () => ShowHub();
        _hotkeys = new HotkeyManager();
        _tray = new TrayIcon();
        ApplySettings();

        _single.ArgsReceived += a => Dispatcher.BeginInvoke(() => HandleArgs(a, false));
        _single.StartServer();
        HandleArgs(args, true);
    }

    private void HandleArgs(string[] args, bool initial)
    {
        bool any = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--tray": case "--background": any = true; break;
                case "--hub": ShowHub(i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null); any = true; break;
                case "--start": ToggleStartMenu(); any = true; break;
                case "--page" when i + 1 < args.Length: ShowHub(null, args[++i]); any = true; break;   // e.g. --page startmenu
                case "--show": ShowExplorer(); any = true; break;
                case "--rebuild": _ = Index.RebuildAsync(); any = true; break;
                case "--exit": ExitApp(); return;
                case "--move-data" when i + 1 < args.Length: MoveDataTo(args[++i], interactive: false); return;
                case "--set-text" when TestMode && i + 2 < args.Length:
                    // UI Automation's SetValue focuses the box (and so the window); this fills it without any focus change
                    SetTestText(args[i + 1], args[i + 2]); i += 2; any = true; break;
                default:
                    if (a.StartsWith("--")) break;
                    any = true;
                    if (Directory.Exists(a) || a.StartsWith("::")) ShowExplorer(a);
                    else if (File.Exists(a)) ShowExplorer(Path.GetDirectoryName(a), selectPath: a);
                    else ShowHub(string.Join(" ", args[i..]));   // anything else is a search query
                    if (!Directory.Exists(a) && !File.Exists(a) && !a.StartsWith("::")) i = args.Length;
                    break;
            }
        }
        if (!any) ShowExplorer();
    }

    // ------------------------------------------------------------------ windows

    /// <summary>
    /// --test (first launch only): windows open off-screen, never activate and are never topmost, and the start menu
    /// stays open without focus, so Claude can drive FileHub with UI Automation and capture it with PrintWindow
    /// while the owner keeps using the PC. See the project CLAUDE.md for the recipe.
    /// </summary>
    public static bool TestMode { get; private set; }

    /// <summary>Shows a window in front with focus, or in --test mode off-screen without focus.</summary>
    public static void Present(Window w)
    {
        if (TestMode)
        {
            w.ShowActivated = false; w.Topmost = false;
            w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -20000; w.Top = 0;
            MakeNoActivate(w);
            w.Show();
            return;
        }
        w.Show(); w.Activate();
    }

    /// <summary>--test: sets the text of a visible TextBox by x:Name, newest window first; "TitlePart:Name" picks the window.</summary>
    private static void SetTestText(string name, string text)
    {
        string? title = null;
        if (name.IndexOf(':') is int c and > 0) { title = name[..c]; name = name[(c + 1)..]; }
        foreach (var w in Current.Windows.Cast<Window>().Reverse())
            if (w.IsVisible && (title == null || w.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                && FindVisible<System.Windows.Controls.TextBox>(w, name) is { } box) { box.Text = text; return; }
        Log.Warn("--set-text: no visible text box named " + name);
    }

    private static T? FindVisible<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t && t.Name == name && t.IsVisible) return t;
            if (FindVisible<T>(c, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>--test: the window can never become active, so FileHub's own focus calls can't pull it in front of the owner's work.</summary>
    public static void MakeNoActivate(Window w)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_NOACTIVATE) == 0) Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
    }

    public static void ShowExplorer(string? path = null, bool newWindow = false, string? selectPath = null)
    {
        var app = Instance;
        app.Dispatcher.VerifyAccess();
        ExplorerWindow win;
        ExplorerTab? tab = null;
        var existing = app._explorers.LastOrDefault(w => w.IsLoaded);
        if (existing != null && !newWindow)
        {
            win = existing;
            if (path != null) tab = win.OpenTab(path).Tab;
        }
        else
        {
            win = new ExplorerWindow(path);
            tab = win.Tabs.FirstOrDefault()?.Tab;
            app._explorers.Add(win);
            win.Closed += (_, _) => { app._explorers.Remove(win); app.MaybeExit(); };
            Present(win);
        }
        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
        Present(win);
        if (selectPath != null && tab != null) tab.RequestSelect(selectPath);
    }

    public static void ShowHub(string? query = null, string? page = null)
    {
        var app = Instance;
        if (app._hub == null || !app._hub.IsLoaded)
        {
            app._hub = new HubWindow();
            app._hub.Closed += (_, _) => { app._hub = null; app.MaybeExit(); };
            Present(app._hub);
        }
        if (app._hub.WindowState == WindowState.Minimized) app._hub.WindowState = WindowState.Normal;
        Present(app._hub);
        app._hub.Navigate(page ?? "search", query);
    }

    // ------------------------------------------------------------------ start menu pins (shared by every context menu)

    public static bool IsPinnedToStart(string path) => Settings.StartMenu.Pinned.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    public static void PinToStart(string path)
    {
        if (IsPinnedToStart(path)) return;
        string label = Directory.Exists(path) ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(path);
        Settings.StartMenu.Pinned.Add(new PinnedItem { Label = label.Length > 0 ? label : path, Path = path });
        Settings.Save();
        Instance._start?.BuildPinned();
    }

    public static void UnpinFromStart(string path)
    {
        if (Settings.StartMenu.Pinned.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Settings.Save();
        Instance._start?.BuildPinned();
    }

    public static void ToggleStartMenu()
    {
        var app = Instance;
        app._start ??= new StartMenuWindow();
        app._start.Toggle();
    }

    private void MaybeExit()
    {
        if (_exiting) return;
        if (!Settings.General.KeepRunningInBackground && _explorers.Count == 0 && _hub == null) ExitApp();
    }

    // ------------------------------------------------------------------ settings side effects

    public static void ApplySettings()
    {
        var app = Instance;
        var s = Settings;
        ThemeManager.Apply(s.General.Theme);
        // The Hub hotkey is captured by the low-level hook so it wins over apps that registered the
        // same combination (e.g. Claude desktop owns Ctrl+Alt+Space). RegisterHotKey is only a fallback.
        bool hubParsed = HotkeyManager.TryParse(s.General.HubHotkey, out uint hubMods, out uint hubVk);
        bool hookActive = false;
        if (app._hook != null)
        {
            app._hook.ReplaceStartMenu = s.StartMenu.ReplaceWinKey;
            app._hook.ReplaceWinE = s.StartMenu.ReplaceWinE;
            app._hook.SetHubHotkey(hubParsed ? hubMods : 0, hubParsed ? hubVk : 0);
            if (s.StartMenu.ReplaceWinKey || s.StartMenu.ReplaceWinE || hubParsed) app._hook.Install(); else app._hook.Uninstall();
            hookActive = app._hook.IsInstalled;
        }
        if (app._hotkeys != null)
        {
            app._hotkeys.Unregister(app._hubHotkeyId);
            app._hubHotkeyId = 0;
            if (hubParsed && hookActive) HubHotkeyRegistered = true;
            else
            {
                if (app._hook != null && !hookActive) app._hook.SetHubHotkey(0, 0);
                HubHotkeyRegistered = hubParsed && app._hotkeys.Register(s.General.HubHotkey, () => ShowHub(), out app._hubHotkeyId);
                if (!HubHotkeyRegistered && !string.IsNullOrWhiteSpace(s.General.HubHotkey)) Log.Warn("Could not register hub hotkey " + s.General.HubHotkey);
            }
        }
        if (app._tray != null) app._tray.Visible = s.General.ShowTrayIcon;
        ShellUtil.SetStartWithWindows(s.General.StartWithWindows);
        Imaging.DiskCacheEnabled = s.Index.ThumbnailCaching;
        app._start?.BuildPinned();
    }

    // ------------------------------------------------------------------ indexing controls (shared by Explorer and Hub toggles)

    public static bool IndexingPaused => Index.Paused;
    public static bool RealTimeIndexing => Settings.Index.RealTimeIndexing;
    public static event Action? IndexControlChanged;

    /// <summary>Pauses/resumes all background indexing: crawl, live file changes (queued meanwhile) and content/metadata extraction.</summary>
    public static void SetIndexingPaused(bool paused)
    {
        if (Index.Paused == paused) return;
        Index.SetPaused(paused);
        Content.Paused = paused;
        ThumbPass.Paused = paused;
        IndexControlChanged?.Invoke();
    }

    /// <summary>Turns real-time (watcher-based) indexing of new/changed/deleted files on or off; persisted.</summary>
    public static void SetRealTimeIndexing(bool on)
    {
        if (Settings.Index.RealTimeIndexing == on) return;
        Index.SetRealTime(on);
        Settings.Save();
        IndexControlChanged?.Invoke();
    }

    /// <summary>Schedules the data folder move, then restarts: the move itself runs at startup, before anything opens the data.</summary>
    public static void MoveDataTo(string folder, bool interactive)
    {
        string? error = Paths.ScheduleMove(folder);
        if (error != null)
        {
            Log.Warn("Move data folder: " + error);
            if (interactive) MessageBox.Show(error, "Move data folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Log.Info("Restarting to move the data folder to " + folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--tray --restart" + (TestMode ? " --test" : "")) { UseShellExecute = false });
        ExitApp();
    }

    public static void ExitApp()
    {
        var app = Instance;
        if (app._exiting) return;
        app._exiting = true;
        // Exit always ends the process: if something holds it up (a long database write), stop anyway after 15 s.
        // Safe: meta.db and thumbs.db use a write-ahead log, so an unfinished write is simply rolled back
        new Thread(() => { Thread.Sleep(15000); Log.Warn("Exit took over 15 s; ending the process"); System.Diagnostics.Process.GetCurrentProcess().Kill(); }) { IsBackground = true }.Start();
        try { Settings.Save(); } catch { }
        try { app._start?.Close(); } catch { }
        foreach (var w in app._explorers.ToList()) { try { w.Close(); } catch { } }
        try { app._hub?.Close(); } catch { }
        app.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Index?.SaveIfChanged();
            _hook?.Dispose();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            Content?.Dispose();
            ThumbPass?.Dispose();
            Images?.Dispose();
            Index?.Dispose();
            Db?.Dispose();
            Thumbs?.Dispose();
            _single?.Dispose();
        }
        catch (Exception ex) { Log.Error("Shutdown error", ex); }
        Log.Info("FileHub exited");
        base.OnExit(e);
    }
}
