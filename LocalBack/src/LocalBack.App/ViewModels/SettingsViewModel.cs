using System.Windows.Input;
using LocalBack.App.Localization;
using LocalBack.App.Services;
using LocalBack.Core.Model;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>"Settings": language, startup, Explorer menu, daily check, version retention, battery.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly App _app = App.Current;

    public SettingsViewModel()
    {
        Hours = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();
        RetentionChoices = RetentionPlan.Choices.Select(p => new RetentionChoice(p, this)).ToList();
        Languages = new List<LanguageChoice> { new(null, Loc.T("settings.language.system")) };
        Languages.AddRange(Loc.Available.Select(a => new LanguageChoice(a.Code, a.Name)));
        OpenLogs = new RelayCommand(() => WindowsIntegration.OpenWithShell(new Core.Service.AppPaths().DataDir));
        TogglePause = new RelayCommand(() => { _app.TogglePause(); Raise(nameof(PauseText)); Raise(nameof(PauseButton)); });
    }

    private Core.Service.BackupService Service => _app.Service;

    public List<LanguageChoice> Languages { get; }

    public LanguageChoice SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => l.Code == Service.Settings.Language) ?? Languages[0];
        set
        {
            if (value == null || value.Code == Service.Settings.Language) return;
            Save(s => s.Language = value.Code);
            _app.ApplyLanguage();
            Languages[0] = new LanguageChoice(null, Loc.T("settings.language.system"));
            RaiseAll();
        }
    }

    public bool StartWithWindows
    {
        get => Service.Settings.StartWithWindows;
        set { Save(s => s.StartWithWindows = value); }
    }

    public bool ExplorerMenu
    {
        get => Service.Settings.ExplorerMenu;
        set { Save(s => s.ExplorerMenu = value); }
    }

    public bool ThrottleOnBattery
    {
        get => Service.Settings.ThrottleOnBattery;
        set { Save(s => s.ThrottleOnBattery = value); }
    }

    public bool AutoFreeSpace
    {
        get => Service.Settings.AutoFreeSpace;
        set { Save(s => s.AutoFreeSpace = value); }
    }

    public List<string> Hours { get; }

    public string DailyCheck
    {
        get => $"{Service.Settings.DailyCheckAt.Hours:00}:00";
        set
        {
            if (int.TryParse(value.Split(':')[0], out var h))
                Save(s => s.DailyCheckAt = TimeSpan.FromHours(h));
        }
    }

    public List<RetentionChoice> RetentionChoices { get; }

    internal RetentionPlan Retention
    {
        get => Service.Settings.Retention;
        set { Save(s => s.Retention = value); foreach (var c in RetentionChoices) c.Changed(); }
    }

    public string PauseText => Service.IsPaused
        ? Loc.T("settings.pausedUntil", Service.Settings.PausedUntil!.Value.ToLocalTime().ToString("HH:mm"))
        : Loc.T("settings.running");
    public string PauseButton => Loc.T(Service.IsPaused ? "settings.resume" : "settings.pause1h");
    public ICommand TogglePause { get; }
    public ICommand OpenLogs { get; }
    public string Version => $"LocalBack {typeof(App).Assembly.GetName().Version?.ToString(3)}";

    private void Save(Action<AppSettings> change)
    {
        Service.SaveSettings(change);
        WindowsIntegration.Apply(Service.Settings.StartWithWindows, Service.Settings.ExplorerMenu);
        RaiseAll();
        Log.Info("Settings changed");
    }
}

public sealed record LanguageChoice(string? Code, string Name);

public sealed class RetentionChoice : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public RetentionChoice(RetentionPlan plan, SettingsViewModel owner)
    {
        Plan = plan;
        _owner = owner;
    }

    public RetentionPlan Plan { get; }
    public string Title => Ui.RetentionTitle(Plan);
    public string Description => Ui.RetentionDescription(Plan);

    public bool IsChecked
    {
        get => _owner.Retention.Kind == Plan.Kind;
        set { if (value) _owner.Retention = Plan; }
    }

    internal void Changed() => RaiseAll();
}
