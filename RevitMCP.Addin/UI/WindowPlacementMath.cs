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
