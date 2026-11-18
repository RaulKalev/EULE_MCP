using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Flex tests of family types and checks of placed instances (#176). Registers its tools; it owns no inspection section.</summary>
internal static class ModelFamilyFlexTools
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new FlexModelFamilyTool());
        handler.RegisterTool(new CheckModelFamilyInstancesTool());
    }
}

/// <summary>
/// revit_flex_model_family: exercises every family type and caller-given parameter ranges, regenerates each case,
/// checks expectations and reports refusals with the constraints Revit names. Every change is undone: each case is
/// restored and read back, and the session rolls the whole trial back.
/// </summary>
public sealed class FlexModelFamilyTool : IRevitMcpTool
{
    public string Name => "revit_flex_model_family";

    public string Description =>
        "Flex-tests a model family: runs every type (or the named " + ModelFamilyFlexArgs.Types + "), each " + ModelFamilyFlexArgs.Ranges +
        " value and explicit " + ModelFamilyFlexArgs.Cases + ", regenerates, evaluates " + ModelFamilyFlexArgs.Checks +
        " (bounding box, planes, faces, centring, parameters, materials, connectors, nested symbols, cuts) and reports refused cases with " +
        "the constraints Revit names. Restores every value after each case and rolls the trial back; " + ModelFamilyFlexArgs.ReopenCopy +
        " repeats the type cases on a saved and reopened copy. Changes nothing.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilyFlexExecutor.Execute(uiapp, request, cancellationToken));
}

/// <summary>revit_check_model_family_instances: checks placed instances of a loaded family in a project, in family coordinates.</summary>
public sealed class CheckModelFamilyInstancesTool : IRevitMcpTool
{
    public string Name => "revit_check_model_family_instances";

    public string Description =>
        "Checks placed instances of a loaded family (" + ModelFamilyFlexArgs.Family + " and/or " + ModelFamilyFlexArgs.ElementIds + ", optional " +
        ModelFamilyFlexArgs.Types + ") against " + ModelFamilyFlexArgs.Checks + ": bounding box and centring in the instance's own (family) " +
        "coordinates, parameters, materials, connectors (origin, direction, domain, system type, electrical values, system membership), " +
        "hosting and instance counts per type. Returns rows with element ids and missingTypes. Reads only.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilyFlexInstances.Execute(uiapp, request, cancellationToken));
}
