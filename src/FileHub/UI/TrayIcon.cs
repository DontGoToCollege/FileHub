using System.Drawing;
using System.Windows.Forms;
using FileHub.Platform;

namespace FileHub.UI;

/// <summary>Notification-area icon (WinForms NotifyIcon) so FileHub keeps living in the background.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon()
    {
        _icon = new NotifyIcon { Text = "FileHub", Visible = false };
        try { _icon.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch (Exception ex) { Log.Warn("Tray icon load failed: " + ex.Message); _icon.Icon = SystemIcons.Application; }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Explorer", null, (_, _) => App.ShowExplorer());
        menu.Items.Add("Open File Hub", null, (_, _) => App.ShowHub());
        menu.Items.Add("Start menu", null, (_, _) => App.ToggleStartMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Rebuild index", null, (_, _) => _ = App.Index.RebuildAsync());
        menu.Items.Add("Settings", null, (_, _) => App.ShowHub(null, "general"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => App.ExitApp());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => App.ShowExplorer();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) App.ShowExplorer(); };
    }

    public bool Visible { get => _icon.Visible; set => _icon.Visible = value; }

    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info); } catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
