using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Reference planes, reference lines, dimensions and constraints of a model family (#170). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyReferenceTools
{
    public const string What = "edit reference planes, dimensions and constraints";

    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new PreviewEditModelFamilyReferencesTool());
        handler.RegisterTool(new EditModelFamilyReferencesTool());
        ModelFamilyReferenceInspect.RegisterSections();
    }
}

/// <summary>revit_preview_edit_model_family_references: runs the edit and rolls it back; reports what Revit would do.</summary>
public sealed class PreviewEditModelFamilyReferencesTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_model_family_references";

    public string Description =>
        "Previews revit_edit_model_family_references without changing anything: runs the same edit (remove, referencePlanes, " +
        "referenceLines, dimensions, alignments) in the family and rolls it back, so constraints Revit refuses are reported " +
        "with the request items that caused them.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: false, ModelFamilyReferenceTools.What, ModelFamilyReferenceExecutor.Run, cancellationToken));
}

/// <summary>revit_edit_model_family_references: reference planes, reference lines, dimensions, labels, locks and alignments.</summary>
public sealed class EditModelFamilyReferencesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_model_family_references";

    public string Description =>
        "Edits the reference planes, reference lines, dimensions and constraints of a model family. Requires approval. " +
        "Runs in order: remove (ids), referencePlanes (create, offset from a plane, or edit: rename, isReference, " +
        "definesOrigin, move), referenceLines, dimensions (linear or angular; label with a family parameter, lock, equal) " +
        "and alignments (lock a coincident face or plane to a reference). A refused constraint rolls the whole edit back. " +
        "Run revit_preview_edit_model_family_references first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, ModelFamilyReferenceTools.What, ModelFamilyReferenceExecutor.Run, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
        => $"Edit reference planes, dimensions and constraints ({ModelFamilyReferenceExecutor.Describe(request)}) of {ModelFamilySession.DescribeTarget(request)}.";
}
