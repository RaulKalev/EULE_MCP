using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PlaceFamilyInstancesTool : IRevitMcpTool
{
    public string Name => "revit_place_family_instances";
    public string Description => "Places instances of a loaded family type at given points (mm). Handles model components (level-based) and detail items (view-based) automatically from the family's placement type. Optional per-point rotation in degrees. Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var familyName = ToolArguments.GetString(request.Arguments, "familyName");
        var typeName = ToolArguments.GetString(request.Arguments, "typeName");
        var typeId = ToolArguments.GetLong(request.Arguments, "typeId");
        var levelName = ToolArguments.GetString(request.Arguments, "levelName");
        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var hostElementId = ToolArguments.GetLong(request.Arguments, "hostElementId");

        if (!request.Arguments.TryGetValue("placements", out var rawPlacements) || rawPlacements == null)
            return Task.FromResult(Fail(request, "Provide 'placements': a JSON array of {x, y, z, rotationDegrees} points in millimetres."));
        if (ToJArray(rawPlacements) is not JArray placements || placements.Count == 0)
            return Task.FromResult(Fail(request, "'placements' could not be parsed as a non-empty JSON array of {x, y, z, rotationDegrees}."));

        var (symbol, symbolError) = ResolveSymbol(doc, typeId, familyName, typeName);
        if (symbol == null)
            return Task.FromResult(Fail(request, symbolError!));

        var placementType = symbol.Family.FamilyPlacementType;

        // View-based families (detail items, some annotations) need a target view;
        // everything else places into the model, optionally on a level or host.
        View? view = null;
        if (placementType == FamilyPlacementType.ViewBased)
        {
            var (resolvedView, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc, doc, viewId);
            if (resolvedView == null)
                return Task.FromResult(Fail(request, viewError!));
            view = resolvedView;
        }

        Level? explicitLevel = null;
        if (!string.IsNullOrWhiteSpace(levelName))
        {
            explicitLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
            if (explicitLevel == null)
                return Task.FromResult(Fail(request, $"Level '{levelName}' not found."));
        }

        Element? host = null;
        if (hostElementId > 0)
        {
            host = doc.GetElement(new ElementId(hostElementId));
            if (host == null)
                return Task.FromResult(Fail(request, $"Host element {hostElementId} not found."));
        }

        var created = new List<object>();
        var errors = new List<string>();

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Place Family Instances", () =>
        {
            if (!symbol.IsActive)
                symbol.Activate();

            int index = 0;
            foreach (var token in placements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                var point = PlacementHelpers.PointFromMm(
                    PlacementHelpers.TokenDouble(token, "x"),
                    PlacementHelpers.TokenDouble(token, "y"),
                    PlacementHelpers.TokenDouble(token, "z"));
                var rotationDegrees = PlacementHelpers.TokenDouble(token, "rotationDegrees");

                try
                {
                    var instance = CreateInstance(doc, uidoc, symbol, placementType, point, view, explicitLevel, host);

                    if (Math.Abs(rotationDegrees) > 1e-9)
                    {
                        var axisDirection = view != null ? view.ViewDirection : XYZ.BasisZ;
                        var axis = Line.CreateBound(point, point + axisDirection);
                        ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotationDegrees * Math.PI / 180.0);
                    }

                    created.Add(new
                    {
                        elementId = instance.Id.Value,
                        x = PlacementHelpers.TokenDouble(token, "x"),
                        y = PlacementHelpers.TokenDouble(token, "y"),
                        z = PlacementHelpers.TokenDouble(token, "z")
                    });
                }
                catch (Exception ex)
                {
                    errors.Add($"Placement {index}: {ex.Message}");
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
                Message = diagnostics.OriginalError ?? "Transaction failed — no instances were placed.",
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
                ? $"Placed {created.Count} of {placements.Count} instance(s) of {symbol.Family.Name} : {symbol.Name}."
                : "No instances were placed.",
            Errors = errors,
            Data = new
            {
                familyName = symbol.Family.Name,
                typeName = symbol.Name,
                typeId = symbol.Id.Value,
                placementType = placementType.ToString(),
                createdCount = created.Count,
                created
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static FamilyInstance CreateInstance(
        Document doc,
        UIDocument uidoc,
        FamilySymbol symbol,
        FamilyPlacementType placementType,
        XYZ point,
        View? view,
        Level? explicitLevel,
        Element? host)
    {
        switch (placementType)
        {
            case FamilyPlacementType.ViewBased:
                return doc.Create.NewFamilyInstance(point, symbol, view);

            case FamilyPlacementType.OneLevelBased:
            case FamilyPlacementType.TwoLevelsBased:
            {
                var level = ResolveLevel(doc, uidoc, explicitLevel, point)
                    ?? throw new InvalidOperationException("No level found in the model — pass levelName.");
                if (host != null)
                    return doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural);
                return doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
            }

            case FamilyPlacementType.OneLevelBasedHosted:
            {
                if (host == null)
                    throw new InvalidOperationException(
                        $"Family '{symbol.Family.Name}' is host-based ({placementType}) — pass hostElementId (e.g. the wall to place into).");
                var level = ResolveLevel(doc, uidoc, explicitLevel, point);
                return level != null
                    ? doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(point, symbol, host, StructuralType.NonStructural);
            }

            case FamilyPlacementType.WorkPlaneBased:
            {
                if (host != null)
                    return doc.Create.NewFamilyInstance(point, symbol, host, StructuralType.NonStructural);
                // Generic overload works for many work-plane-based families when the
                // document has an implicit placement plane; otherwise Revit throws and
                // the per-item error explains what to pass.
                return doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            }

            default:
                throw new InvalidOperationException(
                    $"Family placement type '{placementType}' is not supported by this tool (family '{symbol.Family.Name}').");
        }
    }

    private static Level? ResolveLevel(Document doc, UIDocument uidoc, Level? explicitLevel, XYZ point)
    {
        if (explicitLevel != null) return explicitLevel;
        if (uidoc.ActiveView is ViewPlan plan && plan.GenLevel != null) return plan.GenLevel;
        return PlacementHelpers.NearestLevel(doc, point.Z);
    }

    private static (FamilySymbol? Symbol, string? Error) ResolveSymbol(
        Document doc, long typeId, string familyName, string typeName)
    {
        if (typeId > 0)
        {
            if (doc.GetElement(new ElementId(typeId)) is FamilySymbol byId)
                return (byId, null);
            return (null, $"Element {typeId} is not a family type (FamilySymbol).");
        }

        if (string.IsNullOrWhiteSpace(familyName) && string.IsNullOrWhiteSpace(typeName))
            return (null, "Provide typeId, or familyName and/or typeName to pick the family type to place.");

        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s =>
                (string.IsNullOrWhiteSpace(familyName) ||
                 s.Family.Name.Contains(familyName, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(typeName) ||
                 s.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Prefer exact (case-insensitive) matches before partial ones.
        var exact = candidates.Where(s =>
            (string.IsNullOrWhiteSpace(familyName) ||
             string.Equals(s.Family.Name, familyName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(typeName) ||
             string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0) candidates = exact;

        if (candidates.Count == 0)
            return (null, $"No loaded family type matches familyName='{familyName}', typeName='{typeName}'.");

        if (candidates.Count > 1)
        {
            var sample = string.Join("; ", candidates.Take(10)
                .Select(s => $"{s.Family.Name} : {s.Name} (typeId {s.Id.Value})"));
            return (null, $"{candidates.Count} family types match — narrow the name or pass typeId. Candidates: {sample}");
        }

        return (candidates[0], null);
    }

    private static JArray? ToJArray(object value) =>
        value as JArray ?? (value is string s ? ToolArguments.TryParseJArray(s) : null);

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}
