using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LocalBack.App.Views;

public partial class SetsView : UserControl
{
    public SetsView() => InitializeComponent();

    /// <summary>The "⋯" button opens the row's context menu.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement b) return;
        DependencyObject? d = b;
        while (d != null && !(d is Border border && border.ContextMenu != null)) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is Border { ContextMenu: { } menu })
        {
            menu.PlacementTarget = b;
            menu.Placement = PlacementMode.Bottom;
            menu.DataContext = b.DataContext;
            menu.IsOpen = true;
        }
    }
}
