namespace FileHub.UI.Common;

/// <summary>
/// The one place indexing status is put together, so every screen that shows it (Explorer's status line, the Hub's
/// side panel, Hub → Index &amp; database) reports the same passes in the same words. The Start menu shows none.
/// A new background pass is added here: its line in <see cref="Passes"/> and its StatusChanged in Subscribe.
/// </summary>
public static class IndexingStatus
{
    /// <summary>Each background pass after the name index: its status text and whether it's working right now.</summary>
    private static IEnumerable<(string text, bool busy)> Passes()
    {
        string content = App.Content.StatusText;
        yield return (content, App.Content.IsRunning || content.StartsWith("Repairing") || content.StartsWith("Upgrading"));
        yield return (App.ThumbPass.StatusText, App.ThumbPass.IsRunning);
    }

    public static string IndexLine => "Index: " + App.Index.StatusText;

    /// <summary>Every pass, one per line, busy or not (the Hub's panels).</summary>
    public static string PassLines => string.Join("\n", Passes().Select(p => p.text));

    /// <summary>The index, then only the passes at work, on one line (Explorer's status line).</summary>
    public static string Compact => IndexLine + string.Concat(Passes().Where(p => p.busy).Select(p => " · " + p.text));

    public static void Subscribe(Action onChange)
    {
        App.Index.StatusChanged += onChange; App.Content.StatusChanged += onChange; App.ThumbPass.StatusChanged += onChange;
    }

    public static void Unsubscribe(Action onChange)
    {
        App.Index.StatusChanged -= onChange; App.Content.StatusChanged -= onChange; App.ThumbPass.StatusChanged -= onChange;
    }
}
