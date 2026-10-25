namespace RevitMCP.Addin.UI;

/// <summary>
/// Pure window-bounds normalization used before restoring persisted WPF geometry.
/// Keeping this independent of WPF makes the monitor-safety rules testable.
/// </summary>
public static class WindowPlacementMath
{
    public static WindowPlacement Normalize(
        WindowPlacement saved,
        WindowPlacement availableArea,
        double minimumWidth,
        double minimumHeight,
        double defaultWidth,
        double defaultHeight)
    {
        if (!IsValidArea(availableArea))
            return new WindowPlacement(0, 0, defaultWidth, defaultHeight);

        var width = IsPositiveFinite(saved.Width) ? saved.Width : defaultWidth;
        var height = IsPositiveFinite(saved.Height) ? saved.Height : defaultHeight;

        width = Clamp(width, Math.Min(minimumWidth, availableArea.Width), availableArea.Width);
        height = Clamp(height, Math.Min(minimumHeight, availableArea.Height), availableArea.Height);

        var centeredLeft = availableArea.Left + ((availableArea.Width - width) / 2);
        var centeredTop = availableArea.Top + ((availableArea.Height - height) / 2);
        var left = IsFinite(saved.Left) ? saved.Left : centeredLeft;
        var top = IsFinite(saved.Top) ? saved.Top : centeredTop;

        left = Clamp(left, availableArea.Left, availableArea.Left + availableArea.Width - width);
        top = Clamp(top, availableArea.Top, availableArea.Top + availableArea.Height - height);

        return new WindowPlacement(left, top, width, height);
    }

    /// <summary>
    /// The collapsed status strip: <paramref name="width"/> × <paramref name="height"/> at the normal window's
    /// bottom-left corner, so the window shrinks down onto its bottom edge (the user's call, 2026-10-25), moved only as
    /// far as needed to stay inside <paramref name="availableArea"/>. Also used for the taller peek, which therefore
    /// grows upward from the strip.
    /// </summary>
    public static WindowPlacement Compact(
        WindowPlacement normal,
        WindowPlacement availableArea,
        double width,
        double height)
    {
        var bottomTop = IsFinite(normal.Top) && IsFinite(normal.Height) ? normal.Top + normal.Height - height : double.NaN;
        if (!IsValidArea(availableArea))
            return new WindowPlacement(IsFinite(normal.Left) ? normal.Left : 0, IsFinite(bottomTop) ? bottomTop : 0, width, height);

        width = Math.Min(width, availableArea.Width);
        height = Math.Min(height, availableArea.Height);
        var left = IsFinite(normal.Left) ? normal.Left : availableArea.Left;
        var top = IsFinite(bottomTop) ? bottomTop : availableArea.Top + availableArea.Height - height;
        left = Clamp(left, availableArea.Left, availableArea.Left + availableArea.Width - width);
        top = Clamp(top, availableArea.Top, availableArea.Top + availableArea.Height - height);
        return new WindowPlacement(left, top, width, height);
    }

    /// <summary>
    /// The full window a collapsed strip expands back to: <paramref name="normalWidth"/> × <paramref name="normalHeight"/>
    /// standing on the strip's bottom edge (so it follows the strip if that was dragged), then kept inside
    /// <paramref name="availableArea"/>.
    /// </summary>
    public static WindowPlacement Expanded(
        WindowPlacement strip,
        WindowPlacement availableArea,
        double normalWidth,
        double normalHeight,
        double minimumWidth,
        double minimumHeight)
        => Normalize(
            new WindowPlacement(strip.Left, strip.Top + strip.Height - normalHeight, normalWidth, normalHeight),
            availableArea, minimumWidth, minimumHeight, normalWidth, normalHeight);

    private static bool IsValidArea(WindowPlacement area) =>
        IsFinite(area.Left)
        && IsFinite(area.Top)
        && IsPositiveFinite(area.Width)
        && IsPositiveFinite(area.Height);

    private static bool IsPositiveFinite(double value) => IsFinite(value) && value > 0;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Max(minimum, Math.Min(value, maximum));
}

public sealed class WindowPlacement
{
    public WindowPlacement(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }
}
