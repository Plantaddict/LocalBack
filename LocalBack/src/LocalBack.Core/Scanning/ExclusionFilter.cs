using System.Text.RegularExpressions;
using LocalBack.Core.Model;

namespace LocalBack.Core.Scanning;

/// <summary>
/// Decides what to skip before any file is opened.
/// Pattern forms: <c>*.tmp</c> (file or folder name), <c>node_modules/</c> (folder name),
/// <c>Renders/Final/</c> or <c>docs/*.pdf</c> (relative path, matched at any depth).
/// </summary>
public sealed class ExclusionFilter
{
    private readonly List<Regex> _names = new();
    private readonly List<Regex> _dirNames = new();
    private readonly List<Regex> _paths = new();
    private readonly List<Regex> _dirPaths = new();
    public long? MaxFileSize { get; }

    public ExclusionFilter(IEnumerable<string> enabledRules, IEnumerable<string> customPatterns)
    {
        foreach (var id in enabledRules)
        {
            var rule = ExclusionRules.Find(id);
            if (rule == null) continue;
            foreach (var p in rule.Patterns) Add(p);
            if (rule.MaxSize is long max) MaxFileSize = MaxFileSize is long cur ? Math.Min(cur, max) : max;
        }
        foreach (var p in customPatterns) Add(p);
    }

    public static ExclusionFilter For(BackupSet set) => new(set.EnabledRules, set.CustomPatterns);

    private void Add(string pattern)
    {
        var p = pattern.Trim().Replace('\\', '/');
        if (p.Length == 0) return;
        bool dirOnly = p.EndsWith('/');
        p = p.Trim('/');
        if (p.Length == 0) return;
        var regex = GlobToRegex(p);
        if (p.Contains('/'))
            (dirOnly ? _dirPaths : _paths).Add(regex);
        else
            (dirOnly ? _dirNames : _names).Add(regex);
    }

    /// <summary>Validates a custom pattern typed by the user. Returns an error message or null.</summary>
    public static string? Validate(string pattern)
    {
        var p = pattern.Trim();
        if (p.Length == 0) return "Type a pattern such as *.iso or Renders/";
        if (p.Trim('/', '\\').Length == 0) return "That pattern would match everything.";
        if (p == "*" || p == "*.*") return "That pattern would match every file.";
        return null;
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var c in glob)
        {
            sb.Append(c switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(c.ToString()),
            });
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>Attributes that always exclude: hidden+system files (pagefile, OS metadata) and links.</summary>
    public static bool IsExcludedByAttributes(FileAttributes attrs) =>
        (attrs & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)
        || (attrs & FileAttributes.ReparsePoint) != 0;

    /// <param name="relativePath">Path relative to the source folder, forward slashes.</param>
    public bool ExcludeDirectory(string relativePath)
    {
        var name = LastSegment(relativePath);
        foreach (var r in _names) if (r.IsMatch(name)) return true;
        foreach (var r in _dirNames) if (r.IsMatch(name)) return true;
        foreach (var r in _paths) if (MatchesSuffix(r, relativePath)) return true;
        foreach (var r in _dirPaths) if (MatchesSuffix(r, relativePath)) return true;
        return false;
    }

    public bool ExcludeFile(string relativePath, long size)
    {
        if (MaxFileSize is long max && size > max) return true;
        var name = LastSegment(relativePath);
        foreach (var r in _names) if (r.IsMatch(name)) return true;
        foreach (var r in _paths) if (MatchesSuffix(r, relativePath)) return true;
        return false;
    }

    /// <summary>True when any folder on the way to <paramref name="relativePath"/> is excluded (used for single changed paths).</summary>
    public bool ExcludeAnyParent(string relativePath)
    {
        var idx = -1;
        while ((idx = relativePath.IndexOf('/', idx + 1)) >= 0)
        {
            if (ExcludeDirectory(relativePath[..idx])) return true;
        }
        return false;
    }

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>Path patterns match the whole relative path or any trailing run of segments.</summary>
    private static bool MatchesSuffix(Regex r, string relativePath)
    {
        if (r.IsMatch(relativePath)) return true;
        var idx = -1;
        while ((idx = relativePath.IndexOf('/', idx + 1)) >= 0)
        {
            if (r.IsMatch(relativePath[(idx + 1)..])) return true;
        }
        return false;
    }
}
