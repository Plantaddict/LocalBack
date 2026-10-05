using System.Windows.Input;
using LocalBack.App.Localization;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;

namespace LocalBack.App.ViewModels;

/// <summary>Adds a password to a destination that has been used without one.</summary>
public sealed class ProtectViewModel : ObservableObject
{
    private readonly App _app = App.Current;
    private readonly DriveStore _drive;
    private string _error = "";

    public string Password { get; set; } = "";
    public string PasswordRepeat { get; set; } = "";
    public event Action<bool>? CloseRequested;

    public ProtectViewModel(DriveStore drive)
    {
        _drive = drive;
        Protect = new RelayCommand(Submit);
        Cancel = new RelayCommand(() => CloseRequested?.Invoke(false));
    }

    public ICommand Protect { get; }
    public ICommand Cancel { get; }
    public string Title => Loc.T("protect.title", BackupService.DriveName(_drive));
    public string Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;

    private void Submit()
    {
        if (Password.Length < 8) { Error = Loc.T("protect.short"); return; }
        if (Password != PasswordRepeat) { Error = Loc.T("protect.mismatch"); return; }
        try
        {
            _app.Service.ProtectDrive(_drive, Password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
            return;
        }
        CloseRequested?.Invoke(true);
    }
}
