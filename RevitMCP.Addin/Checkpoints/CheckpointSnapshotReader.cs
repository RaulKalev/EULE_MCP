using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Checkpoints;

/// <summary>
/// Resolves a checkpoint scope to elements and reads <see cref="ElementSnapshot"/>s (#98). The same reader
/// produces the checkpoint-time snapshot and the "now" side of a diff, so values compare as equal strings.
/// API thread only.
/// </summary>
internal sealed class CheckpointSnapshotReader
{
    private const double MmPerFoot = 304.8;

    private readonly Document _doc;
    private readonly int _maxParameters;
    private readonly Dictionary<long, string?> _levelNames = new();
    private readonly Dictionary<long, string?> _typeNames = new();

    public CheckpointSnapshotReader(Document doc, QueryLimits? limits = null)
    {
        _doc = doc;
        _maxParameters = (limits ?? QueryLimits.Default).MaxParametersPerElement;
    }

    /// <summary>
    /// The element ids a scope names, distinct. Null with <paramref name="error"/> for an unknown category or
    /// view, or selection on a non-active document.
    /// </summary>
    public static List<long>? ResolveScope(UIApplication uiapp, Document doc, bool isActive, SnapshotScope scope, out string? error)
    {
        error = null;
        var ids = new HashSet<long>(scope.ElementIds);

        if (scope.UseSelection)
        {
            if (!isActive)
            {
                error = "useSelection needs the document to be active; pass elementIds instead or call revit_activate_document first.";
                return null;
            }
            foreach (var id in uiapp.ActiveUIDocument!.Selection.GetElementIds()) ids.Add(id.Value);
        }

        if (scope.Categories.Count > 0)
        {
            var resolver = new CategoryResolver();
            foreach (var name in scope.Categories)
            {
                var resolved = resolver.Resolve(doc, name);
                if (resolved.Category == null)
                {
                    error = resolved.Message + (resolved.Suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", resolved.Suggestions)}?" : string.Empty);
                    return null;
                }
                foreach (var id in new FilteredElementCollector(doc).WhereElementIsNotElementType().OfCategoryId(resolved.Category.Id).ToElementIds())
                    ids.Add(id.Value);
            }
        }

        if (scope.ViewId is long viewId)
        {
            if (doc.GetElement(new ElementId(viewId)) is not View view)
            {
                error = $"viewId {viewId} is not a view in '{doc.Title}'.";
                return null;
            }
            foreach (var id in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds())
                ids.Add(id.Value);
        }

        return ids.OrderBy(i => i).ToList();
    }

    /// <summary>
    /// Reads one element; null when it does not exist. With <paramref name="parameterNames"/> exactly those
    /// instance parameters are read (a missing value is stored as null); without, the writable instance parameters
    /// (with a value first, then empty ones) and the read-only ones with a value, up to QueryLimits.MaxParametersPerElement.
    /// </summary>
    public ElementSnapshot? Read(long id, IReadOnlyCollection<string>? parameterNames)
    {
        Element? element;
        try { element = _doc.GetElement(new ElementId(id)); }
        catch { return null; }
        if (element == null) return null;

        var snapshot = new ElementSnapshot();
        FillIdentity(element, snapshot);
        ReadLocation(element, snapshot);
        ReadParameters(element, parameterNames, snapshot);
        return snapshot;
    }

    /// <summary>Category, name, type and level only — what the change tracker caches for later deletions.</summary>
    public ElementIdentity? ReadIdentity(long id)
    {
        Element? element;
        try { element = _doc.GetElement(new ElementId(id)); }
        catch { return null; }
        if (element == null) return null;
        var identity = new ElementIdentity();
        FillIdentity(element, identity);
        return identity;
    }

    private void FillIdentity(Element element, ElementIdentity into)
    {
        try { into.Category = element.Category?.Name; } catch { }
        try { into.Name = element.Name; } catch { }
        try
        {
            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                if (!_typeNames.TryGetValue(typeId.Value, out var typeName))
                {
                    typeName = _doc.GetElement(typeId)?.Name;
                    _typeNames[typeId.Value] = typeName;
                }
                into.TypeName = typeName;
            }
        }
        catch { }
        try
        {
            var levelId = element.LevelId;
            if (levelId != null && levelId != ElementId.InvalidElementId)
            {
                if (!_levelNames.TryGetValue(levelId.Value, out var levelName))
                {
                    levelName = _doc.GetElement(levelId)?.Name;
                    _levelNames[levelId.Value] = levelName;
                }
                into.Level = levelName;
            }
        }
        catch { }
    }

    private static void ReadLocation(Element element, ElementSnapshot snapshot)
    {
        try
        {
            switch (element.Location)
            {
                case LocationPoint point:
                    snapshot.LocationMm = ToMm(point.Point);
                    try { snapshot.RotationRad = point.Rotation; } catch { /* not every point location has a rotation */ }
                    break;
                case LocationCurve curve when curve.Curve != null && curve.Curve.IsBound:
                    var start = ToMm(curve.Curve.GetEndPoint(0));
                    var end = ToMm(curve.Curve.GetEndPoint(1));
                    snapshot.LocationLineMm = start.Concat(end).ToArray();
                    break;
            }
        }
        catch { }
    }

    private void ReadParameters(Element element, IReadOnlyCollection<string>? parameterNames, ElementSnapshot snapshot)
    {
        IList<Parameter> parameters;
        try { parameters = element.GetOrderedParameters(); }
        catch { return; }

        if (parameterNames is { Count: > 0 })
        {
            foreach (var name in parameterNames)
            {
                var p = parameters.FirstOrDefault(x => string.Equals(x.Definition?.Name, name, StringComparison.OrdinalIgnoreCase));
                if (p == null)
                {
                    snapshot.Parameters[name] = null;
                    continue;
                }
                Add(p, name, snapshot);
            }
            return;
        }

        // Writable empty parameters too: a value written into them later must show in the diff (SnapshotParameterSelection).
        var parameterOf = new Dictionary<SnapshotParameterCandidate, Parameter>();
        foreach (var p in parameters)
        {
            try
            {
                if (p.Definition?.Name is not { Length: > 0 } name) continue;
                var candidate = new SnapshotParameterCandidate(name, p.IsReadOnly, p.HasValue && ParameterResolver.DisplayValue(p) is { Length: > 0 });
                parameterOf[candidate] = p;
            }
            catch { }
        }
        foreach (var c in SnapshotParameterSelection.Select(parameterOf.Keys, _maxParameters))
            Add(parameterOf[c], c.Name, snapshot);
    }

    /// <summary>
    /// Revit bookkeeping (the hidden System Browser view, plain uncategorised elements) that change tracking
    /// skips; see <see cref="InternalElementRule"/>. False for a missing element or one that cannot be read.
    /// </summary>
    public static bool IsInternal(Document doc, long id)
    {
        try
        {
            var element = doc.GetElement(new ElementId(id));
            if (element == null) return false;
            return InternalElementRule.IsInternal(element.Category != null, element is ElementType, element is View,
                element.GetType() == typeof(Element));
        }
        catch
        {
            return false;
        }
    }

    private static void Add(Parameter p, string name, ElementSnapshot snapshot)
    {
        try
        {
            snapshot.Parameters[name] = ParameterResolver.DisplayValue(p);
            if (p.IsReadOnly) return;
            var raw = p.StorageType switch
            {
                StorageType.String => RawParameterValue.String(p.AsString()),
                StorageType.Integer => RawParameterValue.Integer(p.AsInteger()),
                StorageType.Double => RawParameterValue.Double(p.AsDouble()),
                StorageType.ElementId => RawParameterValue.ElementId(p.AsElementId().Value),
                _ => null
            };
            if (raw != null) snapshot.Raw[name] = raw;
        }
        catch { }
    }

    private static double[] ToMm(XYZ p) => new[] { p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot };
}
