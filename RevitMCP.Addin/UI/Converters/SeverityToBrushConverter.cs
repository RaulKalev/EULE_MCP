using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RevitMCP.Addin.UI.ViewModels;

namespace RevitMCP.Addin.UI.Converters;

/// <summary>
/// <see cref="HeadlineSeverity"/> → the <c>Status.Ok/Info/Warning/Error</c> brush of the palette in effect. A
/// MultiBinding: value 0 is the severity, value 1 the element whose resources are searched (RelativeSource Self).
/// ConverterParameter "Subtle" picks the <c>.Subtle</c> variant. The palette is swapped at runtime, so the window
/// refreshes these bindings when <see cref="Themes.ThemeManager.Applied"/> fires.
/// </summary>
public class SeverityToBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var severity = values.Length > 0 && values[0] is HeadlineSeverity s ? s : HeadlineSeverity.Ok;
        var element = values.Length > 1 ? values[1] as FrameworkElement : null;
        var key = "Status." + severity + (parameter as string == "Subtle" ? ".Subtle" : string.Empty);
        return element?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
