using System.Windows;
using System.Windows.Input;
using LocalBack.App.ViewModels;
using LocalBack.Core.Storage;

namespace LocalBack.App.Views;

public partial class FreeSpaceWindow : DialogWindow
{
    private readonly FreeSpaceViewModel _vm;

    public static readonly DependencyProperty FreePercentProperty =
        DependencyProperty.Register(nameof(FreePercent), typeof(double), typeof(FreeSpaceWindow), new PropertyMetadata(100.0));

    /// <summary>Remaining part of the usage bar (free space plus anything that is not LocalBack's).</summary>
    public double FreePercent
    {
        get => (double)GetValue(FreePercentProperty);
        set => SetValue(FreePercentProperty, value);
    }

    public FreeSpaceWindow(DriveStore drive, bool lowSpace)
    {
        InitializeComponent();
        _vm = new FreeSpaceViewModel(drive, lowSpace);
        _vm.CloseRequested += Close;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FreeSpaceViewModel.CurrentPercent) or nameof(FreeSpaceViewModel.OlderPercent))
                FreePercent = Math.Max(0, 100 - _vm.CurrentPercent - _vm.OlderPercent);
        };
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.LoadAsync();
        Closed += (_, _) => _vm.Cancel();
    }

    private void Option_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlanOption o }) o.IsChecked = true;
    }
}
