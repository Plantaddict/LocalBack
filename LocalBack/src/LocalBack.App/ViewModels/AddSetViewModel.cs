using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LocalBack.Core.Drives;
using LocalBack.Core.Model;
using LocalBack.Core.Scanning;
using LocalBack.Core.Service;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>"Add backup set" dialog; also used to edit an existing set (drive cannot change then).</summary>
public sealed class AddSetViewModel : ObservableObject
{
    private readonly App _app = App.Current;
    private readonly BackupSet? _editing;
    private string _name = "";
    private DriveChoice? _drive;
    private ScheduleChoice _schedule;
    private string _custom = "";
    private bool _keepHistory = true;
    private string _error = "";

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<DriveChoice> Drives { get; } = new();
    public List<ScheduleChoice> Schedules { get; }
    public List<RuleTile> Rules { get; }
    public ObservableCollection<string> CustomPatterns { get; } = new();

    public event Action<bool>? CloseRequested;

    public AddSetViewModel(BackupSet? editing)
    {
        _editing = editing;
        var check = DateTime.Today.Add(_app.Service.Settings.DailyCheckAt).ToString("HH:mm");
        Schedules = new()
        {
            new(RunSchedule.Live, "When files change (live)"),
            new(RunSchedule.Hourly, "Every hour"),
            new(RunSchedule.Daily, $"Daily at {check}"),
            new(RunSchedule.OnPlugIn, "When the drive is plugged in"),
        };
        var enabled = editing?.EnabledRules ?? ExclusionRules.DefaultEnabled.ToList();
        Rules = ExclusionRules.All.Select(r => new RuleTile(r, enabled.Contains(r.Id), () => Raise(nameof(RulesSummary)))).ToList();
        _schedule = Schedules[0];

        if (editing != null)
        {
            _name = editing.Name;
            foreach (var f in editing.Folders) Folders.Add(f);
            foreach (var p in editing.CustomPatterns) CustomPatterns.Add(p);
            _keepHistory = editing.KeepHistory;
            _schedule = Schedules.First(s => s.Value == editing.Schedule);
            Drives.Add(new DriveChoice(null, _app.DriveDisplayName(editing)));
            _drive = Drives[0];
        }
        else
        {
            _name = "Desktop";
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desktop)) Folders.Add(desktop);
            LoadDrives();
        }

        AddFolder = new RelayCommand(PickFolders);
        RemoveFolder = new RelayCommand(p => { if (p is string s) Folders.Remove(s); });
        AddPattern = new RelayCommand(AddCustom);
        RemovePattern = new RelayCommand(p => { if (p is string s) CustomPatterns.Remove(s); Raise(nameof(RulesSummary)); });
        RefreshDrives = new RelayCommand(LoadDrives, () => !IsEditing);
        Create = new RelayCommand(Submit);
        Cancel = new RelayCommand(() => CloseRequested?.Invoke(false));
        Folders.CollectionChanged += (_, _) => { Error = ""; SuggestName(); };
    }

    public bool IsEditing => _editing != null;
    public string Title => IsEditing ? "Edit backup set" : "Add backup set";
    public string SubmitText => IsEditing ? "Save changes" : "Create and back up";

    public ICommand AddFolder { get; }
    public ICommand RemoveFolder { get; }
    public ICommand AddPattern { get; }
    public ICommand RemovePattern { get; }
    public ICommand RefreshDrives { get; }
    public ICommand Create { get; }
    public ICommand Cancel { get; }

    public string Name { get => _name; set { if (Set(ref _name, value)) Error = ""; } }
    public DriveChoice? SelectedDrive { get => _drive; set { if (Set(ref _drive, value)) Error = ""; } }
    public ScheduleChoice SelectedSchedule { get => _schedule; set => Set(ref _schedule, value); }
    public string CustomPattern { get => _custom; set => Set(ref _custom, value); }
    public bool KeepHistory { get => _keepHistory; set => Set(ref _keepHistory, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool NoDrives => Drives.Count == 0;

    public string RulesSummary =>
        $"{Rules.Count(r => r.IsOn) + CustomPatterns.Count} rules on · files still being written are picked up next pass";

    private void LoadDrives()
    {
        var current = _drive?.Root;
        Drives.Clear();
        foreach (var d in App.BackupDriveChoices())
            Drives.Add(new DriveChoice(d.Root, $"{d.DisplayName} ({Format.Size(d.Free)} free)"));
        SelectedDrive = Drives.FirstOrDefault(d => d.Root == current)
                        ?? Drives.FirstOrDefault(d => DriveLocator.Describe(d.Root!)?.HasStore == true)
                        ?? Drives.FirstOrDefault();
        Raise(nameof(NoDrives));
    }

    private bool _nameTouched;
    private void SuggestName()
    {
        // Name follows the first folder until the user types their own.
        if (IsEditing || _nameTouched) return;
        if (Folders.Count > 0) _name = System.IO.Path.GetFileName(Folders[0].TrimEnd('\\')) is { Length: > 0 } n ? n : _name;
        Raise(nameof(Name));
    }

    public void NameEdited() => _nameTouched = true;

    private void PickFolders()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Choose folders to back up" };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FolderNames)
            if (!Folders.Contains(f, StringComparer.OrdinalIgnoreCase)) Folders.Add(f);
    }

    private void AddCustom()
    {
        var p = CustomPattern.Trim();
        if (ExclusionFilter.Validate(p) is { } err)
        {
            Error = err;
            return;
        }
        if (!CustomPatterns.Contains(p, StringComparer.OrdinalIgnoreCase)) CustomPatterns.Add(p);
        CustomPattern = "";
        Error = "";
        Raise(nameof(RulesSummary));
    }

    private void Submit()
    {
        if (CustomPattern.Trim().Length > 0) AddCustom();
        if (HasError) return;
        if (Name.Trim().Length == 0) { Error = "Give the set a name."; return; }
        if (Folders.Count == 0) { Error = "Add at least one folder to back up."; return; }
        try { Core.Service.BackupService.NormalizeFolders(Folders); }
        catch (ArgumentException ex) { Error = ex.Message; return; }
        var missing = Folders.FirstOrDefault(f => !Directory.Exists(f));
        if (missing != null) { Error = $"{missing} does not exist."; return; }
        var rules = Rules.Where(r => r.IsOn).Select(r => r.Rule.Id).ToList();

        if (_editing != null)
        {
            var updated = SettingsStore.Clone(_editing);
            updated.Name = Name.Trim();
            updated.Folders = Folders.Select(PathUtil.NormalizeFolder).ToList();
            updated.Schedule = SelectedSchedule.Value;
            updated.EnabledRules = rules;
            updated.CustomPatterns = CustomPatterns.ToList();
            updated.KeepHistory = KeepHistory;
            _app.Service.UpdateSet(updated);
            CloseRequested?.Invoke(true);
            return;
        }

        if (SelectedDrive?.Root is not { } root) { Error = "Plug in a USB drive or external disk to keep the backups on."; return; }
        var driveRoot = PathUtil.NormalizeFolder(root);
        var clash = Folders.FirstOrDefault(f => PathUtil.IsUnder(PathUtil.NormalizeFolder(f), driveRoot) || PathUtil.IsUnder(driveRoot, PathUtil.NormalizeFolder(f)));
        if (clash != null) { Error = $"{clash} is on the backup drive itself. Pick a different drive or folder."; return; }

        try
        {
            _app.Service.AddSet(Name, Folders, root, SelectedSchedule.Value, rules, CustomPatterns, KeepHistory);
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Could not write to {SelectedDrive.Text}: {ex.Message}";
        }
    }
}

public sealed record DriveChoice(string? Root, string Text);

public sealed record ScheduleChoice(RunSchedule Value, string Text);

/// <summary>One toggleable exclusion tile.</summary>
public sealed class RuleTile : ObservableObject
{
    private readonly Action _changed;
    private bool _on;

    public RuleTile(ExclusionRule rule, bool on, Action changed)
    {
        Rule = rule;
        _on = on;
        _changed = changed;
        Toggle = new RelayCommand(() => IsOn = !IsOn);
    }

    public ExclusionRule Rule { get; }
    public string Title => Rule.Title;
    public string Patterns => Rule.Description;
    public ICommand Toggle { get; }

    public bool IsOn
    {
        get => _on;
        set
        {
            if (!Set(ref _on, value)) return;
            Raise(nameof(Border));
            Raise(nameof(Background));
            Raise(nameof(BoxBorder));
            Raise(nameof(BoxBackground));
            _changed();
        }
    }

    public Brush Border => IsOn ? Ui.Brush("AccentTintBorder") : Ui.Brush("Border");
    public Brush Background => IsOn ? Ui.Brush("AccentTintLight") : Brushes.White;
    public Brush BoxBorder => IsOn ? Ui.Brush("Accent") : (Brush)new BrushConverter().ConvertFrom("#B4B4B4")!;
    public Brush BoxBackground => IsOn ? Ui.Brush("Accent") : Brushes.White;
}
