using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PlaceTagsTool : IRevitMcpTool
{
    public string Name => "revit_place_tags";
    public string Description => "Tags model elements in a view (IndependentTag). Elements come from elementIds or the current selection. The tag type is resolved automatically from each element's category unless tagTypeId or tagFamilyName/tagTypeName is given. Optional leader and head offset (mm, in view plane). Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var tagTypeId = ToolArguments.GetLong(request.Arguments, "tagTypeId");
        var tagFamilyName = ToolArguments.GetString(request.Arguments, "tagFamilyName");
        var tagTypeName = ToolArguments.GetString(request.Arguments, "tagTypeName");
        var addLeader = ToolArguments.GetBool(request.Arguments, "addLeader");
        var orientationStr = ToolArguments.GetString(request.Arguments, "orientation", "Horizontal");
        var offsetXMm = ToolArguments.GetDouble(request.Arguments, "offsetXMm");
        var offsetYMm = ToolArguments.GetDouble(request.Arguments, "offsetYMm");

        if (!useSelection && elementIds.Length == 0)
            return Task.FromResult(Fail(request, "Provide useSelection=true or elementIds."));

        if (!Enum.TryParse<TagOrientation>(orientationStr, ignoreCase: true, out var orientation))
            return Task.FromResult(Fail(request,
                $"Unknown orientation '{orientationStr}'. Valid values: {string.Join(", ", Enum.GetNames(typeof(TagOrientation)))}"));

        var (view, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc, doc, viewId);
        if (view == null)
            return Task.FromResult(Fail(request, viewError!));
        if (view is View3D view3d && !view3d.IsLocked)
            return Task.FromResult(Fail(request, "Tags in a 3D view require the view to be locked. Lock the view or use a plan/section view."));

        var ids = useSelection
            ? uidoc.Selection.GetElementIds().Select(id => id.Value).ToArray()
            : elementIds;
        if (ids.Length == 0)
            return Task.FromResult(Fail(request, "Nothing selected."));

        // Explicit tag type (by id or name) applies to every element; otherwise the
        // type is resolved per element category and cached.
        FamilySymbol? explicitTagType = null;
        if (tagTypeId > 0)
        {
            explicitTagType = doc.GetElement(new ElementId(tagTypeId)) as FamilySymbol;
            if (explicitTagType == null)
                return Task.FromResult(Fail(request, $"Element {tagTypeId} is not a tag family type."));
        }
        else if (!string.IsNullOrWhiteSpace(tagFamilyName) || !string.IsNullOrWhiteSpace(tagTypeName))
        {
            var matches = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s =>
                    s.Category?.CategoryType == CategoryType.Annotation &&
                    (string.IsNullOrWhiteSpace(tagFamilyName) ||
                     s.Family.Name.Contains(tagFamilyName, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(tagTypeName) ||
                     s.Name.Contains(tagTypeName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matches.Count == 0)
                return Task.FromResult(Fail(request,
                    $"No loaded tag type matches tagFamilyName='{tagFamilyName}', tagTypeName='{tagTypeName}'."));
            if (matches.Count > 1)
            {
                var sample = string.Join("; ", matches.Take(10)
                    .Select(s => $"{s.Family.Name} : {s.Name} (typeId {s.Id.Value})"));
                return Task.FromResult(Fail(request,
                    $"{matches.Count} tag types match — narrow the name or pass tagTypeId. Candidates: {sample}"));
            }
            explicitTagType = matches[0];
        }

        var offset = view.RightDirection * PlacementHelpers.MmToFt(offsetXMm)
                   + view.UpDirection * PlacementHelpers.MmToFt(offsetYMm);

        var created = new List<object>();
        var errors = new List<string>();
        var tagTypeCache = new Dictionary<long, FamilySymbol?>();

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Place Tags", () =>
        {
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var element = doc.GetElement(new ElementId(id));
                if (element == null)
                {
                    errors.Add($"Element {id}: not found.");
                    continue;
                }

                try
                {
                    var tagType = explicitTagType ?? ResolveTagTypeForCategory(doc, element, tagTypeCache);
                    if (tagType == null)
                    {
                        errors.Add($"Element {id} ({element.Category?.Name}): no matching tag family is loaded — load one or pass tagTypeId.");
                        continue;
                    }

                    if (!tagType.IsActive)
                        tagType.Activate();

                    var anchor = GetElementPoint(element, view);
                    if (anchor == null)
                    {
                        errors.Add($"Element {id}: no usable location or bounding box in this view.");
                        continue;
                    }

                    var tag = IndependentTag.Create(
                        doc, tagType.Id, view.Id, new Reference(element), addLeader, orientation, anchor + offset);

                    created.Add(new
                    {
                        tagId = tag.Id.Value,
                        taggedElementId = id,
                        tagType = $"{tagType.Family.Name} : {tagType.Name}"
                    });
                }
                catch (Exception ex)
                {
                    errors.Add($"Element {id}: {ex.Message}");
                }
            }
        });

        sw.Stop();

        if (!txSuccess)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = diagnostics.OriginalError ?? "Transaction failed — no tags were placed.",
                Errors = errors,
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created.Count > 0,
            Message = created.Count > 0
                ? $"Placed {created.Count} tag(s) in view '{view.Name}' ({errors.Count} skipped)."
                : "No tags were placed.",
            Errors = errors,
            Data = new { viewId = view.Id.Value, viewName = view.Name, createdCount = created.Count, created },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// Default tag type for an element's category. Built-in tag categories follow the
    /// naming convention OST_&lt;Category&gt;Tags (e.g. OST_FireAlarmDevices →
    /// OST_FireAlarmDeviceTags), so candidates are derived from the enum name and the
    /// first loaded tag family in the resolved category wins. Cached per category.
    /// </summary>
    private static FamilySymbol? ResolveTagTypeForCategory(
        Document doc, Element element, Dictionary<long, FamilySymbol?> cache)
    {
        var category = element.Category;
        if (category == null) return null;

        var catKey = category.Id.Value;
        if (cache.TryGetValue(catKey, out var cached)) return cached;

        FamilySymbol? resolved = null;
        var bicName = category.BuiltInCategory.ToString(); // e.g. "OST_FireAlarmDevices"
        if (bicName.StartsWith("OST_", StringComparison.Ordinal))
        {
            var baseName = bicName.Substring(4);
            var candidates = new List<string> { baseName + "Tags" };
            if (baseName.EndsWith("s", StringComparison.Ordinal))
                candidates.Add(baseName.Substring(0, baseName.Length - 1) + "Tags");
            if (baseName.EndsWith("es", StringComparison.Ordinal))
                candidates.Add(baseName.Substring(0, baseName.Length - 2) + "Tags");

            foreach (var candidate in candidates)
            {
                if (!Enum.TryParse<BuiltInCategory>("OST_" + candidate, out var tagBic))
                    continue;

                resolved = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(tagBic)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();
                if (resolved != null) break;
            }
        }

        // Last resort: a loaded multi-category tag can tag most things.
        resolved ??= new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_MultiCategoryTags)
            .Cast<FamilySymbol>()
            .FirstOrDefault();

        cache[catKey] = resolved;
        return resolved;
    }

    private static XYZ? GetElementPoint(Element element, View view)
    {
        switch (element.Location)
        {
            case LocationPoint lp:
                return lp.Point;
            case LocationCurve lc:
                return lc.Curve.Evaluate(0.5, normalized: true);
        }

        var bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        return bb == null ? null : (bb.Min + bb.Max) / 2.0;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
