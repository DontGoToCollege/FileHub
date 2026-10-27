using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FileHub.Core.Search;
using FileHub.Core.Shell;
using FileHub.UI.Common;

namespace FileHub.UI.Controls;

public partial class PreviewPane : UserControl
{
    private FileItem? _current;
    private int _version;

    public PreviewPane() => InitializeComponent();

    public void Show(FileItem? item)
    {
        _current = item;
        int v = ++_version;
        if (item == null || item.IsVirtual)
        {
            Placeholder.Visibility = Visibility.Visible; Body.Visibility = Visibility.Collapsed;
            return;
        }
        Placeholder.Visibility = Visibility.Collapsed; Body.Visibility = Visibility.Visible;
        NameText.Text = item.Name;
        TypeText.Text = item.IsDirectory ? "File folder" : $"{item.TypeName} · {item.SizeText}";
        TextPreview.Visibility = Visibility.Collapsed;
        Thumb.Source = null;

        var props = new List<KeyValuePair<string, string>>
        {
            new("Location", item.Location),
            new("Modified", item.ModifiedText),
            new("Created", item.CreatedText),
        };
        if (!item.IsDirectory) props.Add(new("Size", $"{item.SizeText} ({item.Size:N0} bytes)"));
        if (item.AttributesText.Length > 0) props.Add(new("Attributes", item.AttributesText));
        Props.ItemsSource = props;

        string path = item.FullPath; bool dir = item.IsDirectory; long mod = item.ModifiedUtc.Ticks; string ext = item.Extension;
        Task.Run(() =>
        {
            var img = App.Imaging.GetImage(path, dir, 256, true, mod);
            Dictionary<string, string>? extra = null;
            string? text = null;
            if (!dir)
            {
                try
                {
                    var json = App.Db.GetPropsJson(path);
                    extra = json != null ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) : PropertyReader.Read(path);
                }
                catch { }
                if (FileTypes.IsIn("code", ext) || FileTypes.IsIn("document", ext) && ext is "txt" or "md" or "csv" or "log")
                {
                    try
                    {
                        if (new FileInfo(path).Length < 512 * 1024)
                        {
                            var raw = File.ReadAllText(path);
                            text = raw.Length > 4000 ? raw[..4000] + "…" : raw;
                            if (text.Contains('\0')) text = null;
                        }
                    }
                    catch { }
                }
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (v != _version) return;
                Thumb.Source = img;
                if (extra != null && extra.Count > 0)
                {
                    var merged = props.ToList();
                    foreach (var kv in extra) if (kv.Key is not ("Width" or "Height" or "FrameWidth" or "FrameHeight")) merged.Add(new(kv.Key, kv.Value));
                    Props.ItemsSource = merged;
                }
                if (text != null) { TextPreview.Text = text; TextPreview.Visibility = Visibility.Visible; }
            });
        });
    }
}
