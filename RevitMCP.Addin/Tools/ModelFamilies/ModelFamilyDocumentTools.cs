using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Create, inspect and edit loadable model-family documents (#169). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyDocumentTools
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new GetModelFamilyTool());
        handler.RegisterTool(new PreviewCreateModelFamilyTool());
        handler.RegisterTool(new CreateModelFamilyTool());
        handler.RegisterTool(new PreviewEditModelFamilyTool());
        handler.RegisterTool(new EditModelFamilyTool());
        ModelFamilyDocumentInspect.RegisterSections();
    }
}

/// <summary>revit_get_model_family: reads a family's sections (every feature's) without changing, saving or loading anything.</summary>
public sealed class GetModelFamilyTool : IRevitMcpTool
{
    public string Name => "revit_get_model_family";

    public string Description =>
        "Reads a loadable model family without changing it: target {family | path | familyDocument}. Returns the list of " +
        "sections and, for each one in include (default all), its data with element ids usable in later calls: family " +
        "(category, placement, hosting, settings, types), views, levels, elements (counts; rows for elementIds), and the " +
        "sections of the other model-family tools. limit caps each list (default 200).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Read(uiapp, request, "read the family", ModelFamilyDocumentInspect.Read, cancellationToken));
}

/// <summary>revit_preview_create_model_family: checks a new family from a template; creates and saves nothing.</summary>
public sealed class PreviewCreateModelFamilyTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_model_family";

    public string Description =>
        "Previews revit_create_model_family: creates the family from the template in memory, sets category and settings, " +
        "reads them back and discards it. Returns ready, problems, the placement and whether it can be placed without a " +
        "host. Nothing is saved or loaded.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: false, "create the family", ModelFamilyDocumentEdit.Run,
            cancellationToken, ModelFamilySessionMode.Create));
}

/// <summary>revit_create_model_family: a new loadable family from a template, saved to savePath and optionally loaded.</summary>
public sealed class CreateModelFamilyTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_model_family";

    public string Description =>
        "Creates a loadable model family from a template (default Metric Generic Model.rft, not hosted). Requires approval. " +
        "target {savePath, familyName?, templatePath?, overwrite?, load?, overwriteParameterValues?}. category (OST_ name or " +
        "category name) is set first, then settings; each is read back. Saved to savePath; load=true loads it into the project.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, "create the family", ModelFamilyDocumentEdit.Run,
            cancellationToken, ModelFamilySessionMode.Create));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) =>
        ModelFamilyDocumentEdit.Summary(request, ModelFamilySessionMode.Create);
}

/// <summary>revit_preview_edit_model_family: checks a category/settings edit (or a save as / reload); changes nothing.</summary>
public sealed class PreviewEditModelFamilyTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_model_family";

    public string Description =>
        "Previews revit_edit_model_family: runs the category and settings change on the family and rolls it back. Returns " +
        "ready, problems, before/after values, the placement after the edit and where the result would be saved.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: false, "edit the family", ModelFamilyDocumentEdit.Run, cancellationToken));
}

/// <summary>
/// revit_edit_model_family: changes a family's category and settings. With neither, it only saves as, saves back,
/// reloads or loads the family, as its target says.
/// </summary>
public sealed class EditModelFamilyTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_model_family";

    public string Description =>
        "Changes a model family's category and settings (workPlaneBased, alwaysVertical, shared, cutsWithVoids, " +
        "roomCalculationPoint, partType), category first, each read back. Requires approval. With neither it is the save as / " +
        "reload tool: the target does the work (path with savePath = save as; family = reload into the project; load=true = " +
        "load). Nothing else in the family is changed.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, "edit the family", ModelFamilyDocumentEdit.Run, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) =>
        ModelFamilyDocumentEdit.Summary(request, ModelFamilySessionMode.Edit);
}
