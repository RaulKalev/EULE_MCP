using System.Globalization;

namespace RevitMCP.Addin.Checkpoints;

/// <summary>Which text note property a pseudo-parameter restores.</summary>
public enum TextNoteField
{
    Text,
    Width
}

/// <summary>
/// A text note's text and width are no parameters, so checkpoints (#98, H11) keep them as pseudo-parameters
/// "(Text)" and "(Width)" next to the real ones: they diff, report and roll back like any parameter value. The
/// display value of the width is in millimetres (paper), its raw value TextNote.Width in feet. Pure — no Revit API.
/// </summary>
public static class TextNoteSnapshot
{
    public const string TextParameter = "(Text)";
    public const string WidthParameter = "(Width)";
    private const double MmPerFoot = 304.8;

    public static bool IsPseudoParameter(string? name)
        => string.Equals(name, TextParameter, StringComparison.Ordinal) || string.Equals(name, WidthParameter, StringComparison.Ordinal);

    /// <summary>Adds the text and the width (in feet; null when unreadable) to a snapshot.</summary>
    public static void Add(ElementSnapshot snapshot, string? text, double? widthFeet)
    {
        if (text != null)
        {
            snapshot.Parameters[TextParameter] = text;
            snapshot.Raw[TextParameter] = RawParameterValue.String(text);
        }
        if (widthFeet is double w)
        {
            snapshot.Parameters[WidthParameter] = WidthDisplay(w);
            snapshot.Raw[WidthParameter] = RawParameterValue.Double(w);
        }
    }

    /// <summary>"51.9 mm": one decimal, so a sub-0.05 mm round-off does not show as a change.</summary>
    public static string WidthDisplay(double widthFeet)
        => Math.Round(widthFeet * MmPerFoot, 1).ToString("0.0", CultureInfo.InvariantCulture) + " mm";

    /// <summary>
    /// What restoring a pseudo-parameter sets: the text (kind S) or the width in feet (kind D). False with
    /// <paramref name="error"/> for a real parameter (error null) or a stored value of the wrong kind.
    /// </summary>
    public static bool TryParseRestore(string parameter, string raw, out TextNoteField field, out string text, out double widthFeet, out string? error)
    {
        field = TextNoteField.Text;
        text = string.Empty;
        widthFeet = 0;
        error = null;
        if (!IsPseudoParameter(parameter)) return false;
        if (!RawParameterValue.TryParse(raw, out var kind, out var value))
        {
            error = "no stored value";
            return false;
        }
        if (parameter == TextParameter)
        {
            if (kind != 'S')
            {
                error = "the stored text is not a string";
                return false;
            }
            text = value;
            return true;
        }
        field = TextNoteField.Width;
        if (kind != 'D' || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out widthFeet) || widthFeet <= 0)
        {
            error = "the stored width is not a positive length";
            return false;
        }
        return true;
    }
}
