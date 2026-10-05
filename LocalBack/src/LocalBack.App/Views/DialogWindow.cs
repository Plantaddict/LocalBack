using System.Windows;
using System.Windows.Input;

namespace LocalBack.App.Views;

/// <summary>Borderless rounded dialog, draggable anywhere on its background, Escape closes.</summary>
public class DialogWindow : Window
{
    public DialogWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont");
        FontSize = 14;
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text");
        MouseLeftButtonDown += (_, e) =>
        {
            // Drag by the header area only, so clicks on options and fields below behave normally.
            if (e.ButtonState == MouseButtonState.Pressed && e.GetPosition(this).Y < 72 &&
                e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }
}
