using Autodesk.Revit.DB;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.ModelState;

/// <summary>A view by id and name (phase listings).</summary>
public sealed class ViewRef
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ViewType { get; set; } = string.Empty;
}

public sealed class PhaseRow
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SequenceNumber { get; set; }
    public int ViewCount { get; set; }
    public List<ViewRef> ViewsUsingAsPhase { get; set; } = new();
    public long? Created { get; set; }
    public long? Demolished { get; set; }
}

public sealed class PhaseFilterRow
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public string ExistingStatus { get; set; } = string.Empty;
    public string DemolishedStatus { get; set; } = string.Empty;
    public string TemporaryStatus { get; set; } = string.Empty;
    public int ViewCount { get; set; }
}

public sealed class PhaseListing
{
    public List<PhaseRow> Phases { get; set; } = new();
    public List<PhaseFilterRow> PhaseFilters { get; set; } = new();
    public bool Counted { get; set; }
    public int Scanned { get; set; }
    public string? CountNote { get; set; }
}

/// <summary>
/// Phases in sequence, the views that use each one, the phase filters with their status presentations and — on
/// request — element counts created / demolished per phase (#109). API thread only.
/// </summary>
public static class PhaseReader
{
    public const int MaxViewsPerPhase = 200;

    /// <summary>The phases in sequence (1 = first), for the phase writer.</summary>
    public static List<PhaseInfo> Phases(Document doc)
    {
        var result = new List<PhaseInfo>();
        var sequence = 0;
        foreach (Phase phase in doc.Phases)
            result.Add(new PhaseInfo { Id = phase.Id.Value, Name = phase.Name ?? string.Empty, Sequence = ++sequence });
        return result;
    }

    /// <param name="scanCap">Element scan cap for the counts; above it the counts are left out with a note.</param>
    public static PhaseListing Read(Document doc, bool countElements, int scanCap, CancellationToken cancellationToken)
    {
        var listing = new PhaseListing();
        var rows = new Dictionary<long, PhaseRow>();
        foreach (var phase in Phases(doc))
        {
            var row = new PhaseRow { Id = phase.Id, Name = phase.Name, SequenceNumber = phase.Sequence };
            listing.Phases.Add(row);
            rows[phase.Id] = row;
        }

        var filters = new Dictionary<long, PhaseFilterRow>();
        foreach (var filter in new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>())
        {
            var row = new PhaseFilterRow
            {
                Id = filter.Id.Value,
                Name = filter.Name ?? string.Empty,
                NewStatus = Presentation(filter, ElementOnPhaseStatus.New),
                ExistingStatus = Presentation(filter, ElementOnPhaseStatus.Existing),
                DemolishedStatus = Presentation(filter, ElementOnPhaseStatus.Demolished),
                TemporaryStatus = Presentation(filter, ElementOnPhaseStatus.Temporary)
            };
            listing.PhaseFilters.Add(row);
            filters[row.Id] = row;
        }

        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (view.IsTemplate) continue;
            var phaseId = IdOf(view, BuiltInParameter.VIEW_PHASE);
            if (phaseId != null && rows.TryGetValue(phaseId.Value, out var phaseRow))
            {
                phaseRow.ViewCount++;
                if (phaseRow.ViewsUsingAsPhase.Count < MaxViewsPerPhase)
                    phaseRow.ViewsUsingAsPhase.Add(new ViewRef { Id = view.Id.Value, Name = SafeName(view), ViewType = view.ViewType.ToString() });
            }
            var filterId = IdOf(view, BuiltInParameter.VIEW_PHASE_FILTER);
            if (filterId != null && filters.TryGetValue(filterId.Value, out var filterRow))
                filterRow.ViewCount++;
        }

        if (countElements)
            Count(doc, listing, rows, scanCap, cancellationToken);
        return listing;
    }

    private static void Count(Document doc, PhaseListing listing, Dictionary<long, PhaseRow> rows, int scanCap, CancellationToken cancellationToken)
    {
        var created = new Dictionary<long, long>();
        var demolished = new Dictionary<long, long>();
        var scanned = 0;
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (scanned >= scanCap)
            {
                listing.Scanned = scanned;
                listing.CountNote = $"The model has more than {scanCap} elements (the scan cap), so the counts are left out. " +
                                    "Use revit_graph_summary (elementsByPhase) or revit_audit_model for model-wide figures.";
                return;
            }
            scanned++;
            if ((scanned & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var c = IdOf(element, BuiltInParameter.PHASE_CREATED);
            if (c != null) created[c.Value] = created.TryGetValue(c.Value, out var n) ? n + 1 : 1;
            var d = IdOf(element, BuiltInParameter.PHASE_DEMOLISHED);
            if (d != null) demolished[d.Value] = demolished.TryGetValue(d.Value, out var m) ? m + 1 : 1;
        }
        foreach (var row in rows.Values)
        {
            row.Created = created.TryGetValue(row.Id, out var n) ? n : 0;
            row.Demolished = demolished.TryGetValue(row.Id, out var m) ? m : 0;
        }
        listing.Counted = true;
        listing.Scanned = scanned;
    }

    /// <summary>The scan cap of revit_list_phases countElements=true.</summary>
    public static int DefaultScanCap => QueryLimits.Default.MaxScanElements;

    private static string Presentation(PhaseFilter filter, ElementOnPhaseStatus status)
    {
        try { return filter.GetPhaseStatusPresentation(status).ToString(); }
        catch { return string.Empty; }
    }

    /// <summary>The element id an id parameter points at, or null when unset.</summary>
    public static long? IdOf(Element element, BuiltInParameter parameter)
    {
        try
        {
            var p = element.get_Parameter(parameter);
            if (p == null || p.StorageType != StorageType.ElementId) return null;
            var id = p.AsElementId();
            return id == null || id == ElementId.InvalidElementId ? null : id.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeName(Element element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}
