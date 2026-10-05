using System.Windows;
using System.Windows.Media;
using LocalBack.Core.Service;

namespace LocalBack.App.ViewModels;

/// <summary>Brushes and small helpers shared by the view models.</summary>
public static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Green up to date, amber pending/warning, red failed, blue running.</summary>
    public static Brush Dot(SetHealth h) => h switch
    {
        SetHealth.UpToDate => Brush("Green"),
        SetHealth.Running => Brush("Accent"),
        SetHealth.Error => Brush("Red"),
        SetHealth.NeverRun or SetHealth.Disabled => Brush("BorderStrong"),
        _ => Brush("Amber"),
    };

    public static bool Confirm(string text, string title = "LocalBack", Window? owner = null)
    {
        owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return (owner != null
            ? MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK;
    }

    public static void Info(string text, string title = "LocalBack")
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner != null) MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
