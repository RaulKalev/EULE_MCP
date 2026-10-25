using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RevitMCP.Addin.UI.Converters;

/// <summary>True → Visible, false → Collapsed; <see cref="Invert"/> swaps them.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is Visibility.Visible) != Invert;
}
