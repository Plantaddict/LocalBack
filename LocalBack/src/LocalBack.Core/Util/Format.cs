using System.Globalization;

namespace LocalBack.Core.Util;

/// <summary>Human-friendly sizes and times, matching the wording in the design.</summary>
public static class Format
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes == 0 ? "0 KB" : "1 KB";
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        if (u == 1) return $"{Math.Round(v):0} KB";
        if (v >= 1000 && u < units.Length - 1) return string.Create(En, $"{v / 1024:0.0} {units[u + 1]}");
        return v >= 100 ? string.Create(En, $"{v:0} {units[u]}") : string.Create(En, $"{v:0.0} {units[u]}");
    }

    /// <summary>"14:32 today", "Yesterday 18:00", "Fri 3 Oct, 18:00".</summary>
    public static string When(DateTimeOffset utc, DateTimeOffset? now = null)
    {
        var local = utc.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        if (local.Date == today) return local.ToString("HH:mm", En) + " today";
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm", En);
        return local.ToString("ddd d MMM, HH:mm", En);
    }

    /// <summary>"Today 14:32", "Yesterday 18:00", "Fri 3 Oct, 18:00" (snapshot list labels).</summary>
    public static string SnapshotLabel(DateTimeOffset utc, DateTimeOffset? now = null)
    {
        var local = utc.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        if (local.Date == today) return "Today " + local.ToString("HH:mm", En);
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm", En);
        return local.ToString("ddd d MMM, HH:mm", En);
    }

    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
