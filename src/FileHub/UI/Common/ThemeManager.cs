using System.Windows;
using System.Windows.Interop;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.Platform;

namespace FileHub.UI.Common;

public static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Apply(ThemeMode mode)
    {
        bool dark = mode switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => ShellUtil.IsDarkSystemTheme() };
        IsDark = dark;
        var app = Application.Current;
        if (app == null) return;
        var dicts = app.Resources.MergedDictionaries;
        var uri = new Uri(dark ? "UI/Themes/Colors.Dark.xaml" : "UI/Themes/Colors.Light.xaml", UriKind.Relative);
        var existing = dicts.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Colors."));
        var rd = new ResourceDictionary { Source = uri };
        if (existing != null) dicts[dicts.IndexOf(existing)] = rd; else dicts.Insert(0, rd);
        foreach (Window w in app.Windows) ApplyToWindow(w);
        Changed?.Invoke();
    }

    public static void ApplyToWindow(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) Native.SetDarkTitleBar(h, IsDark);
        else w.SourceInitialized += (_, _) => Native.SetDarkTitleBar(new WindowInteropHelper(w).Handle, IsDark);
    }
}
