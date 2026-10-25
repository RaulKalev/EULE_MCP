using Autodesk.Revit.DB;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.ModelState;

/// <summary>
/// Reads an element's phase created / demolished, design option and workset as names (#109), for
/// <c>includeModelState</c> and the model-state filter pseudo-parameters. Phase and workset names are cached per
/// reader, so make one per query. API thread only; never throws.
/// </summary>
public sealed class ElementModelStateReader
{
    private readonly Document _doc;
    private readonly WorksetNameReader _worksets;
    private readonly Dictionary<long, string> _phaseNames = new();

    public ElementModelStateReader(Document doc)
    {
        _doc = doc;
        _worksets = new WorksetNameReader(doc);
    }

    public ElementModelState Read(Element element, bool includeOwnership) => new()
    {
        PhaseCreated = PhaseName(element, BuiltInParameter.PHASE_CREATED),
        PhaseDemolished = PhaseName(element, BuiltInParameter.PHASE_DEMOLISHED),
        DesignOption = DesignOptionName(element),
        Workset = _worksets.NameOf(element),
        OwnedBy = includeOwnership ? OwnerOf(_doc, element.Id, _worksets.IsWorkshared) : null
    };

    /// <summary>The phase an id parameter points at, by name; empty when the element has no such phase.</summary>
    public string PhaseName(Element element, BuiltInParameter parameter)
    {
        try
        {
            var p = element.get_Parameter(parameter);
            if (p == null || p.StorageType != StorageType.ElementId) return string.Empty;
            var id = p.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) return string.Empty;
            if (_phaseNames.TryGetValue(id.Value, out var cached)) return cached;
            var name = (_doc.GetElement(id) as Phase)?.Name ?? string.Empty;
            _phaseNames[id.Value] = name;
            return name;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>The design option's name; empty for the main model.</summary>
    public static string DesignOptionName(Element element)
    {
        try { return element.DesignOption?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Who has the element checked out; empty when nobody does or the model is not workshared.</summary>
    public static string OwnerOf(Document doc, ElementId id, bool isWorkshared)
    {
        if (!isWorkshared) return string.Empty;
        try
        {
            var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            return status is CheckoutStatus.OwnedByCurrentUser or CheckoutStatus.OwnedByOtherUser
                ? owner ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
