using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using ClosedXML.Excel;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Skills.Tasks.Security;

/// <summary>
/// Task ID: security.audit.valve-labipaas-spec
/// Audits the Valve/Läbipääs section (rows 4.1–4.20) of an EN spec Excel workbook against
/// the live Revit model.
///
/// Settings (all optional except <c>excelFilePath</c>):
/// <list type="bullet">
///   <item><c>excelFilePath</c> — absolute path to the spec workbook (.xlsx/.xlsm). Required.</item>
///   <item><c>worksheetName</c> — worksheet to read; default "Sheet1".</item>
///   <item><c>draftingViewName</c> — name of the drafting view containing detail items; default "SHS/LPS struktuur".</item>
///   <item><c>quantityColumnLetter</c> — column letter for spec quantities (e.g. "G"); empty = auto-detect by "Arv"/"Kogus" header.</item>
///   <item><c>cableMarkupPercent</c> — markup applied to raw circuit lengths; default 30.</item>
///   <item><c>cableRoundUpMeters</c> — round cable totals up to this multiple; default 100.</item>
///   <item><c>bundledRunsContributeToValveKaabel</c> — when true, circuits using the bundled lock cable type also contribute to row 4.16; default false.</item>
///   <item><c>lengthEstimationMethod</c> — one of StraightLineMax, StraightLineSum, ManhattanMax, ManhattanSum, NearestNeighborPath; default "NearestNeighborPath".</item>
/// </list>
///
/// SharedData outputs:
/// <list type="bullet">
///   <item><c>valveLabipaasSummary</c> — List&lt;<see cref="ValveLabipaasAuditRow"/>&gt; with per-row before/after.</item>
///   <item><c>issueDtos</c> — List&lt;<see cref="IssueDto"/>&gt; for downstream export tasks.</item>
/// </list>
/// </summary>
internal sealed class AuditValveLabipaasSpecTask : ISkillTask
{
    public string Id           => "security.audit.valve-labipaas-spec";
    public string Name         => "Valve/Läbipääs Spec Audit";
    public bool   ChangesModel => false;

    private readonly CategoryResolver _categoryResolver = new();

    // ── ISkillTask.Run ────────────────────────────────────────────────────────

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // ── Read settings ────────────────────────────────────────────────

            var excelFilePath = SkillSettings.GetString(taskDef.Settings, "excelFilePath");
            if (string.IsNullOrWhiteSpace(excelFilePath))
                return Fail(Id, Name, "Setting 'excelFilePath' is required.", sw);

            var worksheetName    = SkillSettings.GetString(taskDef.Settings, "worksheetName",    "Sheet1");
            var draftingViewName = SkillSettings.GetString(taskDef.Settings, "draftingViewName", "SHS/LPS struktuur");
            var qtyColLetter     = SkillSettings.GetString(taskDef.Settings, "quantityColumnLetter", "");
            var markupPercent    = SkillSettings.GetDouble(taskDef.Settings, "cableMarkupPercent",    30.0);
            var roundUpMeters    = SkillSettings.GetInt   (taskDef.Settings, "cableRoundUpMeters",    100);
            var bundledContrib   = SkillSettings.GetBool  (taskDef.Settings, "bundledRunsContributeToValveKaabel", false);
            var lengthMethod     = SkillSettings.GetString(taskDef.Settings, "lengthEstimationMethod", "NearestNeighborPath");

            // ── Read spec Excel ──────────────────────────────────────────────

            var (specRows, readError) = ReadSpecExcel(excelFilePath, worksheetName, qtyColLetter);
            if (readError != null)
                return Fail(Id, Name, $"Excel read error: {readError}", sw);

            // ── Query Revit ──────────────────────────────────────────────────

            var doc      = ctx.Document;
            var warnings = new List<string>();
            int seq      = 0;

            var detailItemCounts    = CollectDetailItemCounts(doc, draftingViewName, warnings);
            var circuitLengthMeters = CollectCircuitLengthsMeters(doc, lengthMethod, warnings);

            // ── Build per-row audit results ──────────────────────────────────

            var auditRows = new List<ValveLabipaasAuditRow>(ValveLabipaasRowDefinitions.AllRows.Count);

            foreach (var rowDef in ValveLabipaasRowDefinitions.AllRows)
            {
                int specQty = specRows.TryGetValue(rowDef.Position, out var sq) ? sq : 0;
                BuildAuditRow(rowDef, specQty, doc, detailItemCounts, circuitLengthMeters,
                              markupPercent, roundUpMeters, bundledContrib, warnings,
                              out var auditRow);
                auditRows.Add(auditRow);
            }

            // ── Build IssueDto list ──────────────────────────────────────────

            var issues = ValveLabipaasAuditHelpers.BuildIssues(auditRows, ctx.SkillId, ctx.RunId, ref seq);

            ctx.SharedData["valveLabipaasSummary"] = auditRows;
            ctx.SharedData["issueDtos"]            = issues;

            return new SkillTaskResult
            {
                TaskId     = Id, TaskName = Name, Success = true, Warnings = warnings,
                Message    = $"Valve/Läbipääs spec audit complete: {auditRows.Count} rows, {issues.Count} issue(s).",
                IssueCount = issues.Count, DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId = Id, TaskName = Name, Success = false,
                Message = $"Valve/Läbipääs spec audit failed: {ex.Message}", DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    // ── Row builder ───────────────────────────────────────────────────────────

    private void BuildAuditRow(
        ValveLabipaasRowDef rowDef,
        int specQty,
        Document doc,
        Dictionary<(string Family, string Type), int> detailItemCounts,
        Dictionary<string, double> circuitLengthMeters,
        double markupPercent,
        int roundUpMeters,
        bool bundledContrib,
        List<string> warnings,
        out ValveLabipaasAuditRow row)
    {
        int    afterValue;
        string rowType;
        string source;
        string notes;

        switch (rowDef.Source)
        {
            case ValveLabipaasRowSource.DetailItem:
            {
                var parts = new List<string>();
                int total = 0;
                foreach (var (family, type) in rowDef.Mappings)
                {
                    int count = detailItemCounts.TryGetValue((family, type), out var c) ? c : 0;
                    total += count;
                    parts.Add($"{family}:{type}={count}");
                }
                afterValue = total;
                rowType    = "count";
                source     = string.Join(", ", parts);
                notes      = rowDef.Mappings.Count > 1
                    ? $"Multi-source: {string.Join(" + ", parts)}"
                    : string.Empty;
                break;
            }

            case ValveLabipaasRowSource.PlacedElement:
            {
                var parts = new List<string>();
                int total = 0;
                foreach (var (family, type) in rowDef.Mappings)
                {
                    int count = CountPlacedElements(doc, rowDef.RevitCategory!, family, type, warnings);
                    total += count;
                    parts.Add($"{family}:{type}={count}");
                }
                afterValue = total;
                rowType    = "count";
                source     = $"{rowDef.RevitCategory} / {string.Join(" + ", rowDef.Mappings.Select(m => $"{m.Family}:{m.Type}"))}";
                notes      = rowDef.Mappings.Count > 1
                    ? $"Multi-source: {string.Join(" + ", parts)}"
                    : string.Empty;
                break;
            }

            case ValveLabipaasRowSource.ProcurementOnly:
                afterValue = specQty;
                rowType    = "procurement";
                source     = "procurement-only / not modeled";
                notes      = "procurement-only / not modeled";
                break;

            case ValveLabipaasRowSource.CableLength:
            {
                double rawMeters = circuitLengthMeters
                    .Where(kv => rowDef.CableTypeMatch!(kv.Key))
                    .Sum(kv => kv.Value);

                if (bundledContrib && rowDef.BundledCableTypeMatch != null)
                {
                    rawMeters += circuitLengthMeters
                        .Where(kv => rowDef.BundledCableTypeMatch(kv.Key))
                        .Sum(kv => kv.Value);
                }

                afterValue = ValveLabipaasRules.ApplyMarkupAndRound(rawMeters, markupPercent, roundUpMeters);
                rowType    = "cable";
                source     = $"circuits × {markupPercent}% markup, ceil to {roundUpMeters} m";

                var bDisp = specQty  > 0 ? $"{specQty}**"   : "0";
                var aDisp = afterValue > 0 ? $"{afterValue}**" : "0";
                notes = $"{bDisp} -> {aDisp}";
                break;
            }

            default:
                afterValue = 0;
                rowType    = "count";
                source     = string.Empty;
                notes      = string.Empty;
                break;
        }

        row = new ValveLabipaasAuditRow
        {
            Position   = rowDef.Position,
            ItemName   = rowDef.ItemName,
            BeforeValue = specQty,
            AfterValue  = afterValue,
            RowType    = rowType,
            Source     = source,
            Notes      = notes,
        };
    }

    // ── Excel reading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the spec workbook and extracts (position → spec quantity) for all 4.X rows found.
    /// </summary>
    internal static (Dictionary<string, int> Rows, string? Error) ReadSpecExcel(
        string filePath, string worksheetName, string quantityColumnLetter)
    {
        if (!File.Exists(filePath))
            return (new(), $"File not found: {filePath}");

        try
        {
            using var wb = new XLWorkbook(filePath);

            IXLWorksheet ws;
            if (!wb.TryGetWorksheet(worksheetName, out ws))
            {
                ws = wb.Worksheets.FirstOrDefault(w => w.Visibility == XLWorksheetVisibility.Visible)!;
                if (ws is null)
                    return (new(), $"Worksheet '{worksheetName}' not found and no visible worksheets exist.");
            }

            var used = ws.RangeUsed();
            if (used is null) return (new(), null);

            int firstRow = used.FirstRow().RowNumber();
            int lastRow  = used.LastRow().RowNumber();
            int firstCol = used.FirstColumn().ColumnNumber();
            int lastCol  = used.LastColumn().ColumnNumber();

            // ── Resolve quantity column ──────────────────────────────────────

            int? qtyCol = null;

            if (!string.IsNullOrWhiteSpace(quantityColumnLetter))
            {
                // User explicitly specified a column letter (e.g. "G")
                qtyCol = XLHelper.GetColumnNumberFromLetter(quantityColumnLetter.Trim().ToUpperInvariant());
            }

            if (qtyCol is null)
            {
                // Auto-detect: scan first 30 rows for a header cell named "Arv", "Kogus" or "Quantity"
                for (int r = firstRow; r <= Math.Min(firstRow + 30, lastRow) && qtyCol is null; r++)
                {
                    for (int c = firstCol; c <= lastCol; c++)
                    {
                        var h = ws.Cell(r, c).GetString().Trim();
                        if (string.Equals(h, "Arv",      StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(h, "Kogus",    StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(h, "Quantity", StringComparison.OrdinalIgnoreCase))
                        {
                            qtyCol = c;
                            break;
                        }
                    }
                }
            }

            // ── Scan for 4.X rows ────────────────────────────────────────────

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int r = firstRow; r <= lastRow; r++)
            {
                // Check the first few columns for a "4.X" position token
                for (int c = firstCol; c <= Math.Min(firstCol + 4, lastCol); c++)
                {
                    var cellText = ws.Cell(r, c).GetString().Trim();
                    if (!TryParsePosition(cellText, out var position)) continue;

                    int qty;
                    if (qtyCol.HasValue)
                    {
                        qty = ParseQty(ws.Cell(r, qtyCol.Value).GetString());
                    }
                    else
                    {
                        // Fallback: scan row right-to-left for the first integer value
                        qty = 0;
                        for (int sc = lastCol; sc > c + 1; sc--)
                        {
                            var v = ws.Cell(r, sc).GetString().Trim();
                            if (v.Length > 0 && int.TryParse(v, out var p)) { qty = p; break; }
                        }
                    }

                    result[position] = qty;
                    break;
                }
            }

            return (result, null);
        }
        catch (Exception ex)
        {
            return (new(), $"Failed to open workbook: {ex.Message}");
        }
    }

    // ── Revit queries ─────────────────────────────────────────────────────────

    /// <summary>
    /// Counts all FamilyInstances in the named drafting view, grouped by (FamilyName, TypeName).
    /// </summary>
    private static Dictionary<(string Family, string Type), int> CollectDetailItemCounts(
        Document doc, string draftingViewName, List<string> warnings)
    {
        var counts = new Dictionary<(string, string), int>();

        var view = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(v =>
                v.ViewType == ViewType.DraftingView &&
                string.Equals(v.Name, draftingViewName, StringComparison.OrdinalIgnoreCase));

        if (view is null)
        {
            warnings.Add($"Drafting view '{draftingViewName}' not found — rows 4.1–4.5 will show AfterValue = 0.");
            return counts;
        }

        var instances = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .ToList();

        foreach (var fi in instances)
        {
            var family = fi.Symbol?.Family?.Name ?? string.Empty;
            var type   = fi.Symbol?.Name         ?? string.Empty;
            var key    = (family, type);
            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// Counts model elements by Revit category, family name, and type name.
    /// </summary>
    private int CountPlacedElements(
        Document doc, string categoryName, string familyName, string typeName,
        List<string> warnings)
    {
        var resolve = _categoryResolver.Resolve(doc, categoryName);
        if (resolve.Category is null)
        {
            warnings.Add($"Category '{categoryName}' not found in model.");
            return 0;
        }

        return new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategoryId(resolve.Category.Id)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Count(fi =>
                string.Equals(fi.Symbol?.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fi.Symbol?.Name,         typeName,   StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sums estimated circuit lengths (in meters) per cable type name, across all electrical systems.
    /// </summary>
    private static Dictionary<string, double> CollectCircuitLengthsMeters(
        Document doc, string estimationMethod, List<string> warnings)
    {
        var totals  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var circuits = CircuitQueryService.GetAll(doc);

        foreach (var circuit in circuits)
        {
            var cableType = CircuitDtoBuilder.GetWireTypeName(doc, circuit);
            if (string.IsNullOrWhiteSpace(cableType)) continue;

            // Panel location
            var panel = CircuitDtoBuilder.TryGetPanel(circuit);
            (double X, double Y, double Z) panelPt = (0, 0, 0);
            if (panel is not null)
            {
                var panelLoc = CircuitLocationResolver.Resolve(panel);
                if (panelLoc.HasValue) panelPt = panelLoc.Value.Pt;
            }

            // Element locations
            var elementPts = new List<(long Id, (double X, double Y, double Z)? Loc)>();
            try
            {
                var elemSet = circuit.Elements;
                if (elemSet is not null)
                {
                    foreach (Element e in elemSet)
                    {
                        var loc = CircuitLocationResolver.Resolve(e);
                        elementPts.Add((e.Id.Value, loc?.Pt));
                    }
                }
            }
            catch { /* Circuit.Elements may throw on certain system types */ }

            if (elementPts.Count == 0) continue;

            var (rawFeet, _) = CircuitLengthEstimator.Estimate(panelPt, elementPts, estimationMethod);
            double meters    = LengthUnitHelper.ToMeters(rawFeet);

            totals[cableType] = totals.TryGetValue(cableType, out var prev) ? prev + meters : meters;
        }

        return totals;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParsePosition(string text, out string position)
    {
        var m = Regex.Match(text, @"^4\.(\d{1,2})\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var sub) && sub >= 1 && sub <= 22)
        {
            position = $"4.{sub}";
            return true;
        }
        position = string.Empty;
        return false;
    }

    private static int ParseQty(string text)
    {
        var t = text.Trim().Replace("\u00a0", "").Replace(" ", "");
        if (int.TryParse(t, out var i)) return i;
        if (double.TryParse(t, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return (int)Math.Round(d);
        return 0;
    }

    private static SkillTaskResult Fail(string id, string name, string message, Stopwatch sw) =>
        new() { TaskId = id, TaskName = name, Success = false, CriticalFailure = true,
                Message = message, DurationMs = sw.ElapsedMilliseconds };
}
