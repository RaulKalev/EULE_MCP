using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Model-family reference planes, reference lines, dimensions and constraints (#170, docs/model-family-references.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyRefNote =
        "A ref is a reference plane name or id, a reference line or level id, \"level\", {element, face: left|right|front|back|top|bottom|[nx,ny,nz]}, " +
        "{element, instanceReference: left…top|strong|weak|<name>} or {reference: <stable representation>}.";

    private const string ModelFamilyRemoveDescription =
        "Element ids of reference planes, reference lines, dimensions or alignments to delete. Runs first.";

    private const string ModelFamilyReferencePlanesDescription =
        "Runs second. Create {name, origin, direction, normal?|cutVector?, length? (1200), view?, isReference?, definesOrigin?}; " +
        "offset {name, offset:{from: plane, distance}, isReference?, definesOrigin?}; edit {plane, rename?, isReference?, definesOrigin?, move? (along normal)}. " +
        "isReference: notAReference|strong|weak|left|centerLeftRight|right|front|centerFrontBack|back|bottom|centerElevation|top.";

    private const string ModelFamilyReferenceLinesDescription =
        "Runs third. Create {start, end, workPlane? (plane name/id, \"level\"), isReference?}; edit {line: id, isReference}.";

    private const string ModelFamilyDimensionsDescription =
        "Runs fourth. Create {references: [ref, ref, …], kind? (linear|angular), view?, linePoint?, radius? (angular), label? (family parameter), lock?, equal? (3+ refs)}; " +
        "edit {dimension: id, label? (null removes), lock?, equal?}. No value: set parameter values with the parameters tool.";

    private const string ModelFamilyAlignmentsDescription =
        "Runs last. {reference: ref (stays), element: ref (aligned to it, must already coincide), view?, lock? (true)}.";

    [McpServerTool(Name = "revit_preview_edit_model_family_references", ReadOnly = true),
     Description("Previews revit_edit_model_family_references WITHOUT changing anything: runs the edit in the family and rolls it back. Returns ready, problems, the planes, lines, dimensions and alignments it would make, and constraints Revit refuses with the request item (e.g. dimensions[2]) that caused them. " + ModelFamilyUnitsNote)]
    public Task<string> PreviewEditModelFamilyReferences(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyRemoveDescription)] object[]? remove = null,
        [Description(ModelFamilyReferencePlanesDescription)] object[]? referencePlanes = null,
        [Description(ModelFamilyReferenceLinesDescription)] object[]? referenceLines = null,
        [Description(ModelFamilyDimensionsDescription + " " + ModelFamilyRefNote)] object[]? dimensions = null,
        [Description(ModelFamilyAlignmentsDescription)] object[]? alignments = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyReferences("revit_preview_edit_model_family_references", target, remove, referencePlanes, referenceLines,
            dimensions, alignments, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_model_family_references"),
     Description("Creates and edits reference planes (names, Is Reference, Defines Origin), reference lines, dimensions (labelled with family parameters, locked, EQ) and alignments in a model family. Requires approval. Runs remove, referencePlanes, referenceLines, dimensions, alignments in that order, each as one step; a constraint Revit refuses rolls the whole edit back and names the request item that caused it. Returns ids, names and stable references for later calls. Run revit_preview_edit_model_family_references first. " + ModelFamilyUnitsNote)]
    public Task<string> EditModelFamilyReferences(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyRemoveDescription)] object[]? remove = null,
        [Description(ModelFamilyReferencePlanesDescription)] object[]? referencePlanes = null,
        [Description(ModelFamilyReferenceLinesDescription)] object[]? referenceLines = null,
        [Description(ModelFamilyDimensionsDescription + " " + ModelFamilyRefNote)] object[]? dimensions = null,
        [Description(ModelFamilyAlignmentsDescription)] object[]? alignments = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyReferences("revit_edit_model_family_references", target, remove, referencePlanes, referenceLines,
            dimensions, alignments, document, cancellationToken);

    private async Task<string> SendEditModelFamilyReferences(string toolName, object? target, object[]? remove, object[]? referencePlanes,
        object[]? referenceLines, object[]? dimensions, object[]? alignments, string? document, CancellationToken cancellationToken)
    {
        var args = ModelFamilyArgs(target, document);
        args["remove"] = ToJToken(remove);
        args["referencePlanes"] = ToJToken(referencePlanes);
        args["referenceLines"] = ToJToken(referenceLines);
        args["dimensions"] = ToJToken(dimensions);
        args["alignments"] = ToJToken(alignments);
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}
