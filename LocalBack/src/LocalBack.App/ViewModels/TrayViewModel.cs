using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LocalBack.App.Localization;
using LocalBack.Core.Service;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>The tray flyout: status at a glance and the four common actions.</summary>
public sealed class TrayViewModel : ObservableObject, IDisposable
{
    private readonly App _app = App.Current;

    public ObservableCollection<TraySetRow> Sets { get; } = new();
    public event Action? CloseRequested;

    public TrayViewModel()
    {
        BackUpNow = new RelayCommand(() => { _ = _app.Service.BackUpNowAsync(); });
        Restore = new RelayCommand(() => { CloseRequested?.Invoke(); _app.OpenHistory(null); });
        TogglePause = new RelayCommand(() => _app.TogglePause());
        OpenApp = new RelayCommand(() => { CloseRequested?.Invoke(); _app.OpenMain(); });
        _app.StatusRefreshed += Refresh;
        Refresh();
    }

    public ICommand BackUpNow { get; }
    public ICommand Restore { get; }
    public ICommand TogglePause { get; }
    public ICommand OpenApp { get; }

    public string Headline { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public Brush BannerBackground { get; private set; } = Brushes.Transparent;
    public Brush BannerForeground { get; private set; } = Brushes.Black;
    public Brush BannerDetail { get; private set; } = Brushes.Black;
    public Geometry BannerIcon { get; private set; } = Geometry.Empty;
    public string PauseText => Loc.T(_app.Service.IsPaused ? "tray.resume" : "tray.pause");

    private void Refresh()
    {
        var service = _app.Service;
        var statuses = service.GetStatuses();
        Sets.Clear();
        foreach (var s in statuses) Sets.Add(new TraySetRow(s.Set.Name, Ui.ShortWhen(s), Ui.Dot(s.Health)));

        var worst = App.Worst(statuses);
        var lastRun = statuses.Where(s => s.LastRun != null).Select(s => s.LastRun!.Value).DefaultIfEmpty().Max();
        var drives = statuses.Select(s => s.DriveName).Distinct().ToList();
        var parts = new List<string>();
        if (lastRun != default) parts.Add(Loc.T("tray.lastRun", lastRun.ToLocalTime().Date == DateTime.Today ? lastRun.ToLocalTime().ToString("HH:mm") : Format.When(lastRun)));
        parts.Add(Format.Plural(statuses.Count, "set", "sets"));
        if (drives.Count == 1) parts.Add(drives[0]);
        Detail = string.Join(" · ", parts);

        (Headline, string tone) = statuses.Count == 0 ? (Loc.T("tray.headline.none"), "amber")
            : service.RunningSetName is { } r ? (Loc.T("tray.headline.running", r), "blue")
            : worst switch
            {
                SetHealth.UpToDate => (Loc.T("tray.headline.upToDate"), "green"),
                SetHealth.Error => (Loc.T("tray.headline.error"), "red"),
                SetHealth.DriveMissing => (Loc.T("tray.headline.driveMissing"), "amber"),
                SetHealth.Locked => (Loc.T("tray.headline.locked"), "amber"),
                SetHealth.Paused => (Loc.T("tray.headline.paused", service.Settings.PausedUntil!.Value.ToLocalTime().ToString("HH:mm")), "amber"),
                SetHealth.NeverRun => (Loc.T("tray.headline.first"), "amber"),
                SetHealth.Disabled => (Loc.T("tray.headline.disabled"), "amber"),
                _ => (Loc.T("tray.headline.pending", Format.Plural(statuses.Sum(s => s.PendingCount), "change", "changes")), "amber"),
            };
        (BannerBackground, BannerForeground, BannerDetail, BannerIcon) = tone switch
        {
            "green" => (Ui.Brush("GreenTint"), Ui.Brush("Green"), Ui.Brush("GreenText"), Geo("IconCheckCircle")),
            "blue" => (Ui.Brush("AccentTint"), Ui.Brush("Accent"), Ui.Brush("AccentDark"), Geo("IconBackUp")),
            "red" => (Ui.Brush("RedTint"), Ui.Brush("Red"), Ui.Brush("Red"), Geo("IconWarning")),
            _ => (Ui.Brush("AmberTint"), Ui.Brush("Amber"), Ui.Brush("Amber"), Geo("IconInfo")),
        };
        RaiseAll();
    }

    private static Geometry Geo(string key) => (Geometry)System.Windows.Application.Current.FindResource(key);

    public void Dispose() => _app.StatusRefreshed -= Refresh;
}

public sealed record TraySetRow(string Name, string When, Brush Dot);
