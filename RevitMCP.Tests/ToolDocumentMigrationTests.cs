using System.Text.RegularExpressions;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// #101: every tool should find its document through ToolDocument, so <c>document="&lt;title&gt;"</c> works outside
/// the documents group. Migrated tools must not read the active document directly any more, and the number of
/// direct uses left in RevitMCP.Addin/Tools may only go down: lower <see cref="MaxRemainingUses"/> with each wave.
/// </summary>
public class ToolDocumentMigrationTests
{
    /// <summary>Uses of ActiveUIDocument left in RevitMCP.Addin/Tools after wave 2 (edit and electrical writes).</summary>
    private const int MaxRemainingUses = 163;

    /// <summary>Tools migrated so far (paths under RevitMCP.Addin/Tools).</summary>
    private static readonly string[] Migrated =
    {
        // Wave 1: core and query reads
        "GetElementsInfoTool.cs", "GetElementParametersTool.cs", "CountElementsTool.cs", "FindElementsByParameterTool.cs",
        "GroupByParameterTool.cs", "GroupElementsTool.cs", "GetAvailableParametersTool.cs", "RunQueryPresetTool.cs",
        "CheckParameterCompletenessTool.cs", "ExportQueryToExcelTool.cs", Path.Combine("ParameterQA", "RunParameterQaRuleSetTool.cs"),
        // Wave 2: edit and electrical writes (with their previews)
        "AlignElementsTool.cs", "PreviewAlignElementsTool.cs", "CopyElementsTools.cs", "CreateLinesTool.cs",
        "EditFamilyTypesTool.cs", "PreviewEditFamilyTypesTool.cs", "MoveElementsTool.cs", "PreviewMoveElementsTool.cs",
        "PlaceFamilyInstancesTool.cs", "PlaceOnFaceTools.cs", "DeviceAdjustTools.cs", "SetParameterTool.cs",
        "AddElementsToCircuitTool.cs", "ApplyCircuitLoadNamesTool.cs", "ApplyCircuitNumberingTool.cs",
        "AssignDataDevicesToPatchPanelsTool.cs", "PreviewAssignDataDevicesToPatchPanelsTool.cs", "ChangeCircuitCableOrWireTypeTool.cs",
        "CreateCableTypeTool.cs", "CreateElectricalCircuitTool.cs", "DrawCircuitArcsTools.cs", "MoveElementsBetweenCircuitsTool.cs",
        "ReassignCircuitPanelTool.cs", "SetCircuitParameterTool.cs", "SetCircuitParametersBulkTool.cs", "SetCircuitPathModeTool.cs",
        // New in #101: worksets and ownership
        Path.Combine("Documents", "WorksetTools.cs"), Path.Combine("Documents", "OwnershipTools.cs"),
        Path.Combine("Documents", "OpenCloseDocumentTools.cs")
    };

    private static string ToolsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "RevitMCP.Addin", "Tools");
    }

    [Fact]
    public void MigratedToolsResolveTheirDocumentThroughToolDocument()
    {
        foreach (var relative in Migrated)
        {
            var source = File.ReadAllText(Path.Combine(ToolsFolder(), relative));
            Assert.True(!source.Contains("ActiveUIDocument"), $"{relative} reads the active document directly; use ToolDocument.Resolve / UiDocumentOf.");
            // WorksharingSupport.Workshared resolves through ToolDocument too.
            Assert.True(source.Contains("ToolDocument.") || source.Contains("WorksharingSupport.Workshared("),
                $"{relative} does not resolve its document through ToolDocument.");
        }
    }

    [Fact]
    public void DirectActiveDocumentUsesOnlyGoDown()
    {
        var count = Directory.GetFiles(ToolsFolder(), "*.cs", SearchOption.AllDirectories)
            .Sum(f => Regex.Matches(File.ReadAllText(f), "ActiveUIDocument").Count);
        Assert.True(count <= MaxRemainingUses,
            $"RevitMCP.Addin/Tools uses ActiveUIDocument {count} times; at most {MaxRemainingUses} are allowed. Resolve the document with ToolDocument instead.");
    }
}
