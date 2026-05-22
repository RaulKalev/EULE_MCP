namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Unit conversion helpers for length values.
/// Revit internal units are feet; user-facing outputs use meters.
/// No Revit API dependency — safe to unit-test directly.
/// </summary>
public static class LengthUnitHelper
{
    private const double MetersPerFoot = 0.3048;

    /// <summary>Converts Revit internal feet to meters.</summary>
    public static double ToMeters(double feet) => feet * MetersPerFoot;

    /// <summary>Converts meters to Revit internal feet.</summary>
    public static double ToFeet(double meters) => meters / MetersPerFoot;

    /// <summary>Rounds a meter value (default 2 decimal places).</summary>
    public static double RoundMeters(double meters, int decimals = 2) => Math.Round(meters, decimals);
}
