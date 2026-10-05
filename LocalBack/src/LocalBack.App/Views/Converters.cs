using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LocalBack.App.Views;

public static class Converters
{
    public static readonly IValueConverter Not = new NotConverter();

    /// <summary>true → Collapsed, false → Visible.</summary>
    public static readonly IValueConverter NotVis = new NotVisConverter();

    /// <summary>Percent (0–100) to a star-sized grid length, for proportional bars.</summary>
    public static readonly IValueConverter Star = new StarConverter();

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    }

    private sealed class NotVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class StarConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            new GridLength(Math.Max(0, value is double d ? d : 0), GridUnitType.Star);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
