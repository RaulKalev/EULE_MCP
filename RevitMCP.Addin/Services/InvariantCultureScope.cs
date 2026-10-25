using System.Globalization;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Formats numbers the same way on every machine while a tool runs: "45.21 m", not the Estonian "45,21 m", and
/// XYZ.ToString() as "(16.91, 78.62, 3.94)" instead of the unparseable "(16,91, 78,62, 3,94)" (X1). Only the
/// formatting culture changes; the UI culture (Revit's language) is left alone. Restores the previous culture on
/// dispose. Pure.
/// </summary>
public sealed class InvariantCultureScope : IDisposable
{
    private readonly CultureInfo _previous;

    public InvariantCultureScope()
    {
        _previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    public void Dispose() => CultureInfo.CurrentCulture = _previous;
}
