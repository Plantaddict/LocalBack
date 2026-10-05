using System.Windows;
using System.Windows.Controls;
using LocalBack.App.ViewModels;
using LocalBack.Core.Storage;

namespace LocalBack.App.Views;

public partial class UnlockWindow : DialogWindow
{
    private readonly UnlockViewModel _vm;

    public UnlockWindow(DriveStore drive)
    {
        InitializeComponent();
        _vm = new UnlockViewModel(drive);
        _vm.CloseRequested += ok => DialogResult = ok;
        DataContext = _vm;
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => _vm.Password = ((PasswordBox)sender).Password;
}
