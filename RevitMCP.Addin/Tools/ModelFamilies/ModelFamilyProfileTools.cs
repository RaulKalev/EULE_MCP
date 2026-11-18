using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Forms from CAD profiles or family curves (#177). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyProfileTools
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new GetModelFamilyProfilesTool());
        handler.RegisterTool(new PreviewCreateModelFamilyFormFromProfileTool());
        handler.RegisterTool(new CreateModelFamilyFormFromProfileTool());
        ModelFamilyInspect.AddSection("imports",
            "CAD imports and links in the family: id, name, linked or imported, pinned, view-specific, units and scale, transform (origin mm, rotation, mirrored), layers with curve counts.",
            ModelFamilyProfileSource.ImportsSection);
    }
}

/// <summary>
/// revit_get_model_family_profiles: analyses CAD line work or family curves into closed loops (outer loops, holes,
/// islands), open chains and issues, on the plane the curves lie in. Changes nothing.
/// </summary>
public sealed class GetModelFamilyProfilesTool : IRevitMcpTool
{
    public string Name => "revit_get_model_family_profiles";

    public string Description =>
        "Analyses the curves of a CAD import (layers), chosen family curves or a view's lines into closed loops on their plane: " +
        "each loop's index, kind, width, height, area, location, layers, parent and depth (holes), plus open chains with their gaps " +
        "and issues (duplicates, overlaps, crossings, branching points, short segments, approximated splines). Changes nothing.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Read(uiapp, request, ModelFamilyProfileExecutor.ReadWhat, ModelFamilyProfileExecutor.Analyse, cancellationToken));
}

/// <summary>revit_preview_create_model_family_form_from_profile: runs the creation and rolls it back; changes nothing.</summary>
public sealed class PreviewCreateModelFamilyFormFromProfileTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_model_family_form_from_profile";

    public string Description =>
        "Previews revit_create_model_family_form_from_profile without changing anything: the same analysis and creation run inside " +
        "a transaction group and are rolled back, so it reports the loops used, measured sizes, bounding boxes and cuts, or why not.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: false, ModelFamilyProfileExecutor.CreateWhat, ModelFamilyProfileExecutor.Create, cancellationToken));
}

/// <summary>
/// revit_create_model_family_form_from_profile: extrudes loops found by revit_get_model_family_profiles into editable
/// solid or void extrusions (#171's builder), with their holes, and optionally cuts a solid and removes the source curves.
/// </summary>
public sealed class CreateModelFamilyFormFromProfileTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_model_family_form_from_profile";

    public string Description =>
        "Creates editable solid or void extrusions from loops of CAD line work or family curves. Requires approval. Every form " +
        "is checked first (loop, holes, expected size, profile); an invalid outline changes nothing.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, ModelFamilyProfileExecutor.CreateWhat, ModelFamilyProfileExecutor.Create, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
        => $"Create {ModelFamilyProfileExecutor.Plan(request)} in {ModelFamilySession.DescribeTarget(request)}.";
}
