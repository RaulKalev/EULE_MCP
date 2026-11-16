using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Change the type of existing instances (#159, docs/change-element-type.md).</summary>
internal sealed partial class RevitMcpTools
{
    [McpServerTool(Name = "revit_preview_change_element_type", ReadOnly = true),
     Description("Previews changing the type of existing instances WITHOUT changing the model. Same arguments as revit_change_element_type. Reports each element as switchable, unchanged (already that type) or blocked with a reason (notFound, isElementType, linkedDocument, noType, inGroup, pinned, notEditable, categoryMismatch, invalidForElement), the target type and counts with up to sampleSize examples.")]
    public Task<string> PreviewChangeElementType(
        [Description("Instance ids to change. Combine with useSelection.")] long[]? elementIds = null,
        [Description("Also take the elements selected in Revit (active document only).")] bool useSelection = false,
        [Description("Target type id (preferred).")] long typeId = 0,
        [Description("Target family name (with typeName) when several families share a type name.")] string? familyName = null,
        [Description("Target type name; must match exactly one type, otherwise the call is refused.")] string? typeName = null,
        [Description("Target category name, to disambiguate a type name.")] string? categoryName = null,
        [Description("Change nothing when any element is blocked or fails (default false).")] bool requireAll = false,
        [Description("Example rows to return per status (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendChangeElementType("revit_preview_change_element_type", elementIds, useSelection, typeId, familyName, typeName, categoryName, requireAll, sampleSize, document, cancellationToken);

    [McpServerTool(Name = "revit_change_element_type"),
     Description("Changes the type of existing instances (Element.ChangeTypeId) without deleting and recreating them. Requires approval. Elements: elementIds and/or useSelection. Target: typeId, or typeName (+ familyName / categoryName); an ambiguous name is refused. Same category only, accepted by Revit per element; grouped, pinned, linked or owned-by-others elements are blocked. requireAll=true is all-or-nothing. Revit may return a new element id: every oldId to newId mapping is reported. One transaction, one undo step. Run revit_preview_change_element_type first.")]
    public Task<string> ChangeElementType(
        [Description("Instance ids to change. Combine with useSelection.")] long[]? elementIds = null,
        [Description("Also take the elements selected in Revit (active document only).")] bool useSelection = false,
        [Description("Target type id (preferred).")] long typeId = 0,
        [Description("Target family name (with typeName) when several families share a type name.")] string? familyName = null,
        [Description("Target type name; must match exactly one type, otherwise the call is refused.")] string? typeName = null,
        [Description("Target category name, to disambiguate a type name.")] string? categoryName = null,
        [Description("Change nothing when any element is blocked or fails (default false).")] bool requireAll = false,
        [Description("Example rows to return per status (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendChangeElementType("revit_change_element_type", elementIds, useSelection, typeId, familyName, typeName, categoryName, requireAll, sampleSize, document, cancellationToken);

    private async Task<string> SendChangeElementType(
        string toolName, long[]? elementIds, bool useSelection, long typeId, string? familyName, string? typeName, string? categoryName,
        bool requireAll, int sampleSize, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementIds"] = elementIds ?? [],
            ["useSelection"] = useSelection,
            ["typeId"] = typeId,
            ["familyName"] = familyName ?? string.Empty,
            ["typeName"] = typeName ?? string.Empty,
            ["categoryName"] = categoryName ?? string.Empty,
            ["requireAll"] = requireAll,
            ["sampleSize"] = sampleSize,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}
