using System.Windows;
using System.Windows.Media;
using LocalBack.App.Localization;
using LocalBack.Core.Model;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>Brushes, localized status texts and small helpers shared by the view models.</summary>
public static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Green up to date, amber pending/warning, red failed, blue running.</summary>
    public static Brush Dot(SetHealth h) => h switch
    {
        SetHealth.UpToDate => Brush("Green"),
        SetHealth.Running => Brush("Accent"),
        SetHealth.Error => Brush("Red"),
        SetHealth.NeverRun or SetHealth.Disabled => Brush("BorderStrong"),
        _ => Brush("Amber"),
    };

    public static string StatusText(SetHealth h) => Loc.T("status." + h);

    /// <summary>Second line under the status: "14:32 today", "3 files changed".</summary>
    public static string WhenText(SetStatus s) => s.Health switch
    {
        SetHealth.Pending or SetHealth.DriveMissing or SetHealth.Paused or SetHealth.Locked when s.PendingCount > 0 => Format.Plural(s.PendingCount, "file changed", "files changed"),
        SetHealth.Error => s.Error ?? "",
        _ => s.LastRun is { } t ? Format.When(t) : Loc.T("status.never"),
    };

    /// <summary>Short form for the tray list: "14:32", "3 pending".</summary>
    public static string ShortWhen(SetStatus s) => s.Health switch
    {
        SetHealth.Running or SetHealth.Error or SetHealth.Disabled or SetHealth.Locked => Loc.T("short." + s.Health),
        _ when s.PendingCount > 0 => Format.Plural(s.PendingCount, "pending", "pending"),
        SetHealth.DriveMissing => Loc.T("short.DriveMissing"),
        _ => s.LastRun is { } t ? (t.ToLocalTime().Date == DateTime.Today ? t.ToLocalTime().ToString("HH:mm") : Format.When(t)) : Loc.T("short.never"),
    };

    public static string SizeText(SetStatus s) => s.Latest is { } l ? Format.Size(l.Bytes) : "—";
    public static string VersionsText(SetStatus s) => Format.Plural(s.Versions, "version", "versions");

    public static string ScheduleText(RunSchedule s) => s switch
    {
        RunSchedule.Live => Loc.T("schedule.live"),
        RunSchedule.Hourly => Loc.T("schedule.hourly"),
        RunSchedule.Daily => Loc.T("schedule.daily", DateTime.Today.Add(App.Current.Service.Settings.DailyCheckAt).ToString("HH:mm")),
        _ => Loc.T("schedule.plugin"),
    };

    public static string TriggerText(SnapshotTrigger t) => Loc.T("trigger." + t);

    public static string RuleTitle(ExclusionRule r) => Loc.T("rule." + r.Id);

    public static string RetentionTitle(RetentionPlan p) => p.Kind switch
    {
        RetentionKind.KeepLast => Loc.T("retention.keepLast.title", p.Count),
        RetentionKind.DailyThenWeekly => Loc.T("retention.daily.title"),
        _ => Loc.T("retention.older.title", p.Days),
    };

    public static string RetentionDescription(RetentionPlan p) => p.Kind switch
    {
        RetentionKind.KeepLast => Loc.T("retention.keepLast.desc"),
        RetentionKind.DailyThenWeekly => Loc.T("retention.daily.desc"),
        _ => Loc.T("retention.older.desc", p.Days),
    };

    public static bool Confirm(string text, string title = "LocalBack", Window? owner = null)
    {
        owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return (owner != null
            ? MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK;
    }

    public static void Info(string text, string title = "LocalBack")
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner != null) MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
