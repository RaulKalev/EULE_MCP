using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Annotation;

/// <summary>How the content of an existing text note is changed.</summary>
public enum TextNoteEditMode
{
    /// <summary>No text change: only the width options are applied.</summary>
    None,
    Set,
    FindReplace,
    Append,
    Prepend,
    Map
}

/// <summary>Validated arguments of revit_preview_set_text_notes / revit_set_text_notes.</summary>
public sealed class TextNoteEditOptions
{
    public TextNoteEditMode Mode { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Find { get; set; } = string.Empty;
    public string Replace { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }

    /// <summary>Map mode: normalised, trimmed old text → new text.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Map { get; set; } = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Explicit width on paper in millimetres; 0 = leave the width alone.</summary>
    public double WidthMm { get; set; }

    /// <summary>Widen notes so the longest line fits (estimate; never narrows a note).</summary>
    public bool AutoWidth { get; set; }

    public bool ChangesWidth => WidthMm > 0 || AutoWidth;
}

/// <summary>One splice of the original text: replace [Start, Start+Length) with Replacement.</summary>
public sealed class TextSplice
{
    public TextSplice(int start, int length, string replacement)
    {
        Start = start;
        Length = length;
        Replacement = replacement;
    }

    public int Start { get; }
    public int Length { get; }
    public string Replacement { get; }
}

/// <summary>The planned text change for one note.</summary>
public sealed class TextNoteEditPlan
{
    public string OldText { get; set; } = string.Empty;
    public string NewText { get; set; } = string.Empty;

    /// <summary>Splices relative to <see cref="OldText"/>, ascending and non-overlapping.</summary>
    public IReadOnlyList<TextSplice> Splices { get; set; } = Array.Empty<TextSplice>();

    public bool TextChanged => !string.Equals(OldText, NewText, StringComparison.Ordinal);

    /// <summary>Why the text stays as it is (null when it changes).</summary>
    public string? UnchangedReason { get; set; }

    /// <summary>Set when the edit is refused for this note (e.g. the result would be empty).</summary>
    public string? SkipReason { get; set; }
}

/// <summary>
/// Pure, Revit-free planning for editing existing text notes: argument validation, the five
/// text modes, case / whole-word matching, map lookup and a width estimate. The tools apply the
/// resulting splices to the note's FormattedText so character formatting survives where Revit
/// allows it. Revit stores paragraph breaks as '\r'; every user-supplied string is normalised to
/// that, so "\n" in arguments behaves as a line break.
/// </summary>
public static class TextNoteEditPlanner
{
    /// <summary>Average glyph advance as a fraction of the text height (Arial-like fonts).</summary>
    public const double AverageCharWidthRatio = 0.62;

    public static TextNoteEditMode? NormalizeMode(string? raw)
    {
        var key = (raw ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        switch (key)
        {
            case "": return TextNoteEditMode.None;
            case "set": case "replaceall": return TextNoteEditMode.Set;
            case "findreplace": case "replace": return TextNoteEditMode.FindReplace;
            case "append": case "suffix": return TextNoteEditMode.Append;
            case "prepend": case "prefix": return TextNoteEditMode.Prepend;
            case "map": return TextNoteEditMode.Map;
            default: return null;
        }
    }

    /// <summary>Converts "\r\n" and "\n" to Revit's paragraph break "\r".</summary>
    public static string NormalizeLineBreaks(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\r").Replace("\n", "\r");

    /// <summary>
    /// Validates the arguments. <paramref name="rawMap"/> may be a JObject, a JSON string or an
    /// IDictionary&lt;string,string&gt;. Returns null with <paramref name="error"/> set when invalid.
    /// </summary>
    public static TextNoteEditOptions? ParseOptions(
        string? mode,
        string? text,
        string? find,
        string? replace,
        string? prefix,
        string? suffix,
        bool matchCase,
        bool wholeWord,
        object? rawMap,
        double widthMm,
        bool autoWidth,
        out string? error)
    {
        error = null;
        var parsedMode = NormalizeMode(mode);
        if (parsedMode == null)
        {
            error = $"Unknown mode '{mode}'. Use set, findReplace, append, prepend or map.";
            return null;
        }

        if (widthMm < 0 || double.IsNaN(widthMm) || double.IsInfinity(widthMm))
        {
            error = "widthMm must be a positive number of millimetres (or 0 to leave the width alone).";
            return null;
        }

        if (widthMm > 0 && autoWidth)
        {
            error = "Pass either widthMm or autoWidth=true, not both.";
            return null;
        }

        var options = new TextNoteEditOptions
        {
            Mode = parsedMode.Value,
            Text = NormalizeLineBreaks(text),
            Find = NormalizeLineBreaks(find),
            Replace = NormalizeLineBreaks(replace),
            Prefix = NormalizeLineBreaks(prefix),
            Suffix = NormalizeLineBreaks(suffix),
            MatchCase = matchCase,
            WholeWord = wholeWord,
            WidthMm = widthMm,
            AutoWidth = autoWidth
        };

        switch (options.Mode)
        {
            case TextNoteEditMode.None:
                if (!options.ChangesWidth)
                {
                    error = "Provide mode (set, findReplace, append, prepend or map), or widthMm / autoWidth to change only the width.";
                    return null;
                }
                break;
            case TextNoteEditMode.Set:
                if (options.Text.Trim().Length == 0)
                {
                    error = "mode=set needs a non-empty 'text'.";
                    return null;
                }
                break;
            case TextNoteEditMode.FindReplace:
                if (options.Find.Length == 0)
                {
                    error = "mode=findReplace needs a non-empty 'find'.";
                    return null;
                }
                break;
            case TextNoteEditMode.Append:
                if (options.Suffix.Length == 0)
                {
                    error = "mode=append needs a non-empty 'suffix'.";
                    return null;
                }
                break;
            case TextNoteEditMode.Prepend:
                if (options.Prefix.Length == 0)
                {
                    error = "mode=prepend needs a non-empty 'prefix'.";
                    return null;
                }
                break;
            case TextNoteEditMode.Map:
                var map = ParseMap(rawMap, matchCase, out error);
                if (map == null) return null;
                options.Map = map;
                break;
        }

        return options;
    }

    /// <summary>Parses and validates the map (old text → new text). Keys are trimmed and normalised.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>>? ParseMap(object? rawMap, bool matchCase, out string? error)
    {
        error = null;
        var pairs = new List<KeyValuePair<string, string>>();

        switch (rawMap)
        {
            case null:
                break;
            case JObject obj:
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value.Type != JTokenType.String)
                    {
                        error = $"map value for '{prop.Name}' must be a string.";
                        return null;
                    }
                    pairs.Add(new KeyValuePair<string, string>(prop.Name, prop.Value.Value<string>() ?? string.Empty));
                }
                break;
            case string s when s.Trim().Length > 0:
                JObject? parsed;
                try { parsed = JToken.Parse(s) as JObject; }
                catch (Exception ex)
                {
                    error = $"map is not valid JSON: {ex.Message}";
                    return null;
                }
                if (parsed == null)
                {
                    error = "map must be a JSON object: {\"old text\": \"new text\", ...}.";
                    return null;
                }
                return ParseMap(parsed, matchCase, out error);
            case string:
                break;
            case IEnumerable<KeyValuePair<string, string>> dict:
                pairs.AddRange(dict);
                break;
            default:
                error = "map must be a JSON object: {\"old text\": \"new text\", ...}.";
                return null;
        }

        if (pairs.Count == 0)
        {
            error = "mode=map needs a non-empty 'map' JSON object: {\"old text\": \"new text\", ...}.";
            return null;
        }

        var comparer = matchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(comparer);
        var result = new List<KeyValuePair<string, string>>(pairs.Count);
        foreach (var pair in pairs)
        {
            var key = NormalizeLineBreaks(pair.Key).Trim();
            var value = NormalizeLineBreaks(pair.Value);
            if (key.Length == 0)
            {
                error = "map has an empty key.";
                return null;
            }
            if (value.Trim().Length == 0)
            {
                error = $"map value for '{key}' is empty; text notes cannot be empty.";
                return null;
            }
            if (!seen.Add(key))
            {
                error = $"map has the key '{key}' more than once{(matchCase ? string.Empty : " (keys are compared case-insensitively unless matchCase=true)")}.";
                return null;
            }
            result.Add(new KeyValuePair<string, string>(key, value));
        }

        return result;
    }

    /// <summary>Case-insensitive substring filter, the same rule revit_get_text_notes uses. Empty matches all.</summary>
    public static bool MatchesFilter(string? text, string? filter) =>
        string.IsNullOrEmpty(filter) || (text ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Plans the text change for one note.</summary>
    public static TextNoteEditPlan Plan(string? currentText, TextNoteEditOptions options)
    {
        var old = currentText ?? string.Empty;
        var plan = new TextNoteEditPlan { OldText = old, NewText = old };
        var splices = new List<TextSplice>();
        var contentEnd = ContentEnd(old);

        switch (options.Mode)
        {
            case TextNoteEditMode.None:
                plan.UnchangedReason = "text not edited (width only)";
                return plan;

            case TextNoteEditMode.Set:
                splices.Add(new TextSplice(0, contentEnd, options.Text));
                break;

            case TextNoteEditMode.Append:
                splices.Add(new TextSplice(contentEnd, 0, options.Suffix));
                break;

            case TextNoteEditMode.Prepend:
                splices.Add(new TextSplice(0, 0, options.Prefix));
                break;

            case TextNoteEditMode.FindReplace:
                foreach (var start in FindAll(old, options.Find, options.MatchCase, options.WholeWord))
                    splices.Add(new TextSplice(start, options.Find.Length, options.Replace));
                if (splices.Count == 0)
                {
                    plan.UnchangedReason = "find text not present";
                    return plan;
                }
                break;

            case TextNoteEditMode.Map:
                var (coreStart, coreLength) = CoreSpan(old);
                var key = NormalizeLineBreaks(old.Substring(coreStart, coreLength));
                var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                string? value = null;
                foreach (var pair in options.Map)
                {
                    if (string.Equals(pair.Key, key, comparison)) { value = pair.Value; break; }
                }
                if (value == null)
                {
                    plan.UnchangedReason = "text not in map";
                    return plan;
                }
                splices.Add(new TextSplice(coreStart, coreLength, value));
                break;
        }

        plan.Splices = splices;
        plan.NewText = ApplySplices(old, splices);

        if (!plan.TextChanged)
        {
            plan.UnchangedReason = "text already as requested";
            plan.Splices = Array.Empty<TextSplice>();
        }
        else if (plan.NewText.Trim().Length == 0)
        {
            plan.SkipReason = "the edit would leave the text note empty";
        }

        return plan;
    }

    /// <summary>Applies ascending, non-overlapping splices to <paramref name="text"/>.</summary>
    public static string ApplySplices(string text, IReadOnlyList<TextSplice> splices)
    {
        var sb = new System.Text.StringBuilder(text.Length + 16);
        var cursor = 0;
        foreach (var splice in splices)
        {
            sb.Append(text, cursor, splice.Start - cursor);
            sb.Append(splice.Replacement);
            cursor = splice.Start + splice.Length;
        }
        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    /// <summary>Start indexes of non-overlapping matches, scanning left to right.</summary>
    public static List<int> FindAll(string text, string find, bool matchCase, bool wholeWord)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(find) || string.IsNullOrEmpty(text)) return result;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while (index <= text.Length - find.Length)
        {
            var hit = text.IndexOf(find, index, comparison);
            if (hit < 0) break;
            if (!wholeWord || IsWholeWord(text, hit, find.Length))
            {
                result.Add(hit);
                index = hit + find.Length;
            }
            else
            {
                index = hit + 1;
            }
        }
        return result;
    }

    private static bool IsWholeWord(string text, int start, int length)
    {
        var end = start + length;
        var beforeOk = start == 0 || !IsWordChar(text[start - 1]) || !IsWordChar(text[start]);
        var afterOk = end >= text.Length || !IsWordChar(text[end]) || !IsWordChar(text[end - 1]);
        return beforeOk && afterOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Length of the text without its trailing paragraph breaks (Revit ends notes with '\r').</summary>
    public static int ContentEnd(string text)
    {
        var end = text.Length;
        while (end > 0 && (text[end - 1] == '\r' || text[end - 1] == '\n')) end--;
        return end;
    }

    /// <summary>The text without leading/trailing whitespace, as (start, length).</summary>
    public static (int Start, int Length) CoreSpan(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end - start);
    }

    /// <summary>Character count of the longest line (paragraph breaks '\r' or '\n').</summary>
    public static int LongestLineLength(string? text)
    {
        var longest = 0;
        foreach (var line in NormalizeLineBreaks(text).Split('\r'))
            longest = Math.Max(longest, line.TrimEnd().Length);
        return longest;
    }

    /// <summary>
    /// Estimated paper width (mm) that fits the longest line on one row: characters × text height ×
    /// the type's width factor × <see cref="AverageCharWidthRatio"/>, plus one character of slack,
    /// rounded up to 0.5 mm. An estimate only — real glyph widths vary (capitals, bold, W/M are wider).
    /// </summary>
    public static double EstimateWidthMm(string? text, double textSizeMm, double widthFactor)
    {
        if (textSizeMm <= 0) return 0;
        if (widthFactor <= 0) widthFactor = 1.0;
        var chars = LongestLineLength(text);
        if (chars == 0) return 0;
        var raw = (chars + 1) * textSizeMm * widthFactor * AverageCharWidthRatio;
        return Math.Ceiling(raw * 2.0 - 1e-9) / 2.0;
    }
}
