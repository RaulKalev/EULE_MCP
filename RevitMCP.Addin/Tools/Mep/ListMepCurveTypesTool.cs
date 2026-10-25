using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Mep;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Mep;

/// <summary>revit_list_mep_curve_types (#106): cable tray and conduit types, their fittings and standard sizes.</summary>
public sealed class ListMepCurveTypesTool : IRevitMcpTool
{
    public string Name => "revit_list_mep_curve_types";

    public string Description =>
        "Lists the cable tray and conduit types of the model (read-only): id, name, kind, family, whether the type uses fittings, " +
        "and per fitting kind (elbow, verticalElbow, tee, cross, transition, union) whether a fitting family is assigned " +
        "(available|missing) and which. A type with missing elbows is flagged: revit_create_mep_run then creates the straight " +
        "segments but reports fitting_failed at every bend. Sizes: conduits list their standard's nominal diameters (mm, a " +
        "diameterMm must be one of them); cable tray width and height are free values, the project's cable tray size table is " +
        "listed as the usual choices. Optional: kind (cableTray|conduit), includeSizes (default true).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var kindText = ToolArguments.GetString(request.Arguments, "kind").Trim();
        var kind = kindText.Length == 0 ? null : MepKind.Normalize(kindText);
        if (kindText.Length > 0 && kind == null)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId, Success = false, Status = MepStatus.ValidationFailed,
                Message = $"kind '{kindText}' is not known: use 'cableTray', 'conduit' or leave it empty for both."
            });
        var includeSizes = ToolArguments.GetBool(request.Arguments, "includeSizes", true);

        var types = MepTypeCatalog.Types(doc, kind);
        var standards = includeSizes && kind != MepKind.CableTray ? MepTypeCatalog.ConduitStandards(doc) : new Dictionary<string, List<double>>();
        var traySizes = includeSizes && kind != MepKind.Conduit ? MepTypeCatalog.TraySizesMm(doc) : new List<double>();
        var warnings = new List<string>();

        var rows = types.Select(type =>
        {
            var typeKind = MepTypeCatalog.KindOf(type);
            var fittings = MepTypeCatalog.Fittings(doc, type);
            var missingElbows = !fittings.Has("elbow") || !fittings.Has("verticalElbow");
            string? standard = null;
            List<double>? sizes = null;
            if (includeSizes && type is ConduitType conduit)
                (standard, sizes) = MepTypeCatalog.ConduitSizesOf(doc, conduit, standards);
            return new
            {
                id = type.Id.Value,
                name = type.Name,
                kind = typeKind,
                familyName = MepTypeCatalog.FamilyName(type),
                withFittings = fittings.WithFittings,
                routingPreference = fittings.Status(),
                fittingFamilies = fittings.Families.Where(f => f.Value != null).ToDictionary(f => f.Key, f => f.Value),
                missingElbows,
                note = missingElbows
                    ? (fittings.Has("elbow")
                        ? "No vertical bend family: elbows at bends into or out of a riser will fail (fitting_failed)."
                        : "No bend family: every elbow will fail (fitting_failed); the straight segments are still created.")
                    : null,
                standard,
                sizesMm = typeKind == MepKind.Conduit ? sizes : null
            };
        }).ToList();

        if (types.Count == 0)
            warnings.Add(kind == null
                ? "The model has no cable tray or conduit types."
                : $"The model has no {MepKind.Label(kind)} types.");
        if (includeSizes && kind != MepKind.Conduit && traySizes.Count == 0 && types.Any(t => t is CableTrayType))
            warnings.Add("The project's cable tray size table is empty or unreadable; tray widths and heights are free values anyway.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{rows.Count(r => r.kind == MepKind.CableTray)} cable tray and {rows.Count(r => r.kind == MepKind.Conduit)} conduit type(s)" +
                      (rows.Any(r => r.missingElbows) ? $"; {rows.Count(r => r.missingElbows)} without (some) elbow fittings." : "."),
            Data = new
            {
                types = rows,
                cableTraySizesMm = kind == MepKind.Conduit || !includeSizes ? null : traySizes,
                cableTraySizeNote = kind == MepKind.Conduit
                    ? null
                    : "Cable tray width and height are free values (widthMm, heightMm); Revit's cable tray size table is the list of usual sizes, not a limit.",
                conduitStandards = kind == MepKind.CableTray || !includeSizes ? null : standards
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}
