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
    public static AppSettings Settings { get; private set; } = null!;
    public static IndexService Index { get; private set; } = null!;
    public static MetaDb Db { get; private set; } = null!;
    public static SearchEngine Search { get; private set; } = null!;
    public static ShellImaging Imaging { get; private set; } = null!;
    public static ImageLoader Images { get; private set; } = null!;
    public static ContentIndexer Content { get; private set; } = null!;
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

        _single = new SingleInstance();
        if (!_single.TryAcquire())
        {
            SingleInstance.SendToRunningInstance(args);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
            try { MessageBox.Show(ex.Exception.Message, "FileHub - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        Log.Info($"FileHub starting ({string.Join(" ", args)})");
        Settings = AppSettings.Load();
        ThemeManager.Apply(Settings.General.Theme);

        Db = new MetaDb(Paths.MetaDbFile);
        Imaging = new ShellImaging(Db) { DiskCacheEnabled = Settings.Index.ThumbnailCaching };
        Images = new ImageLoader(Imaging);
        Index = new IndexService(Settings);
        Search = new SearchEngine(Index, Db);
        Content = new ContentIndexer(Index, Db, Settings);
        Index.Start();
        Content.Start();

        _hook = new KeyboardHook(Dispatcher);
        _hook.StartMenuRequested += ToggleStartMenu;
        _hook.ExplorerRequested += () => ShowExplorer();
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
                case "--show": ShowExplorer(); any = true; break;
                case "--rebuild": _ = Index.RebuildAsync(); any = true; break;
                case "--exit": ExitApp(); return;
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
            win.Show();
        }
        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
        win.Show(); win.Activate();
        if (selectPath != null && tab != null) tab.RequestSelect(selectPath);
    }

    public static void ShowHub(string? query = null, string? page = null)
    {
        var app = Instance;
        if (app._hub == null || !app._hub.IsLoaded)
        {
            app._hub = new HubWindow();
            app._hub.Closed += (_, _) => { app._hub = null; app.MaybeExit(); };
            app._hub.Show();
        }
        if (app._hub.WindowState == WindowState.Minimized) app._hub.WindowState = WindowState.Normal;
        app._hub.Show(); app._hub.Activate();
        app._hub.Navigate(page ?? "search", query);
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
        if (app._hook != null)
        {
            app._hook.ReplaceStartMenu = s.StartMenu.ReplaceWinKey;
            app._hook.ReplaceWinE = s.StartMenu.ReplaceWinE;
            if (s.StartMenu.ReplaceWinKey || s.StartMenu.ReplaceWinE) app._hook.Install(); else app._hook.Uninstall();
        }
        if (app._hotkeys != null)
        {
            app._hotkeys.Unregister(app._hubHotkeyId);
            HubHotkeyRegistered = !string.IsNullOrWhiteSpace(s.General.HubHotkey) && app._hotkeys.Register(s.General.HubHotkey, () => ShowHub(), out app._hubHotkeyId);
            if (!HubHotkeyRegistered && !string.IsNullOrWhiteSpace(s.General.HubHotkey)) Log.Warn("Could not register hub hotkey " + s.General.HubHotkey);
        }
        if (app._tray != null) app._tray.Visible = s.General.ShowTrayIcon;
        ShellUtil.SetStartWithWindows(s.General.StartWithWindows);
        Imaging.DiskCacheEnabled = s.Index.ThumbnailCaching;
        app._start?.BuildPinned();
    }

    public static void ExitApp()
    {
        var app = Instance;
        if (app._exiting) return;
        app._exiting = true;
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
            _hook?.Dispose();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            Content?.Dispose();
            Images?.Dispose();
            Index?.Dispose();
            Db?.Dispose();
            _single?.Dispose();
        }
        catch (Exception ex) { Log.Error("Shutdown error", ex); }
        Log.Info("FileHub exited");
        base.OnExit(e);
    }
}
