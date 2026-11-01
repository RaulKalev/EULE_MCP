using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.ClashFix;

namespace RevitMCP.Addin.Coordination.Bcf;

/// <summary>A BCF component and the Revit element it was matched to (null when none was found).</summary>
internal sealed class ResolvedComponent
{
    public ResolvedComponent(BcfComponent component)
    {
        Component = component;
    }

    public BcfComponent Component { get; }
    public PlacedElement? Placed { get; set; }

    /// <summary>ifcGuid+authoringToolId, ifcGuid, ifcGuidParameter, authoringToolId, or none.</summary>
    public string MatchedBy { get; set; } = "none";

    /// <summary>High (the GUID matches) or Low (only the authoring tool's id matched, in exactly one model).</summary>
    public string? Confidence { get; set; }

    public string? Note { get; set; }

    public object Describe() => new
    {
        ifcGuid = Component.IfcGuid.Length > 0 ? Component.IfcGuid : null,
        authoringToolId = Component.AuthoringToolId,
        originatingSystem = Component.OriginatingSystem,
        matchedBy = MatchedBy,
        confidence = Confidence,
        elementId = Placed?.Element.Id.Value,
        linkInstanceId = Placed?.Link?.Id.Value,
        linkName = Placed?.Link?.Name,
        category = Placed?.Element.Category?.Name,
        family = (Placed?.Element as FamilyInstance)?.Symbol?.FamilyName,
        name = Placed?.Element.Name,
        note = Note
    };
}

/// <summary>
/// Matches BCF components to elements of the host model and its loaded links. An element's IFC GlobalId is what
/// Revit's IFC exporter writes: the "IfcGUID" parameter when the export stored one, else the compressed
/// <c>ExportUtils.GetExportId</c>. The component's AuthoringToolId (the Revit ElementId, when the clash tool kept it)
/// is tried first because it is cheap, and only trusted when that element's GlobalId agrees; otherwise every model is
/// indexed by GlobalId once per resolver. A bare id match is reported with Low confidence, since ids repeat between
/// models. API thread.
/// </summary>
internal sealed class BcfElementResolver
{
    private sealed class ModelEntry
    {
        public ModelEntry(Document document, RevitLinkInstance? link, Transform toHost)
        {
            Document = document;
            Link = link;
            ToHost = toHost;
        }

        public Document Document { get; }
        public RevitLinkInstance? Link { get; }
        public Transform ToHost { get; }
    }

    private readonly List<ModelEntry> _models = new();
    private readonly Dictionary<Document, Dictionary<string, ElementId>> _indexes = new();

    public BcfElementResolver(Document host)
    {
        _models.Add(new ModelEntry(host, null, Transform.Identity));
        foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var linkDoc = link.GetLinkDocument();
            if (linkDoc != null) _models.Add(new ModelEntry(linkDoc, link, link.GetTotalTransform()));
            else UnloadedLinks.Add(link.Name);
        }
    }

    /// <summary>Links that are not loaded: their elements cannot be matched.</summary>
    public List<string> UnloadedLinks { get; } = new();

    /// <summary>How many models (host and loaded links) had to be indexed by GlobalId.</summary>
    public int IndexedModels => _indexes.Count;

    public ResolvedComponent Resolve(BcfComponent component)
    {
        var resolved = new ResolvedComponent(component);
        var guid = component.IfcGuid;
        var hasGuid = IfcGuid.IsValid(guid);

        // 1. The authoring tool's id, confirmed by the GlobalId.
        var idMatches = new List<ModelEntry>();
        if (long.TryParse(component.AuthoringToolId, out var id) && id > 0)
        {
            foreach (var model in _models)
            {
                var element = model.Document.GetElement(new ElementId(id));
                if (element == null || element is ElementType) continue;
                idMatches.Add(model);
                if (hasGuid && GlobalIdsOf(model.Document, element).Contains(guid))
                {
                    resolved.Placed = new PlacedElement(element, model.Link, model.ToHost);
                    resolved.MatchedBy = "ifcGuid+authoringToolId";
                    resolved.Confidence = "High";
                    return resolved;
                }
            }
        }

        // 2. The GlobalId in every model, host first.
        if (hasGuid)
        {
            var matches = new List<(ModelEntry Model, Element Element)>();
            foreach (var model in _models)
            {
                if (Index(model.Document).TryGetValue(guid, out var elementId) && model.Document.GetElement(elementId) is { } element)
                    matches.Add((model, element));
            }

            if (matches.Count > 0)
            {
                var (model, element) = matches[0];
                resolved.Placed = new PlacedElement(element, model.Link, model.ToHost);
                resolved.MatchedBy = "ifcGuid";
                resolved.Confidence = "High";
                if (matches.Count > 1)
                    resolved.Note = $"The GlobalId is in {matches.Count} models (a model linked more than once?); took " +
                                    (model.Link == null ? "the host" : $"link '{model.Link.Name}'") + ".";
                return resolved;
            }
        }

        // 3. Only the id, and only when it is unambiguous.
        if (idMatches.Count == 1)
        {
            var model = idMatches[0];
            resolved.Placed = new PlacedElement(model.Document.GetElement(new ElementId(id))!, model.Link, model.ToHost);
            resolved.MatchedBy = "authoringToolId";
            resolved.Confidence = "Low";
            resolved.Note = hasGuid
                ? "The element id exists but its GlobalId differs: the model changed since the IFC export, or the id is from another model."
                : "Matched by element id only (the component has no GlobalId).";
            return resolved;
        }

        resolved.Note = idMatches.Count > 1
            ? $"Element id {id} exists in {idMatches.Count} models and the GlobalId matches none: ambiguous, not matched."
            : hasGuid
                ? "No element of the host or its loaded links has this GlobalId: deleted, in an unloaded link, or exported without stable GUIDs."
                : "The component has neither a valid GlobalId nor an element id.";
        return resolved;
    }

    /// <summary>The GlobalIds an element is exported under: the stored "IfcGUID" parameter and the export id's.</summary>
    private static HashSet<string> GlobalIdsOf(Document doc, Element element)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var stored = element.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            if (!string.IsNullOrWhiteSpace(stored)) result.Add(stored!.Trim());
        }
        catch
        {
            // No IFC parameter on this element.
        }

        try
        {
            result.Add(IfcGuid.FromGuid(ExportUtils.GetExportId(doc, element.Id)));
        }
        catch
        {
            // Elements without an export id are not exported to IFC either.
        }

        return result;
    }

    private Dictionary<string, ElementId> Index(Document doc)
    {
        if (_indexes.TryGetValue(doc, out var index)) return index;

        index = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (element.Category == null || element.ViewSpecific) continue;
            foreach (var globalId in GlobalIdsOf(doc, element))
            {
                if (!index.ContainsKey(globalId)) index[globalId] = element.Id;
            }
        }

        _indexes[doc] = index;
        return index;
    }
}
