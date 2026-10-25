using System.Globalization;

namespace RevitMCP.Addin.Schedules;

/// <summary>A rectangle on a sheet in paper mm (sheet coordinates, Y up).</summary>
public readonly struct SheetRect
{
    public SheetRect(double minX, double minY, double maxX, double maxY)
    {
        MinX = Math.Min(minX, maxX);
        MinY = Math.Min(minY, maxY);
        MaxX = Math.Max(minX, maxX);
        MaxY = Math.Max(minY, maxY);
    }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public SheetRect Offset(double dx, double dy) => new(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);

    /// <summary>True when the two overlap by more than <paramref name="toleranceMm"/> in both directions.</summary>
    public bool Overlaps(SheetRect other, double toleranceMm = 0.5) =>
        Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX) > toleranceMm &&
        Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY) > toleranceMm;

    public bool Inside(SheetRect outer, double toleranceMm = 0.5) =>
        MinX >= outer.MinX - toleranceMm && MinY >= outer.MinY - toleranceMm &&
        MaxX <= outer.MaxX + toleranceMm && MaxY <= outer.MaxY + toleranceMm;

    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "({0:0.#}, {1:0.#})–({2:0.#}, {3:0.#}) mm", MinX, MinY, MaxX, MaxY);
}

/// <summary>
/// Where a schedule goes on a sheet (#103). A schedule's insertion point is the top-left corner of its table, so the
/// placement is planned as a top-left point; after Revit creates the instance the add-in measures its real box and
/// moves it by <see cref="Correction"/>, so the result does not depend on where Revit anchors the table. Pure.
/// </summary>
public static class SchedulePlacement
{
    public const double DefaultGapMm = 10;

    public const string Right = "right";
    public const string Below = "below";
    public const string Left = "left";
    public const string Above = "above";

    public static bool TryParseSide(string? raw, out string side)
    {
        side = (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "right" or "r" or "east" => Right,
            "below" or "under" or "bottom" or "down" or "b" or "south" => Below,
            "left" or "l" or "west" => Left,
            "above" or "over" or "top" or "up" or "north" => Above,
            _ => string.Empty
        };
        return side.Length > 0;
    }

    /// <summary>
    /// The top-left point of a schedule of the given size placed next to <paramref name="anchor"/> (a viewport box):
    /// right of it with tops aligned, below it with left edges aligned, left of it with tops aligned, or above it with
    /// left edges aligned, <paramref name="gapMm"/> apart.
    /// </summary>
    public static (double X, double Y) NextTo(SheetRect anchor, string side, double gapMm, double width, double height)
    {
        var gap = Math.Max(0, gapMm);
        return side switch
        {
            Below => (anchor.MinX, anchor.MinY - gap),
            Left => (anchor.MinX - gap - Math.Max(0, width), anchor.MaxY),
            Above => (anchor.MinX, anchor.MaxY + gap + Math.Max(0, height)),
            _ => (anchor.MaxX + gap, anchor.MaxY)
        };
    }

    /// <summary>The top-left point that centres a box of the given size on <paramref name="center"/>.</summary>
    public static (double X, double Y) CenteredTopLeft((double X, double Y) center, double width, double height)
        => (center.X - width / 2, center.Y + height / 2);

    /// <summary>How far to move a placed box so its top-left corner lands on <paramref name="desiredTopLeft"/>.</summary>
    public static (double Dx, double Dy) Correction((double X, double Y) desiredTopLeft, SheetRect actual)
        => (desiredTopLeft.X - actual.MinX, desiredTopLeft.Y - actual.MaxY);

    /// <summary>The box of a schedule whose top-left corner is at <paramref name="topLeft"/>.</summary>
    public static SheetRect BoxAt((double X, double Y) topLeft, double width, double height)
        => new(topLeft.X, topLeft.Y - height, topLeft.X + width, topLeft.Y);

    /// <summary>Indexes of the rectangles <paramref name="placed"/> overlaps.</summary>
    public static List<int> Overlapping(SheetRect placed, IReadOnlyList<SheetRect> others, double toleranceMm = 0.5)
    {
        var result = new List<int>();
        for (var i = 0; i < others.Count; i++)
            if (placed.Overlaps(others[i], toleranceMm)) result.Add(i);
        return result;
    }
}
