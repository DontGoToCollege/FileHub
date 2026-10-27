using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FileHub.Core.Search;
using FileHub.Core.Settings;
using FileHub.Platform;

namespace FileHub.Core.Shell;

/// <summary>Launching, "open with", properties, terminals, and other shell verbs.</summary>
public static class ShellUtil
{
    /// <summary>Opens a file with FileHub's own association override if present, otherwise the Windows default.</summary>
    public static void Open(string path, IntPtr hwnd = default)
    {
        try
        {
            if (File.Exists(path))
            {
                string ext = FileTypes.ExtensionOf(path);
                if (ext.Length > 0 && AppSettings.Current.Associations.TryGetValue(ext, out var program) && !string.IsNullOrWhiteSpace(program))
                {
                    Process.Start(new ProcessStartInfo(program, "\"" + path + "\"") { UseShellExecute = true });
                    return;
                }
            }
            Execute(path, "open", null, hwnd);
        }
        catch (Exception ex) { Log.Warn("Open failed for " + path + ": " + ex.Message); }
    }

    public static void OpenWith(string path, IntPtr hwnd = default) => Execute(path, "openas", null, hwnd, Native.SEE_MASK_INVOKEIDLIST);

    public static void ShowProperties(string path, IntPtr hwnd = default) => Execute(path, "properties", null, hwnd, Native.SEE_MASK_INVOKEIDLIST);

    public static void RunAsAdmin(string path, IntPtr hwnd = default) => Execute(path, "runas", null, hwnd);

    public static bool Execute(string file, string? verb, string? args, IntPtr hwnd = default, uint extraMask = 0)
    {
        var info = new Native.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
            fMask = Native.SEE_MASK_NOASYNC | extraMask,
            hwnd = hwnd,
            lpVerb = verb,
            lpFile = file,
            lpParameters = args,
            lpDirectory = Directory.Exists(file) ? file : Path.GetDirectoryName(file),
            nShow = Native.SW_SHOWNORMAL,
        };
        bool ok = Native.ShellExecuteExW(ref info);
        if (!ok) Log.Warn($"ShellExecuteEx({verb}) failed for {file}: {Marshal.GetLastWin32Error()}");
        return ok;
    }

    public static void OpenTerminal(string folder)
    {
        string cmd = AppSettings.Current.Explorer.TerminalCommand;
        if (string.IsNullOrWhiteSpace(cmd)) cmd = "wt.exe -d \"%1\"";
        var (exe, args) = SplitCommand(cmd.Replace("%1", folder));
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = folder }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = folder }); }
            catch (Exception ex) { Log.Warn("OpenTerminal failed: " + ex.Message); }
        }
    }

    public static void OpenInWindowsExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Explorer launch failed: " + ex.Message); }
    }

    /// <summary>Runs a user-defined custom menu command with %1 / %D / %N / %* placeholders expanded.</summary>
    public static void RunCustom(CustomMenuItem item, IReadOnlyList<string> selection, string? folder)
    {
        string first = selection.Count > 0 ? selection[0] : folder ?? string.Empty;
        string dir = selection.Count > 0 ? (Directory.Exists(first) ? first : Path.GetDirectoryName(first) ?? string.Empty) : folder ?? string.Empty;
        string all = string.Join(" ", selection.Select(s => "\"" + s + "\""));
        string Expand(string s) => s.Replace("%*", all).Replace("%1", first).Replace("%D", dir).Replace("%d", dir).Replace("%N", Path.GetFileName(first)).Replace("%n", Path.GetFileName(first));
        string exe = Environment.ExpandEnvironmentVariables(Expand(item.Command));
        string args = Environment.ExpandEnvironmentVariables(Expand(item.Arguments));
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Directory.Exists(dir) ? dir : null };
            if (item.RunAsAdmin) psi.Verb = "runas";
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Warn($"Custom command '{item.Label}' failed: {ex.Message}"); }
    }

    public static (string exe, string args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        int sp = command.IndexOf(' ');
        return sp < 0 ? (command, string.Empty) : (command[..sp], command[(sp + 1)..].Trim());
    }

    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable) key.SetValue("FileHub", "\"" + Environment.ProcessPath + "\" --tray");
            else key.DeleteValue("FileHub", false);
        }
        catch (Exception ex) { Log.Warn("Startup registration failed: " + ex.Message); }
    }

    public static bool IsDarkSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Shutdown(string what)
    {
        try
        {
            switch (what)
            {
                case "lock": Native.LockWorkStation(); break;
                case "sleep": Native.SetSuspendState(false, false, false); break;
                case "hibernate": Native.SetSuspendState(true, false, false); break;
                case "restart": Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }); break;
                case "shutdown": Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true }); break;
                case "signout": Native.ExitWindowsEx(0, 0); break;
            }
        }
        catch (Exception ex) { Log.Warn("Power action failed: " + ex.Message); }
    }
}
