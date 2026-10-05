using System.Windows;
using System.Windows.Controls;
using LocalBack.App.ViewModels;
using LocalBack.Core.Storage;

namespace LocalBack.App.Views;

public partial class ProtectWindow : DialogWindow
{
    private readonly ProtectViewModel _vm;

    public ProtectWindow(DriveStore drive)
    {
        InitializeComponent();
        _vm = new ProtectViewModel(drive);
        _vm.CloseRequested += ok => DialogResult = ok;
        DataContext = _vm;
        Loaded += (_, _) => PasswordBox1.Focus();
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordBox1.Password;
        _vm.PasswordRepeat = PasswordBox2.Password;
    }
}
