using System.Globalization;

namespace RevitMCP.Addin.Mep;

/// <summary>
/// A point or direction in model coordinates, in millimetres (#106). The pure MEP routing logic works on these so it
/// can be unit-tested without the Revit API; the tools convert to and from Revit's feet at the edge.
/// </summary>
public readonly struct MepPoint : IEquatable<MepPoint>
{
    public MepPoint(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    /// <summary>Length of the projection on the XY plane.</summary>
    public double HorizontalLength => Math.Sqrt(X * X + Y * Y);

    public static MepPoint operator +(MepPoint a, MepPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static MepPoint operator -(MepPoint a, MepPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static MepPoint operator *(MepPoint a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static MepPoint operator /(MepPoint a, double k) => new(a.X / k, a.Y / k, a.Z / k);

    public double Dot(MepPoint other) => X * other.X + Y * other.Y + Z * other.Z;

    public MepPoint Cross(MepPoint other) =>
        new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);

    public double DistanceTo(MepPoint other) => (this - other).Length;

    /// <summary>The unit vector, or the zero vector when this one has no length.</summary>
    public MepPoint Normalize()
    {
        var length = Length;
        return length < 1e-12 ? new MepPoint(0, 0, 0) : this / length;
    }

    /// <summary>Rounded to 0.1 mm, the precision every MEP tool reports.</summary>
    public MepPoint Rounded() => new(MepMath.Round(X), MepMath.Round(Y), MepMath.Round(Z));

    public bool Equals(MepPoint other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
    public override bool Equals(object? obj) => obj is MepPoint other && Equals(other);
    public override int GetHashCode() => (X, Y, Z).GetHashCode();

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", MepMath.Round(X), MepMath.Round(Y), MepMath.Round(Z));
}

/// <summary>Shared numbers and helpers of the MEP routing logic.</summary>
public static class MepMath
{
    /// <summary>Two points closer than this are the same point (mm).</summary>
    public const double CoincidentToleranceMm = 0.5;

    /// <summary>Bends below this deflection are straight joints; also the collinearity tolerance (degrees).</summary>
    public const double StraightToleranceDeg = 1.0;

    /// <summary>A run longer than this is most likely a unit mistake (points in metres or feet read as mm).</summary>
    public const double LongRunWarningMm = 500_000;

    /// <summary>Rounds a length in mm to 0.1 mm.</summary>
    public static double Round(double mm) => Math.Round(mm, 1, MidpointRounding.AwayFromZero);

    /// <summary>Millimetres to metres, rounded to 1 mm (3 decimals).</summary>
    public static double ToMetres(double mm) => Math.Round(mm / 1000.0, 3, MidpointRounding.AwayFromZero);

    public static double RadToDeg(double radians) => radians * 180.0 / Math.PI;

    /// <summary>The angle between two directions in degrees (0 = same direction, 180 = opposite).</summary>
    public static double AngleDeg(MepPoint a, MepPoint b)
    {
        var na = a.Normalize();
        var nb = b.Normalize();
        var cos = na.Dot(nb);
        if (cos > 1) cos = 1;
        if (cos < -1) cos = -1;
        return RadToDeg(Math.Acos(cos));
    }

    /// <summary>The angle of a direction above or below the horizontal plane, in degrees (0 = horizontal, 90 = vertical).</summary>
    public static double ElevationAngleDeg(MepPoint direction)
    {
        var horizontal = direction.HorizontalLength;
        if (horizontal < 1e-12 && Math.Abs(direction.Z) < 1e-12) return 0;
        return RadToDeg(Math.Atan2(Math.Abs(direction.Z), horizontal));
    }

    public static bool IsHorizontal(MepPoint direction) => ElevationAngleDeg(direction) < StraightToleranceDeg;

    public static bool IsVertical(MepPoint direction) => ElevationAngleDeg(direction) > 90 - StraightToleranceDeg;

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>A size label in mm with at most one decimal, invariant culture ("300", "62.5").</summary>
    public static string FormatMm(double mm) => Round(mm).ToString("0.#", CultureInfo.InvariantCulture);
}
