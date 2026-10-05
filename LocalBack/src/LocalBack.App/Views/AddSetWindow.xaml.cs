using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalBack.App.ViewModels;
using LocalBack.Core.Model;

namespace LocalBack.App.Views;

public partial class AddSetWindow : DialogWindow
{
    private readonly AddSetViewModel _vm;
    private bool _loaded;

    public AddSetWindow(BackupSet? editing)
    {
        InitializeComponent();
        _vm = new AddSetViewModel(editing);
        _vm.CloseRequested += ok =>
        {
            DialogResult = ok;
        };
        DataContext = _vm;
        Loaded += (_, _) =>
        {
            _loaded = true;
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Once the user types a name, stop suggesting one from the first folder.</summary>
    private void NameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_loaded) _vm.NameEdited();
    }

    /// <summary>Enter in the pattern box adds the pattern instead of submitting the dialog.</summary>
    private void Pattern_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.AddPattern.Execute(null);
        e.Handled = true;
    }

    /// <summary>PasswordBoxes cannot be bound; hand their text to the view model as it changes.</summary>
    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box == PasswordBox2) _vm.PasswordRepeat = box.Password;
        else _vm.Password = box.Password;
    }
}
