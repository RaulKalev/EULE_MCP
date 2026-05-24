using System.IO;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Delivery;

/// <summary>
/// Parses EULE drawing file names into structured components.
/// Expected format: {projectNumber}_{stage}_{discipline}-{group}-{sequence}_{description}[_{revision}].{ext}
/// Example: 1626_TP_EL-5-01_Valgustuse_plaan.pdf
///          1626_TP_EL-5-01_Valgustuse_plaan_r02.pdf
/// </summary>
public static class DeliveryFileNameParser
{
    // Matches: 1626_TP_EL-5-01_...
    // Group 1: projectNumber (digits)
    // Group 2: stage (2-4 uppercase letters)
    // Group 3: sheetNumber = discipline-group-sequence
    // Group 4: discipline (1-3 uppercase letters)
    // Group 5: group (digits)
    // Group 6: sequence (digits)
    // Group 7: remaining description (may contain underscores)
    private static readonly Regex _pattern = new(
        @"^(\d{3,8})_([A-Z]{2,4})_(([A-Z]{1,3})-(\d{1,3})-(\d{2,4}))(?:_(.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Revision suffix: _r01, _rev1, _R01, etc.
    private static readonly Regex _revisionSuffix = new(
        @"^(.+?)_([Rr][Ee][Vv]?\d{1,3}|[Rr]\d{1,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to parse a file name stem (without extension) into delivery components.
    /// Returns null if the name does not match the expected pattern.
    /// </summary>
    public static ParsedDeliveryFileName? Parse(string fileNameWithExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithExtension)) return null;

        var ext = Path.GetExtension(fileNameWithExtension);
        var stem = Path.GetFileNameWithoutExtension(fileNameWithExtension);

        var match = _pattern.Match(stem);
        if (!match.Success) return null;

        var projectNumber = match.Groups[1].Value;
        var stage = match.Groups[2].Value;
        var sheetNumber = match.Groups[3].Value;
        var discipline = match.Groups[4].Value;
        var group = match.Groups[5].Value;
        var sequence = match.Groups[6].Value;
        var descriptionRaw = match.Groups[7].Value; // may be empty

        // Check if description ends with a revision token
        string? revision = null;
        string description = descriptionRaw;
        if (!string.IsNullOrEmpty(descriptionRaw))
        {
            var revMatch = _revisionSuffix.Match(descriptionRaw);
            if (revMatch.Success)
            {
                description = revMatch.Groups[1].Value;
                revision = revMatch.Groups[2].Value.ToUpperInvariant();
            }
        }

        return new ParsedDeliveryFileName
        {
            ProjectNumber = projectNumber,
            Stage = stage,
            SheetNumber = sheetNumber,
            Discipline = discipline,
            Group = group,
            Sequence = sequence,
            Description = description,
            Revision = revision,
            Extension = ext.TrimStart('.').ToLowerInvariant()
        };
    }

    /// <summary>
    /// Returns true if the file name matches the EULE naming pattern (regardless of whether it parses cleanly).
    /// </summary>
    public static bool LooksLikeEuleDrawing(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return _pattern.IsMatch(stem);
    }
}

public sealed class ParsedDeliveryFileName
{
    /// <summary>E.g. "1626"</summary>
    public string ProjectNumber { get; init; } = string.Empty;

    /// <summary>E.g. "TP", "PP", "EP"</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>Full sheet number segment, e.g. "EL-5-01"</summary>
    public string SheetNumber { get; init; } = string.Empty;

    /// <summary>E.g. "EL", "EN", "AR"</summary>
    public string Discipline { get; init; } = string.Empty;

    /// <summary>E.g. "5"</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>E.g. "01"</summary>
    public string Sequence { get; init; } = string.Empty;

    /// <summary>Human-readable description part, underscores preserved. May be empty.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Revision token if present, e.g. "R02". Null if not present.</summary>
    public string? Revision { get; init; }

    /// <summary>Extension without leading dot, lower-case, e.g. "pdf"</summary>
    public string Extension { get; init; } = string.Empty;
}
