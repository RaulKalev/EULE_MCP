using Autodesk.Revit.DB;
using RevitMCP.Addin.Delivery;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>Which sheets to export, as the tools receive it.</summary>
public sealed class SheetSelectionSpec
{
    /// <summary>Element ids, full sheet numbers, or the discipline-group-sequence core ("EL-5-01" matches "1626_TP_EL-5-01_…").</summary>
    public List<string> Sheets { get; init; } = new();
    /// <summary>A saved Print / Export sheet set (ViewSheetSet) by name.</summary>
    public string SheetSet { get; init; } = string.Empty;
    public bool AllSheets { get; init; }
    public string Discipline { get; init; } = string.Empty;
    public string NumberContains { get; init; } = string.Empty;
    public string NameContains { get; init; } = string.Empty;

    public bool IsEmpty => Sheets.Count == 0 && SheetSet.Length == 0 && !AllSheets &&
                           Discipline.Length == 0 && NumberContains.Length == 0 && NameContains.Length == 0;
}

/// <summary>Resolves a <see cref="SheetSelectionSpec"/> against the document and reads what the planner needs per sheet.</summary>
public static class SheetExportSelection
{
    public static List<ViewSheet> Resolve(Document doc, SheetSelectionSpec spec, List<string> notes, out string? error)
    {
        error = null;
        var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IEnumerable<ViewSheet> picked;
        var explicitPick = spec.Sheets.Count > 0 || spec.SheetSet.Length > 0;
        if (!explicitPick)
            picked = all;
        else
        {
            var ids = new HashSet<long>();
            foreach (var token in spec.Sheets.Select(t => (t ?? string.Empty).Trim()).Where(t => t.Length > 0))
            {
                var matches = Match(all, token);
                if (matches.Count == 0) notes.Add($"No sheet matches '{token}'.");
                foreach (var s in matches) ids.Add(s.Id.Value);
            }
            if (spec.SheetSet.Length > 0)
            {
                var set = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                    .FirstOrDefault(s => string.Equals(s.Name, spec.SheetSet, StringComparison.OrdinalIgnoreCase));
                if (set == null)
                {
                    error = $"No sheet set named '{spec.SheetSet}'. revit_list_export_setups lists them.";
                    return new List<ViewSheet>();
                }
                var inSet = set.Views.Cast<View>().OfType<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
                var views = set.Views.Size - inSet.Count;
                if (views > 0) notes.Add($"Sheet set '{set.Name}' also holds {views} view(s); only its sheets are exported.");
                foreach (var s in inSet) ids.Add(s.Id.Value);
            }
            picked = all.Where(s => ids.Contains(s.Id.Value));
        }

        if (spec.Discipline.Length > 0)
            picked = picked.Where(s => string.Equals(RevitSheetNumberParser.Parse(s.SheetNumber)?.Discipline, spec.Discipline, StringComparison.OrdinalIgnoreCase));
        if (spec.NumberContains.Length > 0)
            picked = picked.Where(s => s.SheetNumber.IndexOf(spec.NumberContains, StringComparison.OrdinalIgnoreCase) >= 0);
        if (spec.NameContains.Length > 0)
            picked = picked.Where(s => (s.Name ?? string.Empty).IndexOf(spec.NameContains, StringComparison.OrdinalIgnoreCase) >= 0);

        return picked.ToList();
    }

    /// <summary>An element id, an exact sheet number, or a discipline-group-sequence core (which may match several merge parts).</summary>
    private static List<ViewSheet> Match(List<ViewSheet> all, string token)
    {
        if (long.TryParse(token, out var id))
        {
            var byId = all.Where(s => s.Id.Value == id).ToList();
            if (byId.Count > 0) return byId;
        }
        var exact = all.Where(s => string.Equals(s.SheetNumber, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        var core = RevitSheetNumberParser.Parse(token)?.SheetNumberCore ?? token;
        return all.Where(s => string.Equals(RevitSheetNumberParser.Parse(s.SheetNumber)?.SheetNumberCore, core, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Number, name, current revision, sheet-list flag, title block and printed size per sheet.</summary>
    public static List<ExportSheetInfo> Describe(Document doc, IReadOnlyList<ViewSheet> sheets)
    {
        var withTitleBlock = new HashSet<long>(new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
            .Select(e => e.OwnerViewId.Value));

        return sheets.Select(s =>
        {
            var (w, h) = SizeMm(s);
            return new ExportSheetInfo
            {
                Id = s.Id.Value,
                SheetNumber = s.SheetNumber ?? string.Empty,
                SheetName = s.Name ?? string.Empty,
                Revision = s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? string.Empty,
                AppearsInSheetList = s.get_Parameter(BuiltInParameter.SHEET_SCHEDULED)?.AsInteger() != 0,
                HasTitleBlock = withTitleBlock.Contains(s.Id.Value),
                WidthMm = w,
                HeightMm = h
            };
        }).ToList();
    }

    private static (double W, double H) SizeMm(ViewSheet sheet)
    {
        try
        {
            var o = sheet.Outline;
            if (o == null) return (0, 0);
            return (Math.Round(UnitUtils.ConvertFromInternalUnits(o.Max.U - o.Min.U, UnitTypeId.Millimeters)),
                    Math.Round(UnitUtils.ConvertFromInternalUnits(o.Max.V - o.Min.V, UnitTypeId.Millimeters)));
        }
        catch { return (0, 0); }
    }

    /// <summary>ISO label ("A3") within 10 mm, else "420x297mm" (EliteSheets' PaperSizeHelper).</summary>
    public static string PaperLabel(double widthMm, double heightMm)
    {
        if (widthMm <= 0 || heightMm <= 0) return "unknown";
        var w = Math.Min(widthMm, heightMm);
        var h = Math.Max(widthMm, heightMm);
        var iso = new (string Name, int W, int H)[]
        {
            ("A0", 841, 1189), ("A1", 594, 841), ("A2", 420, 594), ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210)
        };
        foreach (var (name, iw, ih) in iso)
            if (Math.Abs(w - iw) <= 10 && Math.Abs(h - ih) <= 10) return name;
        return $"{Math.Round(widthMm)}x{Math.Round(heightMm)}mm";
    }
}
