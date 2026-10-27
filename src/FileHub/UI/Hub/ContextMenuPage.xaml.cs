using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FileHub.Core.Settings;

namespace FileHub.UI.Hub;

public partial class ContextMenuPage : UserControl, ISavablePage
{
    private readonly ObservableCollection<CustomMenuItem> _items;

    public ContextMenuPage()
    {
        InitializeComponent();
        var s = AppSettings.Current.ContextMenu;
        foreach (ComboBoxItem ci in ShellMode.Items) if ((string)ci.Tag == s.ShellMode.ToString()) ShellMode.SelectedItem = ci;
        HiddenBox.Text = string.Join(Environment.NewLine, s.HiddenShellItems);
        BuiltIns.IsChecked = s.ShowBuiltIns; Terminal.IsChecked = s.ShowOpenTerminal; CopyPath.IsChecked = s.ShowCopyPath; OpenHub.IsChecked = s.ShowOpenInHub;
        _items = new ObservableCollection<CustomMenuItem>(s.Items);
        Grid.ItemsSource = _items;
    }

    public void SavePage()
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        var s = AppSettings.Current.ContextMenu;
        if (ShellMode.SelectedItem is ComboBoxItem ci && Enum.TryParse<ShellMenuMode>((string)ci.Tag, out var m)) s.ShellMode = m;
        s.HiddenShellItems = HiddenBox.Text.Split('\n').Select(x => x.Trim().TrimEnd('\r')).Where(x => x.Length > 0).ToList();
        s.ShowBuiltIns = BuiltIns.IsChecked == true; s.ShowOpenTerminal = Terminal.IsChecked == true; s.ShowCopyPath = CopyPath.IsChecked == true; s.ShowOpenInHub = OpenHub.IsChecked == true;
        s.Items = _items.ToList();
        AppSettings.Current.Save();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var item = new CustomMenuItem();
        _items.Add(item);
        Grid.SelectedItem = item; Grid.ScrollIntoView(item);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.ps1|All files|*.*", Title = "Choose program" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (Grid.SelectedItem is CustomMenuItem sel) sel.Command = dlg.FileName;
        else _items.Add(new CustomMenuItem { Label = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Command = dlg.FileName });
    }

    private void Remove_Click(object sender, RoutedEventArgs e) { if (Grid.SelectedItem is CustomMenuItem sel) _items.Remove(sel); }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is CustomMenuItem sel && _items.IndexOf(sel) is int i && i > 0) { _items.Move(i, i - 1); Grid.SelectedItem = sel; }
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is CustomMenuItem sel && _items.IndexOf(sel) is int i && i >= 0 && i < _items.Count - 1) { _items.Move(i, i + 1); Grid.SelectedItem = sel; }
    }
}
