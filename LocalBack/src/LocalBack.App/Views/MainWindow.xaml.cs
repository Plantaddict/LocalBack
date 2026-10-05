using System.Windows;
using LocalBack.App.ViewModels;

namespace LocalBack.App.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        DataContext = ViewModel;
        StateChanged += (_, _) =>
        {
            // A maximised chrome-less window overhangs the screen by the resize border; pad it back in.
            RootFrame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            RootFrame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        };
        Closed += (_, _) => ViewModel.Dispose();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Closing the window keeps LocalBack running in the tray.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
