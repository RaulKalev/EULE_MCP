namespace RevitMCP.Addin.Annotation;

/// <summary>Angles of text notes and other view-plane elements (rotation and fit-width fixes). Pure, unit tested.</summary>
public static class TextRotationMath
{
    /// <summary>Degrees in [0, 360).</summary>
    public static double Normalize(double degrees)
    {
        var d = degrees % 360.0;
        if (d < 0) d += 360.0;
        return Math.Abs(d - 360.0) < 1e-9 ? 0 : d;
    }

    /// <summary>
    /// The angle of a direction in a view, counter-clockwise from the view's right: <paramref name="alongRight"/> and
    /// <paramref name="alongUp"/> are the direction's dot products with the view's right and up directions.
    /// </summary>
    public static double AngleInViewDeg(double alongRight, double alongUp)
    {
        if (Math.Abs(alongRight) < 1e-12 && Math.Abs(alongUp) < 1e-12) return 0;
        return Normalize(Math.Atan2(alongUp, alongRight) * 180.0 / Math.PI);
    }

    /// <summary>The shortest signed turn from <paramref name="current"/> to <paramref name="target"/>, in (-180, 180].</summary>
    public static double TurnDeg(double current, double target)
    {
        var d = Normalize(target - current);
        return d > 180.0 ? d - 360.0 : d;
    }
}

/// <summary>Text note widths are on paper; agents often pass model millimetres instead.</summary>
public static class TextWidthUnits
{
    /// <summary>Wider than an A0 sheet's long side is never a sensible paper width for a text box.</summary>
    public const double MaxSensiblePaperWidthMm = 1189;

    /// <summary>True when a paper width only makes sense as a model width (larger than any sheet), in a scaled view.</summary>
    public static bool LooksLikeModelWidth(double widthMm, int viewScale) =>
        viewScale > 1 && widthMm > MaxSensiblePaperWidthMm / 4 && widthMm / viewScale >= 5;
}

/// <summary>
/// Finds the narrowest text note width that keeps the layout Revit gives at an unlimited width (#fit-width). Revit lays
/// the note out; <c>heightAt(width)</c> sets the width, regenerates and returns the note's height, which grows by whole
/// lines as the note wraps. Pure: the search is unit tested with a fake layout.
/// </summary>
public static class TextFitWidthSearch
{
    /// <param name="minWidth">Revit's minimum allowed width.</param>
    /// <param name="maxWidth">Revit's maximum allowed width.</param>
    /// <param name="hint">An estimate of the fitting width (font measurement); speeds the search up. 0 = none.</param>
    /// <param name="heightAt">Sets the width, lets Revit lay the note out and returns its height.</param>
    /// <param name="tolerance">Width resolution of the answer (same unit as the widths).</param>
    /// <returns>The fitting width, and how many layouts Revit had to make.</returns>
    public static (double Width, int Layouts) Find(double minWidth, double maxWidth, double hint, Func<double, double> heightAt, double tolerance)
    {
        if (maxWidth < minWidth) maxWidth = minWidth;
        var layouts = 0;
        double Height(double w) { layouts++; return heightAt(w); }

        var unwrapped = Height(maxWidth);
        var heightTolerance = Math.Max(Math.Abs(unwrapped) * 1e-4, 1e-9);
        bool Fits(double w) => Height(w) <= unwrapped + heightTolerance;

        var lo = minWidth;
        var hi = maxWidth;
        if (Fits(lo)) return (lo, layouts);

        if (hint > lo && hint < hi)
        {
            // Bracket around the estimate first: usually a few layouts instead of a search over the whole range.
            var upper = Math.Min(hi, hint * 1.15 + tolerance);
            if (Fits(upper))
            {
                hi = upper;
                var lower = Math.Max(lo, hint * 0.85);
                if (lower > lo && !Fits(lower)) lo = lower;
            }
            else
            {
                lo = upper;
            }
        }

        while (hi - lo > tolerance)
        {
            var mid = (lo + hi) / 2.0;
            if (Fits(mid)) hi = mid;
            else lo = mid;
        }
        return (hi, layouts);
    }
}
