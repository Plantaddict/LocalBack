using System.Windows;
using System.Windows.Threading;
using LocalBack.App.Services;
using LocalBack.App.Views;
using LocalBack.Core.Drives;
using LocalBack.Core.Model;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.App;

/// <summary>
/// Lives in the tray. Windows are created when opened and released when closed, so idle memory stays small.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private DeviceNotifier? _devices;
    private DispatcherTimer? _statusTimer;
    private MainWindow? _main;
    private TrayFlyout? _flyout;
    private FreeSpaceWindow? _freeSpace;
    private DateTime _flyoutClosedAt;

    public static new App Current => (App)Application.Current;
    private BackupService? _service;
    public BackupService Service => _service ?? throw new InvalidOperationException("Not started");

    /// <summary>Raised on the UI thread (coalesced) when anything about sets or runs changed.</summary>
    public event Action? StatusRefreshed;

    /// <summary>Set by UI tests before constructing the app, so the normal startup (tray, first-run dialog) is skipped.</summary>
    internal static bool TestMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (TestMode) return;
        _instance = new SingleInstance();
        if (!_instance.TryAcquire())
        {
            SingleInstance.Send(e.Args);
            Shutdown();
            return;
        }
        _instance.ArgumentsReceived += args => Dispatcher.InvokeAsync(() => HandleArgs(args, fromOtherInstance: true));
        _instance.Listen();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI error", ex.Exception);
            MessageBox.Show(ex.Exception.Message, "LocalBack", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };

        _service = new BackupService(new AppPaths()) { IsOnBattery = WindowsIntegration.IsOnBattery };
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => FlushStatus(), Dispatcher);
        Service.StatusChanged += () => Dispatcher.InvokeAsync(() => { if (!_statusTimer.IsEnabled) _statusTimer.Start(); });
        Service.LowSpace += info => Dispatcher.InvokeAsync(() => ShowFreeSpace(info.Drive, lowSpace: true));
        Service.Notify += (title, text, error) => Dispatcher.InvokeAsync(() => _tray?.ShowBalloon(title, text, error));

        _tray = new TrayIcon();
        _tray.Clicked += ToggleFlyout;
        _tray.OpenRequested += () => OpenMain();
        _tray.BackUpNowRequested += () => _ = Service.BackUpNowAsync();
        _tray.RestoreRequested += () => OpenHistory(null);
        _tray.PauseToggleRequested += TogglePause;
        _tray.ExitRequested += ExitApp;

        _devices = new DeviceNotifier();
        _devices.Arrived += roots => Task.Run(() => { foreach (var r in roots) Service.OnDriveArrived(r); });
        _devices.Removed += _ => Task.Run(Service.OnDriveRemoved);

        WindowsIntegration.Apply(Service.Settings.StartWithWindows, Service.Settings.ExplorerMenu);
        Service.Start();
        FlushStatus();
        HandleArgs(e.Args, fromOtherInstance: false);
    }

    /// <summary>For UI tests: use a service without tray, device watcher or single-instance lock.</summary>
    internal void UseServiceForTests(BackupService service)
    {
        _service = service;
        service.StatusChanged += () => Dispatcher.InvokeAsync(FlushStatus);
    }

    private void HandleArgs(string[] args, bool fromOtherInstance)
    {
        int h = Array.IndexOf(args, "--history");
        if (h >= 0 && h + 1 < args.Length)
        {
            OpenHistory(args[h + 1]);
            return;
        }
        if (args.Contains("--tray") && !fromOtherInstance) return;
        OpenMain();
        if (Service.Sets.Count == 0 && !fromOtherInstance) ShowAddSet(null);
    }

    private void FlushStatus()
    {
        _statusTimer?.Stop();
        var statuses = Service.GetStatuses();
        var worst = Worst(statuses);
        string tip = statuses.Count == 0 ? "LocalBack — no backup sets yet"
            : Service.RunningSetName is { } running ? $"LocalBack — backing up {running}…"
            : worst == SetHealth.UpToDate ? "LocalBack — everything is backed up"
            : $"LocalBack — {statuses.First(s => s.Health == worst).StatusText.ToLowerInvariant()}";
        _tray?.Update(worst, tip, Service.IsPaused);
        StatusRefreshed?.Invoke();
    }

    public static SetHealth Worst(IReadOnlyList<SetStatus> statuses)
    {
        if (statuses.Any(s => s.Health == SetHealth.Running)) return SetHealth.Running;
        foreach (var h in new[] { SetHealth.Error, SetHealth.DriveMissing, SetHealth.Paused, SetHealth.Pending, SetHealth.NeverRun })
            if (statuses.Any(s => s.Health == h)) return h;
        return SetHealth.UpToDate;
    }

    // ------------------------------------------------------------------ windows

    public MainWindow OpenMain()
    {
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) =>
            {
                _main = null;
                // Give the memory back: windows are the bulk of the working set.
                Dispatcher.InvokeAsync(() => GC.Collect(2, GCCollectionMode.Optimized, false), DispatcherPriority.ApplicationIdle);
            };
            _main.Show();
        }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        return _main;
    }

    public void OpenHistory(string? path, BackupSet? set = null)
    {
        var main = OpenMain();
        main.ViewModel.ShowHistory(set, path);
    }

    public void ShowAddSet(BackupSet? editing)
    {
        var owner = OpenMain();
        var dlg = new AddSetWindow(editing) { Owner = owner };
        if (dlg.ShowDialog() == true) FlushStatus();
    }

    public void ShowFreeSpace(DriveStore drive, bool lowSpace)
    {
        if (_freeSpace != null)
        {
            _freeSpace.Activate();
            return;
        }
        _freeSpace = new FreeSpaceWindow(drive, lowSpace);
        if (_main != null) _freeSpace.Owner = _main;
        _freeSpace.Closed += (_, _) => _freeSpace = null;
        _freeSpace.Show();
        _freeSpace.Activate();
    }

    private void ToggleFlyout()
    {
        if (_flyout != null)
        {
            _flyout.Close();
            return;
        }
        // Clicking the tray icon deactivates (and closes) the flyout first; don't reopen it immediately.
        if ((DateTime.UtcNow - _flyoutClosedAt).TotalMilliseconds < 300) return;
        _flyout = new TrayFlyout();
        _flyout.Closed += (_, _) =>
        {
            _flyout = null;
            _flyoutClosedAt = DateTime.UtcNow;
        };
        _flyout.Show();
        _flyout.Activate();
    }

    public void TogglePause()
    {
        if (Service.IsPaused) Service.Resume();
        else Service.Pause(TimeSpan.FromHours(1));
    }

    public void ExitApp()
    {
        _flyout?.Close();
        _main?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _devices?.Dispose();
        _tray?.Dispose();
        _service?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Describes a set's drive for the UI: "E: Samsung T7".</summary>
    public string DriveDisplayName(BackupSet set) =>
        Service.DriveFor(set) is { } d ? BackupService.DriveName(d)
        : string.IsNullOrEmpty(set.Drive.Label) ? set.Drive.LastRoot.TrimEnd('\\') : $"{set.Drive.LastRoot.TrimEnd('\\')} {set.Drive.Label}";

    public static List<DriveCandidate> BackupDriveChoices() =>
        DriveLocator.ListDrives().Where(d => !d.IsSystem).ToList();
}
