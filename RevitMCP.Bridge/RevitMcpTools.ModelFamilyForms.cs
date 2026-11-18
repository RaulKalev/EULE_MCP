using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Solid and void forms of a model family (#171, docs/model-family-forms.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyFormProfileNote =
        "A profile is a list of loops (first outer, rest holes): {points:[[x,y,z],…]}, {rectangle:{min,max}}, {circle:{center,radius}} or " +
        "{segments:[{line:{start,end}}|{arc:{start,mid,end}},…]}, in family mm on the work plane. Every form takes solid (default true; false = void), " +
        "workPlane (reference plane name or id, \"level\" default, or {origin,normal}; a form on a reference plane follows it), subcategory?, visible?.";

    private const string ModelFamilyFormExtrusionsDescription = "Extrusions: [{profile, start?, end | depth, …}]: offsets in mm from the work plane along its normal.";
    private const string ModelFamilyFormBlendsDescription = "Blends: [{bottomProfile, topProfile (one loop each, both given on the work plane), start?, end | depth, …}].";
    private const string ModelFamilyFormRevolvesDescription = "Revolves: [{profile, axis:{start,end} on the work plane, startAngle? (0), endAngle? (360), …}]; the profile must not cross the axis.";
    private const string ModelFamilyFormSweepsDescription = "Sweeps: [{path: segments on the work plane (open or closed), profile: loops in the profile plane's own [u,v] mm, profilePlane? start|middle|end, profileCurve? (path segment index), …}].";
    private const string ModelFamilyFormSweptBlendsDescription = "Swept blends: [{path: one line or arc on the work plane, bottomProfile, topProfile ([u,v] loops, one each), …}].";
    private const string ModelFamilyFormRemoveDescription = "Form ids to delete; runs first.";
    private const string ModelFamilyFormEditDescription = "Edits: [{form, start?, end?, startAngle?, endAngle?, visible?, subcategory?}]. form is an id or {created:\"extrusions[0]\"} (a form made by this call). Profile replacement is not supported (remove and create).";
    private const string ModelFamilyFormCutsDescription = "Void cuts: [{void, solid, remove?}] (ids or {created:…}).";
    private const string ModelFamilyFormJoinsDescription = "Joins: [{elements:[ids]}] or [{combination:id, unjoin:true}].";

    [McpServerTool(Name = "revit_preview_edit_model_family_forms", ReadOnly = true),
     Description("Previews revit_edit_model_family_forms WITHOUT changing anything: the edit runs and is rolled back. Returns ready, problems, each form's work plane and measured profile width/height/area, and what Revit would build or refuse.")]
    public Task<string> PreviewEditModelFamilyForms(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyFormRemoveDescription)] object[]? remove = null,
        [Description(ModelFamilyFormExtrusionsDescription)] object[]? extrusions = null,
        [Description(ModelFamilyFormBlendsDescription)] object[]? blends = null,
        [Description(ModelFamilyFormRevolvesDescription)] object[]? revolves = null,
        [Description(ModelFamilyFormSweepsDescription)] object[]? sweeps = null,
        [Description(ModelFamilyFormSweptBlendsDescription)] object[]? sweptBlends = null,
        [Description(ModelFamilyFormEditDescription)] object[]? edit = null,
        [Description(ModelFamilyFormCutsDescription)] object[]? cuts = null,
        [Description(ModelFamilyFormJoinsDescription)] object[]? joins = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyForms("revit_preview_edit_model_family_forms", target, remove, extrusions, blends, revolves, sweeps, sweptBlends, edit, cuts, joins, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_model_family_forms"),
     Description("Creates and edits solid and void forms in a model family. Requires approval. Runs remove, extrusions, blends, revolves, sweeps, sweptBlends, edit, cuts, joins in that order; edit/cuts/joins can name a form made in the same call as {created:\"extrusions[0]\"}. " +
                 ModelFamilyFormProfileNote + " " + ModelFamilyUnitsNote + " Returns ids, bounding boxes and size checks; revit_get_model_family include=[\"forms\"] lists faces with references. A failed step changes nothing. Preview first.")]
    public Task<string> EditModelFamilyForms(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyFormRemoveDescription)] object[]? remove = null,
        [Description(ModelFamilyFormExtrusionsDescription)] object[]? extrusions = null,
        [Description(ModelFamilyFormBlendsDescription)] object[]? blends = null,
        [Description(ModelFamilyFormRevolvesDescription)] object[]? revolves = null,
        [Description(ModelFamilyFormSweepsDescription)] object[]? sweeps = null,
        [Description(ModelFamilyFormSweptBlendsDescription)] object[]? sweptBlends = null,
        [Description(ModelFamilyFormEditDescription)] object[]? edit = null,
        [Description(ModelFamilyFormCutsDescription)] object[]? cuts = null,
        [Description(ModelFamilyFormJoinsDescription)] object[]? joins = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyForms("revit_edit_model_family_forms", target, remove, extrusions, blends, revolves, sweeps, sweptBlends, edit, cuts, joins, document, cancellationToken);

    private async Task<string> SendEditModelFamilyForms(string toolName, object? target, object[]? remove, object[]? extrusions, object[]? blends,
        object[]? revolves, object[]? sweeps, object[]? sweptBlends, object[]? edit, object[]? cuts, object[]? joins, string? document,
        CancellationToken cancellationToken)
    {
        var args = ModelFamilyArgs(target, document);
        args["remove"] = ToJToken(remove);
        args["extrusions"] = ToJToken(extrusions);
        args["blends"] = ToJToken(blends);
        args["revolves"] = ToJToken(revolves);
        args["sweeps"] = ToJToken(sweeps);
        args["sweptBlends"] = ToJToken(sweptBlends);
        args["edit"] = ToJToken(edit);
        args["cuts"] = ToJToken(cuts);
        args["joins"] = ToJToken(joins);
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}
