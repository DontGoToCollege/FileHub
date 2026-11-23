using System.Windows;
using System.Windows.Controls;
using FileHub.Core.Settings;
using FileHub.Core.Shell;
using FileHub.Platform;

namespace FileHub.UI.Hub;

public partial class GeneralPage : UserControl, ISavablePage
{
    public GeneralPage()
    {
        InitializeComponent();
        var g = AppSettings.Current.General;
        foreach (ComboBoxItem ci in ThemeCombo.Items) if ((string)ci.Tag == g.Theme.ToString()) ThemeCombo.SelectedItem = ci;
        StartWithWindows.IsChecked = g.StartWithWindows; KeepRunning.IsChecked = g.KeepRunningInBackground; TrayIcon.IsChecked = g.ShowTrayIcon;
        HotkeyBox.Text = g.HubHotkey;
        MaxResultsBox.Text = AppSettings.Current.Search.MaxResults.ToString();
        VersionText.Text = $"FileHub {typeof(App).Assembly.GetName().Version} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")}";
        DataText.Text = "Data: " + Paths.DataDir;
        HotkeyStatus.Text = App.HubHotkeyRegistered ? "active (takes priority over other apps)" : "not active (check the key combination)";
    }

    public void SavePage()
    {
        var g = AppSettings.Current.General;
        if (ThemeCombo.SelectedItem is ComboBoxItem ci && Enum.TryParse<ThemeMode>((string)ci.Tag, out var t)) g.Theme = t;
        g.StartWithWindows = StartWithWindows.IsChecked == true; g.KeepRunningInBackground = KeepRunning.IsChecked == true; g.ShowTrayIcon = TrayIcon.IsChecked == true;
        g.HubHotkey = HotkeyBox.Text.Trim();
        if (int.TryParse(MaxResultsBox.Text, out int n)) AppSettings.Current.Search.MaxResults = Math.Clamp(n, 100, 1_000_000);
        AppSettings.Current.Save();
        App.ApplySettings();
        HotkeyStatus.Text = App.HubHotkeyRegistered ? "active (takes priority over other apps)" : "not active (check the key combination)";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => ShellUtil.Open(Paths.DataDir);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => ShellUtil.Open(Log.Directory);
    private void MoveData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where FileHub keeps its data and logs (a FileHub-Data folder is made inside it)" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (MessageBox.Show($"FileHub will close, move its data and logs into a FileHub-Data folder in\n{dlg.FolderName}\nand start again.\n\nMoving to another drive copies everything (several GB) and can take a few minutes, during which FileHub isn't running.",
                "Move data folder", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SavePage();
        App.MoveDataTo(dlg.FolderName, interactive: true);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) { SavePage(); App.ExitApp(); }
}
