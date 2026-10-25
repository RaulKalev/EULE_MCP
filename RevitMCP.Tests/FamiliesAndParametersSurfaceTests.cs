using RevitMCP.Addin.Plans;
using RevitMCP.Bridge;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#102: the families and parameters tools sit in the edit group, carry the right permissions and are classified for plans.</summary>
public class FamiliesAndParametersSurfaceTests
{
    private static readonly string[] Tools =
    {
        "revit_list_families", "revit_preview_load_family", "revit_load_family", "revit_reload_family",
        "revit_preview_purge_unused", "revit_purge_unused", "revit_list_project_parameters", "revit_list_shared_parameters",
        "revit_preview_create_parameters", "revit_create_parameters", "revit_bind_parameter_categories"
    };

    [Fact]
    public void EveryToolIsInTheEditGroup()
    {
        foreach (var tool in Tools) Assert.Equal("edit", BridgeToolGroups.GroupOf(tool));
        Assert.Contains("families", BridgeToolGroups.Descriptions["edit"]);
        Assert.Contains("parameters", BridgeToolGroups.Descriptions["edit"]);
    }

    [Theory]
    [InlineData("revit_list_families", "ReadOnly")]
    [InlineData("revit_preview_load_family", "ReadOnly")]
    [InlineData("revit_load_family", "RequiresApproval")]
    [InlineData("revit_reload_family", "RequiresApproval")]
    [InlineData("revit_preview_purge_unused", "ReadOnly")]
    [InlineData("revit_purge_unused", "DestructiveRequiresManualApproval")]
    [InlineData("revit_list_project_parameters", "ReadOnly")]
    [InlineData("revit_list_shared_parameters", "ReadOnly")]
    [InlineData("revit_preview_create_parameters", "ReadOnly")]
    [InlineData("revit_create_parameters", "RequiresApproval")]
    [InlineData("revit_bind_parameter_categories", "RequiresApproval")]
    public void Permissions(string tool, string permission)
    {
        Assert.Equal(permission, ToolSourceScan.Addin.Value[tool].Permission);
        var bridge = Assert.Single(ToolSourceScan.Bridge.Value, b => b.Name == tool);
        Assert.Equal(permission == "ReadOnly", bridge.ReadOnly);
        Assert.Contains(tool, bridge.Targets);
    }

    [Fact]
    public void PurgeIsDestructiveOnTheBridge()
    {
        var source = string.Concat(Directory.GetFiles(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge"), "RevitMcpTools*.cs").Select(File.ReadAllText));
        Assert.Contains("[McpServerTool(Name = \"revit_purge_unused\", Destructive = true)", source);
    }

    [Theory]
    [InlineData("revit_load_family", PlanToolKind.Creates)]
    [InlineData("revit_create_parameters", PlanToolKind.Creates)]
    [InlineData("revit_reload_family", PlanToolKind.Modifies)]
    [InlineData("revit_purge_unused", PlanToolKind.Modifies)]
    [InlineData("revit_bind_parameter_categories", PlanToolKind.Modifies)]
    public void PlanClassification(string tool, PlanToolKind kind) => Assert.Equal(kind, PlanToolClassification.Classify(tool));
}
