using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FileHub.Core.Indexing;

namespace FileHub.Core.Search;

public enum SortKey { None, Name, Path, Size, Modified, Created, Extension, Type }

public sealed class SearchOptions
{
    public int MaxResults { get; set; } = 5000;
    public bool RegexMode { get; set; }
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool MatchPath { get; set; }
    /// <summary>Restrict results to this folder subtree (Explorer in-folder search).</summary>
    public string? ScopePath { get; set; }
    /// <summary>Quick filter chip: picture/video/audio/document/code/archive/executable/folder/file.</summary>
    public string? TypeFilter { get; set; }
    public SortKey Sort { get; set; } = SortKey.None;
    public bool SortDescending { get; set; }
    /// <summary>Callback used to resolve content:/meta: terms against the full-text database.</summary>
    public Func<string, int, List<string>>? ContentSearch { get; set; }
    public int ContentLimit { get; set; } = 20000;
}

// ---------------------------------------------------------------------------------------------
// Query AST
// ---------------------------------------------------------------------------------------------

public abstract class QueryNode
{
    public abstract bool Match(FileIndex index, int id, ref Entry e, ref string? path);
    public virtual bool NeedsPath => false;
    public virtual bool IsMatchAll => false;
}

public sealed class TrueNode : QueryNode
{
    public static readonly TrueNode Instance = new();
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path) => true;
    public override bool IsMatchAll => true;
}

public sealed class AndNode : QueryNode
{
    public readonly QueryNode[] Children;
    public AndNode(IEnumerable<QueryNode> children) => Children = children.ToArray();
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
    {
        foreach (var c in Children) if (!c.Match(index, id, ref e, ref path)) return false;
        return true;
    }
    public override bool NeedsPath => Children.Any(c => c.NeedsPath);
    public override bool IsMatchAll => Children.All(c => c.IsMatchAll);
}

public sealed class OrNode : QueryNode
{
    public readonly QueryNode[] Children;
    public OrNode(IEnumerable<QueryNode> children) => Children = children.ToArray();
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
    {
        foreach (var c in Children) if (c.Match(index, id, ref e, ref path)) return true;
        return false;
    }
    public override bool NeedsPath => Children.Any(c => c.NeedsPath);
}

public sealed class NotNode : QueryNode
{
    public readonly QueryNode Child;
    public NotNode(QueryNode child) => Child = child;
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path) => !Child.Match(index, id, ref e, ref path);
    public override bool NeedsPath => Child.NeedsPath;
}

/// <summary>Substring match on name (or full path).</summary>
public sealed class ContainsNode : QueryNode
{
    private readonly string _text; private readonly StringComparison _cmp; private readonly bool _onPath;
    public ContainsNode(string text, bool matchCase, bool onPath) { _text = text; _cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; _onPath = onPath; }
    public override bool NeedsPath => _onPath;
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
    {
        if (_onPath) { path ??= index.PathOf(id); return path.Contains(_text, _cmp); }
        return e.Name!.Contains(_text, _cmp);
    }
}

public sealed class StartsWithNode : QueryNode
{
    private readonly string _text; private readonly StringComparison _cmp;
    public StartsWithNode(string text, bool matchCase) { _text = text; _cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; }
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path) => e.Name!.StartsWith(_text, _cmp);
}

public sealed class EndsWithNode : QueryNode
{
    private readonly string _text; private readonly StringComparison _cmp;
    public EndsWithNode(string text, bool matchCase) { _text = text; _cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; }
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path) => e.Name!.EndsWith(_text, _cmp);
}

public sealed class RegexNode : QueryNode
{
    private readonly Regex _regex; private readonly bool _onPath;
    public RegexNode(Regex regex, bool onPath) { _regex = regex; _onPath = onPath; }
    public override bool NeedsPath => _onPath;
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
    {
        if (_onPath) { path ??= index.PathOf(id); return _regex.IsMatch(path); }
        return _regex.IsMatch(e.Name!);
    }
}

public sealed class ExtNode : QueryNode
{
    private readonly HashSet<string> _exts;
    public ExtNode(IEnumerable<string> exts) => _exts = new HashSet<string>(exts.Select(x => x.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
    {
        if (e.IsDirectory) return false;
        var name = e.Name!;
        int dot = name.LastIndexOf('.');
        if (dot < 0) return _exts.Contains(string.Empty);
        return _exts.Contains(name[(dot + 1)..]);
    }
}

public sealed class TypeNode : QueryNode
{
    private readonly string _group; private readonly HashSet<string>? _set;
    public TypeNode(string group) { _group = group; FileTypes.Groups.TryGetValue(group, out _set); }
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
    {
        if (_group == "folder") return e.IsDirectory;
        if (_group == "file") return !e.IsDirectory;
        if (e.IsDirectory || _set == null) return false;
        var name = e.Name!;
        int dot = name.LastIndexOf('.');
        return dot >= 0 && _set.Contains(name[(dot + 1)..]);
    }
}

public sealed class SizeNode : QueryNode
{
    private readonly long _min, _max;
    public SizeNode(long min, long max) { _min = min; _max = max; }
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path) => !e.IsDirectory && e.Size >= _min && e.Size <= _max;
}

public sealed class DateNode : QueryNode
{
    private readonly bool _created; private readonly long _min, _max;
    public DateNode(bool created, long minTicks, long maxTicks) { _created = created; _min = minTicks; _max = maxTicks; }
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
    {
        long t = _created ? e.Created : e.Modified;
        return t >= _min && t <= _max;
    }
}

public sealed class AttrNode : QueryNode
{
    private readonly FileAttributes _must;
    public AttrNode(FileAttributes must) => _must = must;
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path) => (e.Attributes & _must) == _must;
}

public sealed class InFolderNode : QueryNode
{
    private readonly int _dirId; private readonly bool _recursive;
    public InFolderNode(int dirId, bool recursive) { _dirId = dirId; _recursive = recursive; }
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
        => _dirId >= 0 && (_recursive ? index.IsDescendantOf(id, _dirId) : e.Parent == _dirId);
}

public sealed class PathSetNode : QueryNode
{
    private readonly HashSet<string> _paths;
    public PathSetNode(IEnumerable<string> paths) => _paths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    public override bool NeedsPath => true;
    public int Count => _paths.Count;
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path)
    {
        if (_paths.Count == 0) return false;
        path ??= index.PathOf(id);
        return _paths.Contains(path);
    }
}

public sealed class EmptyNode : QueryNode
{
    public override bool Match(FileIndex index, int id, ref Entry e, ref string? path) => !e.IsDirectory && e.Size == 0;
}

// ---------------------------------------------------------------------------------------------
// Parser
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Everything-style query language.
///   words                 AND (substring, case-insensitive) on the file name
///   a|b                   OR      !term    NOT     ( )   grouping     "quoted phrase"
///   *.jpg  ?              wildcards (match whole name)
///   ext:jpg;png  type:picture|video|audio|document|code|archive|executable|folder|file
///   size:&gt;10mb  size:1mb..5mb  size:empty|tiny|small|medium|large|huge|gigantic
///   dm:today|yesterday|thisweek|lastweek|thismonth|lastmonth|thisyear|2024|2024-05|2024-05-01|&gt;2024-01-01|a..b   (dc: created)
///   path:text  infolder:C:\dir  parent:C:\dir  regex:pattern  startwith:  endwith:  case:term  ww:term
///   attrib:h|s|r|a   empty:   content:text (full-text)  meta:text (extracted properties)
/// </summary>
public static class QueryParser
{
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name","file","folder","ext","type","size","dm","dc","datemodified","datecreated","path","infolder","parent","regex","re",
        "content","meta","startwith","sw","endwith","ew","case","nocase","wholeword","ww","attrib","attr","empty","wildcard","wfn","len"
    };

    public sealed class ParseResult
    {
        public QueryNode Root { get; init; } = TrueNode.Instance;
        public bool HasContentTerm { get; init; }
        public int ContentMatches { get; init; }
        public string? Error { get; init; }
    }

    public static ParseResult Parse(string text, SearchOptions options, FileIndex index)
    {
        var p = new Parser(text ?? string.Empty, options, index);
        try
        {
            var root = p.ParseExpression();
            var extra = new List<QueryNode>();
            if (!root.IsMatchAll) extra.Add(root);
            if (!string.IsNullOrEmpty(options.ScopePath))
            {
                int dir = index.FindDirectory(options.ScopePath);
                extra.Add(new InFolderNode(dir, true));
            }
            if (!string.IsNullOrEmpty(options.TypeFilter))
            {
                var g = FileTypes.Canonical(options.TypeFilter) ?? options.TypeFilter;
                extra.Add(new TypeNode(g));
            }
            QueryNode final = extra.Count == 0 ? TrueNode.Instance : extra.Count == 1 ? extra[0] : new AndNode(extra);
            return new ParseResult { Root = final, HasContentTerm = p.HasContent, ContentMatches = p.ContentMatches };
        }
        catch (Exception ex)
        {
            return new ParseResult { Root = new AndNode(new QueryNode[] { new ContainsNode(text ?? "", options.MatchCase, options.MatchPath) }), Error = ex.Message };
        }
    }

    private sealed class Parser
    {
        private readonly string _s; private int _i;
        private readonly SearchOptions _o; private readonly FileIndex _index;
        public bool HasContent; public int ContentMatches;

        public Parser(string s, SearchOptions o, FileIndex index) { _s = s; _o = o; _index = index; }

        private void SkipWs() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private bool AtEnd { get { SkipWs(); return _i >= _s.Length; } }
        private char Peek => _s[_i];

        public QueryNode ParseExpression()
        {
            var terms = new List<QueryNode>();
            var ors = new List<QueryNode>();
            while (!AtEnd)
            {
                if (Peek == ')') break;
                if (Peek == '|')
                {
                    _i++;
                    ors.Add(Wrap(terms)); terms = new List<QueryNode>();
                    continue;
                }
                terms.Add(ParseUnary());
            }
            if (ors.Count == 0) return Wrap(terms);
            ors.Add(Wrap(terms));
            return new OrNode(ors);
        }

        private static QueryNode Wrap(List<QueryNode> terms) => terms.Count == 0 ? TrueNode.Instance : terms.Count == 1 ? terms[0] : new AndNode(terms);

        private QueryNode ParseUnary()
        {
            SkipWs();
            if (Peek == '!') { _i++; return new NotNode(ParseUnary()); }
            if (Peek == '(')
            {
                _i++;
                var inner = ParseExpression();
                SkipWs();
                if (_i < _s.Length && Peek == ')') _i++;
                return inner;
            }
            return ParseTerm();
        }

        private string ReadValue()
        {
            SkipWs();
            var sb = new StringBuilder();
            if (_i < _s.Length && Peek == '"')
            {
                _i++;
                while (_i < _s.Length && Peek != '"') sb.Append(_s[_i++]);
                if (_i < _s.Length) _i++;
                return sb.ToString();
            }
            int depth = 0;
            while (_i < _s.Length)
            {
                char c = Peek;
                if (char.IsWhiteSpace(c)) break;
                if (c == '|' && depth == 0) break;
                if (c == '(' ) depth++;
                if (c == ')') { if (depth == 0) break; depth--; }
                if (c == '"') { _i++; while (_i < _s.Length && Peek != '"') sb.Append(_s[_i++]); if (_i < _s.Length) _i++; continue; }
                sb.Append(c); _i++;
            }
            return sb.ToString();
        }

        private QueryNode ParseTerm()
        {
            SkipWs();
            // key:value ?
            int colon = -1;
            for (int j = _i; j < _s.Length && !char.IsWhiteSpace(_s[j]) && _s[j] != '"'; j++)
            {
                if (_s[j] == ':') { colon = j; break; }
                if (!char.IsLetter(_s[j])) break;
            }
            if (colon > _i && Keys.Contains(_s[_i..colon]))
            {
                string key = _s[_i..colon].ToLowerInvariant();
                _i = colon + 1;
                string value = ReadValue();
                return BuildKeyed(key, value);
            }
            string term = ReadValue();
            return BuildPlain(term, _o.RegexMode, _o.MatchCase, _o.WholeWord, _o.MatchPath);
        }

        private QueryNode BuildPlain(string term, bool regex, bool matchCase, bool wholeWord, bool matchPath)
        {
            if (term.Length == 0) return TrueNode.Instance;
            bool onPath = matchPath || term.Contains('\\') || term.Contains('/');
            var ro = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            if (regex) return new RegexNode(new Regex(term, ro | RegexOptions.CultureInvariant), onPath);
            if (wholeWord) return new RegexNode(new Regex(@"\b" + Regex.Escape(term) + @"\b", ro | RegexOptions.CultureInvariant), onPath);
            if (term.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                string pattern = "^" + Regex.Escape(term).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                if (onPath) pattern = Regex.Escape(term).Replace(@"\*", ".*").Replace(@"\?", ".");
                return new RegexNode(new Regex(pattern, ro | RegexOptions.CultureInvariant), onPath);
            }
            return new ContainsNode(term.Replace('/', '\\'), matchCase, onPath);
        }

        private QueryNode BuildKeyed(string key, string value)
        {
            switch (key)
            {
                case "name": case "file":
                    return value.Length == 0 ? new TypeNode("file") : new AndNode(new[] { new TypeNode("file"), BuildPlain(value, _o.RegexMode, _o.MatchCase, _o.WholeWord, false) });
                case "folder":
                    return value.Length == 0 ? new TypeNode("folder") : new AndNode(new[] { new TypeNode("folder"), BuildPlain(value, _o.RegexMode, _o.MatchCase, _o.WholeWord, false) });
                case "ext":
                    return new ExtNode(value.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                case "type":
                {
                    var groups = value.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(g => FileTypes.Canonical(g) ?? g.ToLowerInvariant()).ToList();
                    if (groups.Count == 1) return new TypeNode(groups[0]);
                    return new OrNode(groups.Select(g => new TypeNode(g)));
                }
                case "size":
                    return SizeRange.TryParse(value, out long smin, out long smax) ? new SizeNode(smin, smax) : throw new FormatException("Bad size: " + value);
                case "dm": case "datemodified":
                    return DateRange.TryParse(value, out long dmin, out long dmax) ? new DateNode(false, dmin, dmax) : throw new FormatException("Bad date: " + value);
                case "dc": case "datecreated":
                    return DateRange.TryParse(value, out long cmin, out long cmax) ? new DateNode(true, cmin, cmax) : throw new FormatException("Bad date: " + value);
                case "path":
                    return BuildPlain(value, _o.RegexMode, _o.MatchCase, _o.WholeWord, true);
                case "infolder":
                    return new InFolderNode(_index.FindDirectory(value.Trim('"')), true);
                case "parent":
                    return new InFolderNode(_index.FindDirectory(value.Trim('"')), false);
                case "regex": case "re":
                    return new RegexNode(new Regex(value, (_o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant), _o.MatchPath || value.Contains(@"\\"));
                case "startwith": case "sw":
                    return new StartsWithNode(value, _o.MatchCase);
                case "endwith": case "ew":
                    return new EndsWithNode(value, _o.MatchCase);
                case "case":
                    return BuildPlain(value, _o.RegexMode, true, _o.WholeWord, _o.MatchPath);
                case "nocase":
                    return BuildPlain(value, _o.RegexMode, false, _o.WholeWord, _o.MatchPath);
                case "wholeword": case "ww":
                    return BuildPlain(value, false, _o.MatchCase, true, _o.MatchPath);
                case "wildcard": case "wfn":
                    return BuildPlain(value.Contains('*') || value.Contains('?') ? value : "*" + value + "*", false, _o.MatchCase, false, false);
                case "attrib": case "attr":
                {
                    FileAttributes a = 0;
                    foreach (char c in value.ToLowerInvariant())
                        a |= c switch { 'h' => FileAttributes.Hidden, 's' => FileAttributes.System, 'r' => FileAttributes.ReadOnly, 'a' => FileAttributes.Archive, 'd' => FileAttributes.Directory, 'l' => FileAttributes.ReparsePoint, 'c' => FileAttributes.Compressed, 'e' => FileAttributes.Encrypted, _ => 0 };
                    return new AttrNode(a);
                }
                case "empty":
                    return new EmptyNode();
                case "len":
                {
                    if (SizeRange.TryParse(value, out long lmin, out long lmax)) return new LenNode((int)Math.Min(int.MaxValue, lmin), (int)Math.Min(int.MaxValue, lmax));
                    throw new FormatException("Bad len: " + value);
                }
                case "content": case "meta":
                {
                    HasContent = true;
                    if (_o.ContentSearch == null || value.Length == 0) return new PathSetNode(Array.Empty<string>());
                    var paths = _o.ContentSearch(value, _o.ContentLimit);
                    ContentMatches += paths.Count;
                    return new PathSetNode(paths);
                }
            }
            return BuildPlain(key + ":" + value, _o.RegexMode, _o.MatchCase, _o.WholeWord, _o.MatchPath);
        }
    }

    public sealed class LenNode : QueryNode
    {
        private readonly int _min, _max;
        public LenNode(int min, int max) { _min = min; _max = max; }
        public override bool Match(FileIndex index, int id, ref Entry e, ref string? path) => e.Name!.Length >= _min && e.Name.Length <= _max;
    }
}

public static class SizeRange
{
    public static bool TryParse(string text, out long min, out long max)
    {
        min = 0; max = long.MaxValue;
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return false;
        switch (text)
        {
            case "empty": max = 0; return true;
            case "tiny": max = 10 * 1024; return true;
            case "small": min = 10 * 1024; max = 100 * 1024; return true;
            case "medium": min = 100 * 1024; max = 1024 * 1024; return true;
            case "large": min = 1024 * 1024; max = 16L * 1024 * 1024; return true;
            case "huge": min = 16L * 1024 * 1024; max = 128L * 1024 * 1024; return true;
            case "gigantic": min = 128L * 1024 * 1024; return true;
        }
        int range = text.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            var a = text[..range]; var b = text[(range + 2)..];
            if (a.Length > 0 && !TryBytes(a, out min)) return false;
            if (b.Length > 0 && !TryBytes(b, out max)) return false;
            return true;
        }
        if (text.StartsWith(">=")) { return TryBytes(text[2..], out min); }
        if (text.StartsWith("<=")) { return TryBytes(text[2..], out max); }
        if (text.StartsWith('>')) { if (!TryBytes(text[1..], out min)) return false; min += 1; return true; }
        if (text.StartsWith('<')) { if (!TryBytes(text[1..], out max)) return false; max -= 1; return true; }
        if (text.StartsWith('=')) text = text[1..];
        if (!TryBytes(text, out long v)) return false;
        // exact value with unit tolerance: "1mb" means [1MB, 2MB)
        min = v; max = v;
        var unitPart = text.TrimStart("0123456789. ".ToCharArray());
        if (unitPart.Length > 0 && TryBytes("1" + unitPart, out long unit)) max = v + unit - 1;
        return true;
    }

    public static bool TryBytes(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim().ToLowerInvariant();
        int i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
        if (i == 0) return false;
        if (!double.TryParse(text[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return false;
        string unit = text[i..].Trim();
        double mult = unit switch
        {
            "" or "b" or "bytes" => 1,
            "k" or "kb" or "kib" => 1024,
            "m" or "mb" or "mib" => 1024.0 * 1024,
            "g" or "gb" or "gib" => 1024.0 * 1024 * 1024,
            "t" or "tb" or "tib" => 1024.0 * 1024 * 1024 * 1024,
            _ => -1,
        };
        if (mult < 0) return false;
        bytes = (long)(num * mult);
        return true;
    }

    public static string Format(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes / 1024.0;
        if (v < 1024) return v.ToString("0.#") + " KB";
        v /= 1024; if (v < 1024) return v.ToString("0.#") + " MB";
        v /= 1024; if (v < 1024) return v.ToString("0.##") + " GB";
        v /= 1024; return v.ToString("0.##") + " TB";
    }
}

public static class DateRange
{
    /// <summary>Parses to inclusive UTC tick range.</summary>
    public static bool TryParse(string text, out long minTicks, out long maxTicks)
    {
        minTicks = 0; maxTicks = long.MaxValue;
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return false;
        var now = DateTime.Now;
        var today = now.Date;
        DateTime a, b;
        switch (text)
        {
            case "today": a = today; b = today.AddDays(1); return Out(a, b, out minTicks, out maxTicks);
            case "yesterday": a = today.AddDays(-1); b = today; return Out(a, b, out minTicks, out maxTicks);
            case "thisweek": a = today.AddDays(-(int)today.DayOfWeek); b = a.AddDays(7); return Out(a, b, out minTicks, out maxTicks);
            case "lastweek": b = today.AddDays(-(int)today.DayOfWeek); a = b.AddDays(-7); return Out(a, b, out minTicks, out maxTicks);
            case "thismonth": a = new DateTime(today.Year, today.Month, 1); b = a.AddMonths(1); return Out(a, b, out minTicks, out maxTicks);
            case "lastmonth": b = new DateTime(today.Year, today.Month, 1); a = b.AddMonths(-1); return Out(a, b, out minTicks, out maxTicks);
            case "thisyear": a = new DateTime(today.Year, 1, 1); b = a.AddYears(1); return Out(a, b, out minTicks, out maxTicks);
            case "lastyear": b = new DateTime(today.Year, 1, 1); a = b.AddYears(-1); return Out(a, b, out minTicks, out maxTicks);
        }
        var m = Regex.Match(text, @"^last(\d+)(day|days|week|weeks|month|months|year|years|hour|hours|minute|minutes)$");
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value);
            a = m.Groups[2].Value switch
            {
                "day" or "days" => now.AddDays(-n),
                "week" or "weeks" => now.AddDays(-7 * n),
                "month" or "months" => now.AddMonths(-n),
                "year" or "years" => now.AddYears(-n),
                "hour" or "hours" => now.AddHours(-n),
                _ => now.AddMinutes(-n),
            };
            return Out(a, DateTime.MaxValue, out minTicks, out maxTicks);
        }
        int range = text.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            var s1 = text[..range]; var s2 = text[(range + 2)..];
            DateTime lo = DateTime.MinValue, hi = DateTime.MaxValue;
            if (s1.Length > 0 && !TryPoint(s1, out lo, out _)) return false;
            if (s2.Length > 0 && !TryPoint(s2, out _, out hi)) return false;
            return Out(lo, hi, out minTicks, out maxTicks);
        }
        if (text.StartsWith(">=")) { return TryPoint(text[2..], out a, out _) && Out(a, DateTime.MaxValue, out minTicks, out maxTicks); }
        if (text.StartsWith("<=")) { return TryPoint(text[2..], out _, out b) && Out(DateTime.MinValue, b, out minTicks, out maxTicks); }
        if (text.StartsWith('>')) { return TryPoint(text[1..], out _, out a) && Out(a, DateTime.MaxValue, out minTicks, out maxTicks); }
        if (text.StartsWith('<')) { return TryPoint(text[1..], out b, out _) && Out(DateTime.MinValue, b, out minTicks, out maxTicks); }
        if (text.StartsWith('=')) text = text[1..];
        return TryPoint(text, out a, out b) && Out(a, b, out minTicks, out maxTicks);
    }

    /// <summary>Parses "2024", "2024-05", "2024-05-13", "5/13/2024" into a [start, end) window.</summary>
    private static bool TryPoint(string s, out DateTime start, out DateTime end)
    {
        start = default; end = default;
        if (Regex.IsMatch(s, @"^\d{4}$")) { start = new DateTime(int.Parse(s), 1, 1); end = start.AddYears(1); return true; }
        var m = Regex.Match(s, @"^(\d{4})[-/](\d{1,2})$");
        if (m.Success) { start = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 1); end = start.AddMonths(1); return true; }
        m = Regex.Match(s, @"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})$");
        if (m.Success) { start = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); end = start.AddDays(1); return true; }
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var d)) { start = d.Date; end = start.AddDays(1); return true; }
        return false;
    }

    private static bool Out(DateTime a, DateTime b, out long min, out long max)
    {
        min = a == DateTime.MinValue ? 0 : a.ToUniversalTime().Ticks;
        max = b == DateTime.MaxValue ? long.MaxValue : b.ToUniversalTime().Ticks - 1;
        return true;
    }
}
