using System.Windows;
using LocalBack.App.ViewModels;

namespace LocalBack.App.Views;

/// <summary>Status flyout above the notification area. Closes as soon as it loses focus, like Windows' own flyouts.</summary>
public partial class TrayFlyout : Window
{
    private readonly TrayViewModel _vm;
    private bool _closing;

    public TrayFlyout()
    {
        InitializeComponent();
        _vm = new TrayViewModel();
        _vm.CloseRequested += SafeClose;
        DataContext = _vm;
        Loaded += (_, _) => PlaceNearTray();
        Deactivated += (_, _) => SafeClose();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) SafeClose(); };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => _vm.Dispose();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void PlaceNearTray()
    {
        // Bottom-right of the work area covers the usual taskbar position; other edges are handled by the work area itself.
        var area = SystemParameters.WorkArea;
        var cursor = System.Windows.Forms.Cursor.Position;
        var source = PresentationSource.FromVisual(this);
        double scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double cx = cursor.X * scale, cy = cursor.Y * scale;
        Left = Math.Min(Math.Max(area.Left, cx - Width / 2), area.Right - Width);
        Top = cy < area.Top + area.Height / 2 ? area.Top : area.Bottom - Height;
        if (cx < area.Left || cx > area.Right) Left = cx < area.Left ? area.Left : area.Right - Width;
    }
}
