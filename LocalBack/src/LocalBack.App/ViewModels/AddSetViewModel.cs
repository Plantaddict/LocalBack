using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LocalBack.App.Localization;
using LocalBack.Core.Drives;
using LocalBack.Core.Engine;
using LocalBack.Core.Model;
using LocalBack.Core.Scanning;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

public enum DestinationState { None, New, Plain, Encrypted, Locked }

/// <summary>"Add backup set" dialog; also used to edit an existing set (destination cannot change then).</summary>
public sealed class AddSetViewModel : ObservableObject
{
    private readonly App _app = App.Current;
    private readonly BackupSet? _editing;
    private string _name = "";
    private DriveChoice? _drive;
    private ScheduleChoice _schedule;
    private string _custom = "";
    private bool _keepHistory = true;
    private bool _protect;
    private string _error = "";
    private DestinationState _destState;

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<DriveChoice> Drives { get; } = new();
    public List<ScheduleChoice> Schedules { get; }
    public List<RuleTile> Rules { get; }
    public ObservableCollection<string> CustomPatterns { get; } = new();

    /// <summary>Set by the window from its PasswordBoxes (they cannot be bound).</summary>
    public string Password { get; set; } = "";
    public string PasswordRepeat { get; set; } = "";

    public event Action<bool>? CloseRequested;

    public AddSetViewModel(BackupSet? editing)
    {
        _editing = editing;
        Schedules = new()
        {
            new(RunSchedule.Live, Ui.ScheduleText(RunSchedule.Live)),
            new(RunSchedule.Hourly, Ui.ScheduleText(RunSchedule.Hourly)),
            new(RunSchedule.Daily, Ui.ScheduleText(RunSchedule.Daily)),
            new(RunSchedule.OnPlugIn, Ui.ScheduleText(RunSchedule.OnPlugIn)),
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
        BrowseDestination = new RelayCommand(PickDestination, () => !IsEditing);
        Create = new RelayCommand(Submit);
        Cancel = new RelayCommand(() => CloseRequested?.Invoke(false));
        Folders.CollectionChanged += (_, _) => { Error = ""; SuggestName(); };
    }

    public bool IsEditing => _editing != null;
    public string Title => Loc.T(IsEditing ? "addset.editTitle" : "addset.title");
    public string SubmitText => Loc.T(IsEditing ? "addset.save" : "addset.create");

    public ICommand AddFolder { get; }
    public ICommand RemoveFolder { get; }
    public ICommand AddPattern { get; }
    public ICommand RemovePattern { get; }
    public ICommand RefreshDrives { get; }
    public ICommand BrowseDestination { get; }
    public ICommand Create { get; }
    public ICommand Cancel { get; }

    public string Name { get => _name; set { if (Set(ref _name, value)) Error = ""; } }

    public DriveChoice? SelectedDrive
    {
        get => _drive;
        set
        {
            if (!Set(ref _drive, value)) return;
            Error = "";
            UpdateDestinationState();
        }
    }

    public ScheduleChoice SelectedSchedule { get => _schedule; set => Set(ref _schedule, value); }
    public string CustomPattern { get => _custom; set => Set(ref _custom, value); }
    public bool KeepHistory { get => _keepHistory; set => Set(ref _keepHistory, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool NoDrives => Drives.Count == 0;

    // ---- destination and password ----

    public DestinationState DestState
    {
        get => _destState;
        private set
        {
            if (!Set(ref _destState, value)) return;
            Raise(nameof(ShowProtect));
            Raise(nameof(ShowPasswordFields));
            Raise(nameof(ShowUnlock));
            Raise(nameof(DestinationNote));
            Raise(nameof(HasDestinationNote));
        }
    }

    /// <summary>"Protect with a password" is offered only for a destination used for the first time.</summary>
    public bool ShowProtect => DestState == DestinationState.New;
    public bool Protect
    {
        get => _protect;
        set { if (Set(ref _protect, value)) { Raise(nameof(ShowPasswordFields)); Error = ""; } }
    }
    public bool ShowPasswordFields => DestState == DestinationState.New && Protect;
    public bool ShowUnlock => DestState == DestinationState.Locked;
    public string DestinationNote => DestState switch
    {
        DestinationState.Encrypted => Loc.T("addset.password.unlocked"),
        DestinationState.Plain => Loc.T("addset.password.plain"),
        DestinationState.Locked => Loc.T("addset.password.unlockHint"),
        _ => "",
    };
    public bool HasDestinationNote => DestinationNote.Length > 0;

    private void UpdateDestinationState()
    {
        if (IsEditing || _drive?.Root == null) { DestState = DestinationState.None; return; }
        var store = DriveStore.TryOpen(_drive.Root);
        DestState = store == null ? DestinationState.New
            : store.IsLocked ? DestinationState.Locked
            : store.IsEncrypted ? DestinationState.Encrypted
            : DestinationState.Plain;
    }

    public string RulesSummary => Loc.T("addset.rulesOn", Rules.Count(r => r.IsOn) + CustomPatterns.Count);

    private void LoadDrives()
    {
        var current = _drive?.Root;
        var custom = Drives.Where(d => d.IsFolder).ToList();
        Drives.Clear();
        foreach (var d in App.BackupDriveChoices())
            Drives.Add(new DriveChoice(d.Root, Loc.T("addset.driveChoice", d.DisplayName, Format.Size(d.Free))));
        foreach (var c in custom) Drives.Add(c);
        SelectedDrive = Drives.FirstOrDefault(d => d.Root == current)
                        ?? Drives.FirstOrDefault(d => DriveLocator.Describe(d.Root!)?.HasStore == true)
                        ?? Drives.FirstOrDefault();
        Raise(nameof(NoDrives));
    }

    private void PickDestination()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("addset.pickDestination") };
        if (dlg.ShowDialog() != true) return;
        var root = PathUtil.NormalizeFolder(dlg.FolderName);
        var existing = Drives.FirstOrDefault(d => d.Root != null && PathUtil.Comparer.Equals(d.Root, root));
        if (existing == null)
        {
            existing = new DriveChoice(root, Loc.T("addset.folderChoice", root), IsFolder: true);
            Drives.Add(existing);
        }
        SelectedDrive = existing;
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
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = Loc.T("addset.chooseFolders") };
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
        if (Name.Trim().Length == 0) { Error = Loc.T("addset.err.name"); return; }
        if (Folders.Count == 0) { Error = Loc.T("addset.err.folders"); return; }
        try { BackupService.NormalizeFolders(Folders); }
        catch (ArgumentException ex) { Error = ex.Message; return; }
        var missing = Folders.FirstOrDefault(f => !Directory.Exists(f));
        if (missing != null) { Error = Loc.T("addset.err.missing", missing); return; }
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

        if (SelectedDrive?.Root is not { } root) { Error = Loc.T("addset.err.noDrive"); return; }
        var driveRoot = PathUtil.NormalizeFolder(root);
        var clash = Folders.FirstOrDefault(f => PathUtil.IsUnder(PathUtil.NormalizeFolder(f), driveRoot) || PathUtil.IsUnder(driveRoot, PathUtil.NormalizeFolder(f)));
        if (clash != null) { Error = Loc.T("addset.err.onDrive", clash); return; }

        string? password = null;
        if (ShowPasswordFields)
        {
            if (Password.Length < 8) { Error = Loc.T("addset.password.short"); return; }
            if (Password != PasswordRepeat) { Error = Loc.T("addset.password.mismatch"); return; }
            password = Password;
        }
        else if (ShowUnlock)
        {
            if (Password.Length == 0) { Error = Loc.T("addset.password.unlockHint"); return; }
            password = Password;
        }

        try
        {
            _app.Service.AddSet(Name, Folders, root, SelectedSchedule.Value, rules, CustomPatterns, KeepHistory, password);
            CloseRequested?.Invoke(true);
        }
        catch (DriveLockedException)
        {
            Error = Loc.T("addset.password.wrong");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.T("addset.err.write", SelectedDrive.Text, ex.Message);
        }
    }
}

public sealed record DriveChoice(string? Root, string Text, bool IsFolder = false);

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
    public string Title => Ui.RuleTitle(Rule);
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
