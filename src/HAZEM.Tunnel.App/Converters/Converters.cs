using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HAZEM.Tunnel.App.Converters;

/// <summary>bool → Visibility (Visible when true).</summary>
public sealed class VisibilityFromBool : IValueConverter
{
    public static readonly VisibilityFromBool Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool → brush (green when valid, red when not).</summary>
public sealed class BoolToColor : IValueConverter
{
    public static readonly BoolToColor Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
            : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
