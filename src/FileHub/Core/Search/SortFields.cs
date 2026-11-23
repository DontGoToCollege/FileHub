using System.Text.RegularExpressions;
using FileHub.Core.Shell;

namespace FileHub.Core.Search;

/// <summary>
/// Every sort the Explorer can offer: the file-system basics plus file details the metadata indexer stores
/// (key "prop:&lt;name&gt;"), with their default Sort by list and hotkeys.
/// </summary>
public static class SortFields
{
    public static readonly (string Key, string Label)[] Basics =
    {
        ("Name", "Name"), ("Date", "Date"), ("Modified", "Date modified"), ("Created", "Date created"),
        ("Type", "Type"), ("Size", "Size"), ("Extension", "Extension"), ("Location", "Location"),
    };

    /// <summary>Basics first, then file details (pixel sizes are left out: Dimensions covers them).</summary>
    public static readonly (string Key, string Label)[] All = Basics.Concat(
        PropertyReader.Keys.Select(k => k.Name).Where(n => n is not ("Width" or "Height" or "FrameWidth" or "FrameHeight"))
            .Select(n => ("prop:" + n, Friendly(n)))).ToArray();

    public static string Label(string key) => All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)).Label ?? key;
    public static bool IsKnown(string key) => All.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    public static List<string> DefaultChoices() => new() { "Name", "Date", "Modified", "Type", "Size", "Created" };

    /// <summary>Sort hotkeys; "Ascending"/"Descending" set the order. Picking the current sort again flips it.</summary>
    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        ["Name"] = "Ctrl+Alt+1", ["Date"] = "Ctrl+Alt+2", ["Modified"] = "Ctrl+Alt+3", ["Type"] = "Ctrl+Alt+4",
        ["Size"] = "Ctrl+Alt+5", ["Created"] = "Ctrl+Alt+6", ["Ascending"] = "Ctrl+Alt+PageUp", ["Descending"] = "Ctrl+Alt+PageDown",
    };

    // "DateTaken" -> "Date taken"
    private static string Friendly(string name) => Regex.Replace(name, "(?<=[a-z])([A-Z])", m => " " + m.Value.ToLowerInvariant());
}
