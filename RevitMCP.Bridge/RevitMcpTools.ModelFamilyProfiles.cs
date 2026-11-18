using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Forms from CAD profiles or family curves (#177, docs/model-family-profiles.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyProfileSourceDescription =
        "Curves to analyse: {import: id | name (default: the family's only CAD import), layers?: [names]}, {curves: [family curve ids]} " +
        "(model, symbolic or reference lines) or {view: name | id, subcategory?: line subcategory}.";

    private const string ModelFamilyProfileWorkPlaneDescription =
        "Plane to analyse on: reference plane name or id, \"level\", or {origin, normal}. Default: the plane the curves lie in (within 0.5 mm), " +
        "matched to the reference level or a reference plane.";

    private const string ModelFamilyProfileToleranceDescription = "Endpoint join tolerance in mm (default 0.05, max 1).";

    private const string ModelFamilyProfileFormsDescription =
        "Forms: [{loop: index | {at:[x,y,z]} (innermost loop around the point), holes? \"auto\" (default: loops directly inside) | \"none\" | [indices], " +
        "solid? (default true; false = void), depth | start?+end (mm along the work plane normal), subcategory?, visible?, " +
        "cut? (a void: solid id or {created:\"forms[0]\"}), name?, expected? {width?, height?, centre?, tolerance? (0.5)}}].";

    [McpServerTool(Name = "revit_get_model_family_profiles", ReadOnly = true),
     Description("Analyses CAD line work in a family (an import's layers), family curves or a view's lines into closed loops on their plane. Returns source (layers with counts, units, transform), workPlane, loops (index, kind, width, height, area, min/max/centre mm, layers, parent, depth, usable), open chains with end gaps, and issues (duplicates, overlaps, crossings, branches, short segments, approximated splines). Changes nothing. " + ModelFamilyUnitsNote)]
    public async Task<string> GetModelFamilyProfiles(
        [Description(ModelFamilyReadTargetDescription)] object? target = null,
        [Description(ModelFamilyProfileSourceDescription)] object? source = null,
        [Description(ModelFamilyProfileWorkPlaneDescription)] object? workPlane = null,
        [Description(ModelFamilyProfileToleranceDescription)] double? tolerance = null,
        [Description("Most items per list (default 200, 1-2000).")] int limit = 200,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ModelFamilyArgs(target, document);
        args["source"] = ToJToken(source);
        args["workPlane"] = ToJToken(workPlane);
        args["tolerance"] = tolerance;
        args["limit"] = limit;
        return FormatResult(await pipeClient.SendAsync("revit_get_model_family_profiles", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_model_family_form_from_profile", ReadOnly = true),
     Description("Previews revit_create_model_family_form_from_profile WITHOUT changing anything: runs the same analysis and creation and rolls it back. Returns ready, problems, the loops and holes each form uses, measured size and location, and what Revit builds or refuses.")]
    public Task<string> PreviewCreateModelFamilyFormFromProfile(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyProfileSourceDescription)] object? source = null,
        [Description(ModelFamilyProfileWorkPlaneDescription)] object? workPlane = null,
        [Description(ModelFamilyProfileToleranceDescription)] double? tolerance = null,
        [Description(ModelFamilyProfileFormsDescription)] object[]? forms = null,
        [Description("Delete the family curves the forms were made from, after the forms exist (family curve sources only; default false).")] bool removeSourceCurves = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendModelFamilyFormFromProfile("revit_preview_create_model_family_form_from_profile", target, source, workPlane, tolerance, forms, removeSourceCurves, document, cancellationToken);

    [McpServerTool(Name = "revit_create_model_family_form_from_profile"),
     Description("Creates editable solid or void extrusions in a model family from loops of CAD line work or family curves (loop indices from revit_get_model_family_profiles; same source, workPlane, tolerance). Requires approval. Holes default to the loops directly inside. A loop that is open, crosses itself or misses expected is refused before anything changes. Returns form ids, loops used, measured size, bounding box and cuts. Preview first. " + ModelFamilyUnitsNote)]
    public Task<string> CreateModelFamilyFormFromProfile(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyProfileSourceDescription)] object? source = null,
        [Description(ModelFamilyProfileWorkPlaneDescription)] object? workPlane = null,
        [Description(ModelFamilyProfileToleranceDescription)] double? tolerance = null,
        [Description(ModelFamilyProfileFormsDescription)] object[]? forms = null,
        [Description("Delete the family curves the forms were made from, after the forms exist (family curve sources only; default false).")] bool removeSourceCurves = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendModelFamilyFormFromProfile("revit_create_model_family_form_from_profile", target, source, workPlane, tolerance, forms, removeSourceCurves, document, cancellationToken);

    private async Task<string> SendModelFamilyFormFromProfile(string toolName, object? target, object? source, object? workPlane, double? tolerance,
        object[]? forms, bool removeSourceCurves, string? document, CancellationToken cancellationToken)
    {
        var args = ModelFamilyArgs(target, document);
        args["source"] = ToJToken(source);
        args["workPlane"] = ToJToken(workPlane);
        args["tolerance"] = tolerance;
        args["forms"] = ToJToken(forms);
        args["removeSourceCurves"] = removeSourceCurves;
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}
