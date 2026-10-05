namespace LocalBack.Core.Model;

public sealed record ExclusionRule(string Id, string Title, string Description, string[] Patterns, long? MaxSize = null);

/// <summary>The built-in exclusion tiles shown in the Add backup set dialog.</summary>
public static class ExclusionRules
{
    public const long TwoGB = 2L * 1024 * 1024 * 1024;

    public static readonly IReadOnlyList<ExclusionRule> All = new[]
    {
        new ExclusionRule("temp", "Temporary files", "*.tmp, *.temp, *.bak, *~", new[] { "*.tmp", "*.temp", "*.bak", "*~" }),
        new ExclusionRule("lock", "Office lock files", "~$*, .~lock.*", new[] { "~$*", ".~lock.*" }),
        new ExclusionRule("dl", "Downloads in progress", "*.crdownload, *.part, *.partial", new[] { "*.crdownload", "*.part", "*.partial" }),
        new ExclusionRule("sys", "Windows system files", "Thumbs.db, desktop.ini, $RECYCLE.BIN", new[] { "Thumbs.db", "desktop.ini", "$RECYCLE.BIN/", "System Volume Information/" }),
        new ExclusionRule("dev", "Developer folders", "node_modules/, .git/, bin/, obj/", new[] { "node_modules/", ".git/", "bin/", "obj/" }),
        new ExclusionRule("cache", "Caches", ".cache/, __pycache__/, *.log", new[] { ".cache/", "__pycache__/", "*.log" }),
        new ExclusionRule("large", "Files over 2 GB", "size > 2 GB", Array.Empty<string>(), TwoGB),
        new ExclusionRule("media", "Raw video and disk images", "*.iso, *.vmdk, *.mov, *.mkv", new[] { "*.iso", "*.vmdk", "*.mov", "*.mkv" }),
    };

    public static readonly IReadOnlyList<string> DefaultEnabled = new[] { "temp", "lock", "dl", "sys", "dev", "cache" };

    public static ExclusionRule? Find(string id) => All.FirstOrDefault(r => r.Id == id);
}
