using System.Windows.Input;
using LocalBack.App.Localization;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;

namespace LocalBack.App.ViewModels;

/// <summary>Asks for the password of an encrypted destination.</summary>
public sealed class UnlockViewModel : ObservableObject
{
    private readonly App _app = App.Current;
    private readonly DriveStore _drive;
    private string _error = "";
    private bool _remember = true;

    public string Password { get; set; } = "";
    public event Action<bool>? CloseRequested;

    public UnlockViewModel(DriveStore drive)
    {
        _drive = drive;
        Unlock = new RelayCommand(Submit);
        Cancel = new RelayCommand(() => CloseRequested?.Invoke(false));
    }

    public ICommand Unlock { get; }
    public ICommand Cancel { get; }
    public string Title => Loc.T("unlock.title", BackupService.DriveName(_drive));
    public string Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool Remember { get => _remember; set => Set(ref _remember, value); }

    private void Submit()
    {
        if (Password.Length == 0) return;
        bool ok = Remember ? _app.Service.UnlockDrive(_drive, Password) : _drive.Unlock(Password, remember: false);
        if (!ok)
        {
            Error = Loc.T("unlock.wrong");
            return;
        }
        if (!Remember) _app.Service.RefreshAll();
        CloseRequested?.Invoke(true);
    }
}
