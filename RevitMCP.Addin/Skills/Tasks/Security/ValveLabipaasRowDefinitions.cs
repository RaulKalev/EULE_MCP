using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Skills.Tasks.Security;

/// <summary>Source that determines how the model quantity is derived for a spec row.</summary>
public enum ValveLabipaasRowSource
{
    /// <summary>Detail items placed on the "SHS/LPS struktuur" drafting view.</summary>
    DetailItem,

    /// <summary>FamilyInstances placed in the 3-D model.</summary>
    PlacedElement,

    /// <summary>Not modeled; carry spec quantity unchanged (e.g. remote controls, access cards).</summary>
    ProcurementOnly,

    /// <summary>Sum of electrical circuit lengths for circuits with a matching cable type, after markup and rounding.</summary>
    CableLength,
}

/// <summary>Definition for one row in the Valve/Läbipääs (section 4) of an EN spec workbook.</summary>
public sealed class ValveLabipaasRowDef
{
    /// <summary>Position code exactly as it appears in the spec, e.g. "4.1", "4.16".</summary>
    public string Position { get; }

    /// <summary>Human-readable item name (used in reports when the Excel read fails).</summary>
    public string ItemName { get; }

    /// <summary>How the model quantity is derived.</summary>
    public ValveLabipaasRowSource Source { get; }

    /// <summary>Revit category name; used for <see cref="ValveLabipaasRowSource.PlacedElement"/> rows.</summary>
    public string? RevitCategory { get; }

    /// <summary>
    /// One or more (Family, Type) pairs to match. For detail items and placed elements.
    /// When multiple, their counts are summed.
    /// </summary>
    public IReadOnlyList<(string Family, string Type)> Mappings { get; }

    /// <summary>Matches the cable type name for the primary cable run (cable rows only).</summary>
    public Func<string, bool>? CableTypeMatch { get; }

    /// <summary>
    /// Optional secondary matcher for bundled cable runs that may optionally contribute to this row.
    /// Currently used for row 4.16 when <c>bundledRunsContributeToValveKaabel</c> is enabled.
    /// </summary>
    public Func<string, bool>? BundledCableTypeMatch { get; }

    private ValveLabipaasRowDef(
        string position,
        string itemName,
        ValveLabipaasRowSource source,
        string? revitCategory = null,
        IReadOnlyList<(string Family, string Type)>? mappings = null,
        Func<string, bool>? cableTypeMatch = null,
        Func<string, bool>? bundledCableTypeMatch = null)
    {
        Position             = position;
        ItemName             = itemName;
        Source               = source;
        RevitCategory        = revitCategory;
        Mappings             = mappings ?? Array.Empty<(string, string)>();
        CableTypeMatch       = cableTypeMatch;
        BundledCableTypeMatch = bundledCableTypeMatch;
    }

    // ── Factory helpers ────────────────────────────────────────────────────────

    /// <summary>Detail item counted by summing the given (Family, Type) occurrences on the drafting view.</summary>
    public static ValveLabipaasRowDef DetailItem(string pos, string name,
        params (string Family, string Type)[] mappings)
        => new(pos, name, ValveLabipaasRowSource.DetailItem, mappings: mappings);

    /// <summary>Model element counted by summing FamilyInstances with the given (Family, Type) under the given category.</summary>
    public static ValveLabipaasRowDef PlacedElement(string pos, string name, string category,
        params (string Family, string Type)[] mappings)
        => new(pos, name, ValveLabipaasRowSource.PlacedElement, revitCategory: category, mappings: mappings);

    /// <summary>Procurement-only row; spec quantity is carried through unchanged.</summary>
    public static ValveLabipaasRowDef Procurement(string pos, string name)
        => new(pos, name, ValveLabipaasRowSource.ProcurementOnly);

    /// <summary>Cable-length row derived from circuits whose cable type satisfies <paramref name="primaryMatch"/>.</summary>
    /// <param name="bundled">
    /// Optional secondary matcher; circuits satisfying this are added to the total when the
    /// <c>bundledRunsContributeToValveKaabel</c> skill setting is enabled.
    /// </param>
    public static ValveLabipaasRowDef Cable(string pos, string name,
        Func<string, bool> primaryMatch,
        Func<string, bool>? bundled = null)
        => new(pos, name, ValveLabipaasRowSource.CableLength,
               cableTypeMatch: primaryMatch, bundledCableTypeMatch: bundled);
}

/// <summary>
/// Static row definitions for section 4 (Valve/Läbipääs) of the EN spec workbook.
/// Rows 4.1–4.20 are defined; rows beyond 4.20 (if present) are logged as warnings.
/// </summary>
public static class ValveLabipaasRowDefinitions
{
    public static IReadOnlyList<ValveLabipaasRowDef> AllRows { get; } = new ValveLabipaasRowDef[]
    {
        // ── Section A: detail items on drafting view "SHS/LPS struktuur" (rows 4.1–4.5) ──

        ValveLabipaasRowDef.DetailItem("4.1", "Süsteemi peakontroller",
            ("EN_Keskseade_LPS_11", "Keskseade")),

        ValveLabipaasRowDef.DetailItem("4.2", "Kaardilugeja laiendusmoodul",
            ("EN_Keskseade_LPS_1", "Reader expander"),
            ("EN_Keskseade_LPS_1", "ISC")),

        ValveLabipaasRowDef.DetailItem("4.3", "Valve laiendusmoodul",
            ("EN_Keskseade_LPS_11", "Zone expander"),
            ("EN_Keskseade_LPS_1",  "ILAM")),

        ValveLabipaasRowDef.DetailItem("4.4", "GSM kommunikaator",
            ("EN_Keskseade_LPS_11", "GSM")),

        ValveLabipaasRowDef.DetailItem("4.5", "Raadiomoodul",
            ("EN_Keskseade_LPS_11", "2 relee")),

        // ── Section B: placed model elements (rows 4.6–4.13) ──────────────────

        ValveLabipaasRowDef.PlacedElement("4.6", "Valve kilp", "Electrical Equipment",
            ("EN_LPS_Kilp", "M kilp")),

        ValveLabipaasRowDef.PlacedElement("4.7", "Kaardilugeja", "Security Devices",
            ("EN_LPS-Kaardilugeja_v01", "Kaardilugeja")),

        ValveLabipaasRowDef.PlacedElement("4.8", "IR liikumisandur", "Security Devices",
            ("EN_LPS-Liikumisandur", "IR")),

        ValveLabipaasRowDef.PlacedElement("4.9", "Magnet süvistatud, akna", "Security Devices",
            ("EN_LPS-Uksemagnet_v01", "Magnet süvistatud, akna")),

        ValveLabipaasRowDef.PlacedElement("4.10", "Magnet süvistatud, Suur", "Security Devices",
            ("EN_LPS-Uksemagnet_v01", "Magnet süvistatud, Suur")),

        ValveLabipaasRowDef.PlacedElement("4.11", "Magnet pinnapealne", "Security Devices",
            ("EN_LPS-Uksemagnet_v01", "Magnet pinnapealne")),

        ValveLabipaasRowDef.PlacedElement("4.12", "Laoukse magnet", "Security Devices",
            ("EN_LPS-Uksemagnet_v01", "Laoukse magnet")),

        ValveLabipaasRowDef.PlacedElement("4.13", "Valvesõrmistik", "Security Devices",
            ("EN_LPS-Valvesõrmistik_v01", "Valvesõrmistik")),

        // ── Section C: procurement-only (rows 4.14–4.15) ─────────────────────

        ValveLabipaasRowDef.Procurement("4.14", "Raadiopult"),
        ValveLabipaasRowDef.Procurement("4.15", "Läbipääsu kaardid"),

        // ── Section D: cable lengths (rows 4.16–4.20) ─────────────────────────
        //
        // Rows 4.16 and 4.20 both reference CQR-family cable.
        // Row 4.16 matches the plain CQR (no "3 x 0.75" token).
        // Row 4.20 matches the bundled CQR+lock cable (contains "3 x 0.75").
        // When the "bundledRunsContributeToValveKaabel" setting is enabled,
        // row 4.20 circuits are also added into the 4.16 total.

        ValveLabipaasRowDef.Cable("4.16", "Valve kaabel",
            primaryMatch: s =>
                s.Contains("XX_EN_CQR 8 x 0,22 (CCA)", StringComparison.OrdinalIgnoreCase)
                && !s.Contains("3 x 0.75",             StringComparison.OrdinalIgnoreCase),
            bundled: s =>
                s.Contains("XX_EN_CQR 8 x 0,22 (CCA)", StringComparison.OrdinalIgnoreCase)
                && s.Contains("3 x 0.75",              StringComparison.OrdinalIgnoreCase)),

        ValveLabipaasRowDef.Cable("4.17", "Valve kaabel, välipaigaldus",
            primaryMatch: s => s.Contains("XX_EN_JAMAK-ARM", StringComparison.OrdinalIgnoreCase)),

        ValveLabipaasRowDef.Cable("4.18", "Side kaabel kaardilugejatele",
            primaryMatch: s =>
                s.Contains("XX_EN_Cat 6 F/UTP (CCA)", StringComparison.OrdinalIgnoreCase)
                && !s.Contains("ilmastikukindel",       StringComparison.OrdinalIgnoreCase)),

        ValveLabipaasRowDef.Cable("4.19", "Side kaabel kaardilugejatele, välipaigaldus",
            primaryMatch: s =>
                s.Contains("XX_EN_Cat 6 F/UTP", StringComparison.OrdinalIgnoreCase)
                && s.Contains("ilmastikukindel", StringComparison.OrdinalIgnoreCase)),

        ValveLabipaasRowDef.Cable("4.20", "Luku juhtimis kaabel",
            primaryMatch: s =>
                s.Contains("XX_EN_CQR 8 x 0,22 (CCA)", StringComparison.OrdinalIgnoreCase)
                && s.Contains("3 x 0.75",              StringComparison.OrdinalIgnoreCase)),
    };
}

/// <summary>
/// Pure cable-length business rules — no Revit API dependency, safe to unit-test directly.
/// </summary>
public static class ValveLabipaasRules
{
    /// <summary>
    /// Applies a markup percentage and rounds up to the nearest multiple.
    /// Formula: <c>ceil(rawMeters * (1 + markupPercent/100) / roundUpToMeters) * roundUpToMeters</c>.
    /// Returns 0 when <paramref name="rawMeters"/> is zero or negative.
    /// </summary>
    public static int ApplyMarkupAndRound(double rawMeters, double markupPercent, int roundUpToMeters)
    {
        if (rawMeters <= 0.0 || roundUpToMeters <= 0) return 0;
        var marked = rawMeters * (1.0 + markupPercent / 100.0);
        return (int)(Math.Ceiling(marked / roundUpToMeters) * roundUpToMeters);
    }
}

/// <summary>One resolved audit row comparing a spec quantity to the model-derived quantity.</summary>
public sealed class ValveLabipaasAuditRow
{
    /// <summary>Position code, e.g. "4.1", "4.16".</summary>
    public string Position { get; set; } = string.Empty;

    /// <summary>Item name (from spec if available, otherwise from <see cref="ValveLabipaasRowDef.ItemName"/>).</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Spec quantity (from the Excel workbook). 0 when the row was not found in the workbook.</summary>
    public int BeforeValue { get; set; }

    /// <summary>Model-derived quantity. For cable rows, this is meters rounded to the nearest multiple.</summary>
    public int AfterValue { get; set; }

    /// <summary>"count" | "cable" | "procurement"</summary>
    public string RowType { get; set; } = "count";

    /// <summary>Human-readable description of how <see cref="AfterValue"/> was computed.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Additional notes, including the ** cable display convention.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>True when spec and model quantities agree.</summary>
    public bool IsMatch => BeforeValue == AfterValue;
}

/// <summary>
/// Pure helpers shared between <see cref="AuditValveLabipaasSpecTask"/> and unit tests.
/// No Revit API dependency.
/// </summary>
public static class ValveLabipaasAuditHelpers
{
    /// <summary>
    /// Sums the count for each (Family, Type) mapping defined on <paramref name="rowDef"/>
    /// using the provided <paramref name="counts"/> dictionary.
    /// </summary>
    public static int SumMappings(
        ValveLabipaasRowDef rowDef,
        Dictionary<(string Family, string Type), int> counts)
    {
        int total = 0;
        foreach (var mapping in rowDef.Mappings)
            total += counts.TryGetValue(mapping, out var c) ? c : 0;
        return total;
    }

    /// <summary>
    /// Converts a list of <see cref="ValveLabipaasAuditRow"/> objects to <see cref="IssueDto"/> items.
    /// Severity rules:
    /// <list type="bullet">
    ///   <item>Procurement-only → <see cref="IssueSeverity.Info"/></item>
    ///   <item>AfterValue = 0 and BeforeValue &gt; 0 → <see cref="IssueSeverity.Error"/> (source not found)</item>
    ///   <item>Quantity mismatch → <see cref="IssueSeverity.Warning"/></item>
    ///   <item>Matching rows → no issue emitted</item>
    /// </list>
    /// </summary>
    public static List<IssueDto> BuildIssues(
        IReadOnlyList<ValveLabipaasAuditRow> rows, string skillId, string runId, ref int seq)
    {
        var issues = new List<IssueDto>();

        foreach (var row in rows)
        {
            if (row.RowType == "procurement")
            {
                issues.Add(new IssueDto
                {
                    IssueId      = $"{runId}-LPS-{++seq:D4}",
                    RunId        = runId,
                    SourceTool   = skillId,
                    Severity     = IssueSeverity.Info,
                    Status       = IssueStatus.Open,
                    Category     = "ValveLabipaas.ProcurementOnly",
                    Title        = $"{row.Position} {row.ItemName} — procurement only",
                    Description  = $"Row {row.Position} ({row.ItemName}): spec qty {row.BeforeValue}. {row.Notes}",
                    SuggestedFix = "Verify with procurement records; not modeled in Revit.",
                    CreatedAt    = DateTimeOffset.UtcNow,
                });
                continue;
            }

            if (row.AfterValue == 0 && row.BeforeValue > 0)
            {
                issues.Add(new IssueDto
                {
                    IssueId      = $"{runId}-LPS-{++seq:D4}",
                    RunId        = runId,
                    SourceTool   = skillId,
                    Severity     = IssueSeverity.Error,
                    Status       = IssueStatus.Open,
                    Category     = "ValveLabipaas.MissingSource",
                    Title        = $"{row.Position} {row.ItemName} — source not found in model",
                    Description  = $"Row {row.Position} ({row.ItemName}): spec qty {FormatValue(row.BeforeValue, row.RowType)}, model returned 0. Source: {row.Source}",
                    SuggestedFix = "Verify that the family/type is placed in the model or that cable types match.",
                    CreatedAt    = DateTimeOffset.UtcNow,
                });
                continue;
            }

            if (!row.IsMatch)
            {
                issues.Add(new IssueDto
                {
                    IssueId      = $"{runId}-LPS-{++seq:D4}",
                    RunId        = runId,
                    SourceTool   = skillId,
                    Severity     = IssueSeverity.Warning,
                    Status       = IssueStatus.Open,
                    Category     = "ValveLabipaas.QuantityMismatch",
                    Title        = $"{row.Position} {row.ItemName} — quantity mismatch",
                    Description  = $"Row {row.Position} ({row.ItemName}): spec {FormatValue(row.BeforeValue, row.RowType)} → model {FormatValue(row.AfterValue, row.RowType)}",
                    SuggestedFix = "Update the spec or add/remove the corresponding Revit elements.",
                    CreatedAt    = DateTimeOffset.UtcNow,
                });
            }
        }

        return issues;
    }

    private static string FormatValue(int value, string rowType)
        => rowType == "cable" ? $"{value}**" : value.ToString();
}
