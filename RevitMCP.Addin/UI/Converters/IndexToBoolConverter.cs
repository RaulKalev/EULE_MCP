using System.Globalization;
using System.Windows.Data;

namespace RevitMCP.Addin.UI.Converters;

/// <summary>
/// Binds a segment (radio button) to a selected index: checked when the index equals the parameter, and checking
/// it selects that index.
/// </summary>
public class IndexToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int index && index.ToString(CultureInfo.InvariantCulture) == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : Binding.DoNothing;
}
