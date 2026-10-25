using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Jobs;
using RevitMCP.Addin.Plans;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>H10: plans and jobs accept the bridge's combined tool names and expand them to the add-in tools.</summary>
public class BridgeToolRoutesTests
{
    private static readonly Dictionary<string, ToolPermission> Permissions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["revit_set_view_parameters_bulk"] = ToolPermission.RequiresApproval,
        ["revit_set_sheet_parameters_bulk"] = ToolPermission.RequiresApproval,
        ["revit_rename_views"] = ToolPermission.RequiresApproval,
        ["revit_rename_sheets"] = ToolPermission.RequiresApproval,
        ["revit_duplicate_views"] = ToolPermission.RequiresApproval,
        ["revit_duplicate_sheets"] = ToolPermission.RequiresApproval,
        ["revit_duplicate_family_types"] = ToolPermission.RequiresApproval,
        ["revit_delete_views"] = ToolPermission.DestructiveRequiresManualApproval,
        ["revit_delete_sheets"] = ToolPermission.DestructiveRequiresManualApproval,
        ["revit_delete_elements"] = ToolPermission.DestructiveRequiresManualApproval,
        ["revit_detect_hard_clashes"] = ToolPermission.ReadOnly,
        ["revit_detect_clearance_clashes"] = ToolPermission.ReadOnly,
        ["revit_graph_query"] = ToolPermission.ReadOnly,
        ["revit_set_parameter"] = ToolPermission.RequiresApproval
    };

    private static ToolPermission? PermissionOf(string name) => Permissions.TryGetValue(name, out var p) ? p : null;

    private static PlanDefinition Plan(params string[] tools)
        => PlanEnvelopeParser.Parse(new Dictionary<string, object?>
        {
            ["title"] = "Rename and stamp sheets",
            ["envelope"] = JToken.FromObject(new { tools })
        }, PermissionOf, _ => false);

    private static JobDefinition Job(params object[] steps)
    {
        var args = JObject.FromObject(new { title = "Sheets", steps }).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        return JobDefinitionParser.Parse(args, PermissionOf, _ => false);
    }

    [Fact]
    public void PlanAcceptsCombinedBridgeToolsAndCoversTheirAddinTools()
    {
        var def = Plan("revit_set_parameters_bulk", "revit_rename", "revit_set_parameter");

        Assert.True(def.IsValid, string.Join("; ", def.Errors));
        Assert.Equal(new[]
        {
            "revit_set_view_parameters_bulk", "revit_set_sheet_parameters_bulk", "revit_rename_views", "revit_rename_sheets", "revit_set_parameter"
        }, def.Envelope.Tools);
        Assert.Contains(def.Warnings, w => w.Contains("'revit_rename' is covered as revit_rename_views, revit_rename_sheets"));
        Assert.Contains("revit_set_view_parameters_bulk", PlanSummary.Build(def));
    }

    [Fact]
    public void PlanStillRefusesCombinedDeletesAndSkipsReadOnlyCombinedTools()
    {
        var def = Plan("revit_delete", "revit_detect_clashes", "revit_duplicate");

        Assert.Contains(def.Errors, e => e.Contains("'revit_delete' is destructive"));
        Assert.Contains(def.Warnings, w => w.Contains("'revit_detect_clashes' needs no approval"));
        Assert.Equal(new[] { "revit_duplicate_sheets", "revit_duplicate_views", "revit_duplicate_family_types" }, def.Envelope.Tools);
    }

    [Fact]
    public void NamingABridgeToolAndItsAddinToolsKeepsEachOnce()
    {
        var def = Plan("revit_rename_views", "revit_rename");
        Assert.Equal(new[] { "revit_rename_views", "revit_rename_sheets" }, def.Envelope.Tools);
    }

    [Fact]
    public void JobStepWithACombinedToolRunsAsTheAddinToolItsArgumentPicks()
    {
        var def = Job(
            new { tool = "revit_graph_query", arguments = new { op = "find" }, bind = new { ids = "$.data.items[*].id" } },
            new { tool = "revit_set_parameters_bulk", arguments = new { entity = "Sheets", sheetIds = "{{ids}}", parameters = new { Drawn = "RK" } } },
            new { tool = "revit_rename", arguments = new { entity = "views", mode = "PrefixSuffix", prefix = "EN-" } });

        Assert.True(def.IsValid, string.Join("; ", def.Errors));
        Assert.Equal("revit_set_sheet_parameters_bulk", def.Job.Steps[1].Tool);
        Assert.Null(def.Job.Steps[1].Arguments["entity"]);
        Assert.Equal("{{ids}}", (string?)def.Job.Steps[1].Arguments["sheetIds"]);
        Assert.Equal("revit_rename_views", def.Job.Steps[2].Tool);
        Assert.Equal("PrefixSuffix", (string?)def.Job.Steps[2].Arguments["mode"]);
        Assert.Equal(new[] { "revit_set_sheet_parameters_bulk", "revit_rename_views" }, def.Plan.Envelope.Tools);
    }

    [Fact]
    public void JobStepWithACombinedToolNeedsALiteralRouteValue()
    {
        var def = Job(
            new { tool = "revit_graph_query", arguments = new { op = "find" }, bind = new { kind = "$.data.kind" } },
            new { tool = "revit_set_parameters_bulk", arguments = new { parameters = new { Drawn = "RK" } } },
            new { tool = "revit_rename", arguments = new { entity = "{{kind}}", mode = "Template" } },
            new { tool = "revit_duplicate", arguments = new { entity = "rooms" } });

        Assert.Contains(def.Errors, e => e.StartsWith("step 2: revit_set_parameters_bulk needs arguments.entity = views | sheets"));
        Assert.Contains(def.Errors, e => e.StartsWith("step 3: revit_rename: arguments.entity must be a literal value"));
        Assert.Contains(def.Errors, e => e.StartsWith("step 4: revit_duplicate: arguments.entity 'rooms' is not one of"));
        Assert.DoesNotContain(def.Errors, e => e.Contains("unknown tool"));
    }

    [Fact]
    public void JobEnvelopeWithBridgeNamesIsExpandedToo()
    {
        var args = JObject.FromObject(new
        {
            title = "Sheets",
            steps = new object[] { new { tool = "revit_rename", arguments = new { entity = "sheets", mode = "Template" } } },
            envelope = new { tools = new[] { "revit_rename" } }
        }).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        var def = JobDefinitionParser.Parse(args, PermissionOf, _ => false);

        Assert.True(def.IsValid, string.Join("; ", def.Errors));
        Assert.Contains("revit_rename_sheets", def.Plan.Envelope.Tools);
        Assert.DoesNotContain(def.Warnings, w => w.Contains("is not in envelope.tools"));
    }

    // ── Drift guard: the table must match what RevitMCP.Bridge/RevitMcpTools*.cs actually sends ──────────

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>Every tool name the add-in registers (Name => "…" / ToolName = "…" in RevitMCP.Addin/Tools).</summary>
    private static HashSet<string> AddinTools()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(Root(), "RevitMCP.Addin", "Tools"), "*.cs", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"(?:Name\s*=>\s*|ToolName\s*=\s*)""([a-z][a-z0-9_]+)"""))
                names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>Bridge tool → the add-in tool names its method body mentions as whole string literals.</summary>
    private static Dictionary<string, HashSet<string>> BridgeTargets()
    {
        var addin = AddinTools();
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(Root(), "RevitMCP.Bridge"), "RevitMcpTools*.cs"))
        {
            var source = File.ReadAllText(file);
            var starts = Regex.Matches(source, @"\[McpServerTool\(Name = ""([^""]+)""").Cast<Match>().ToList();
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].Index : source.Length;
                var block = source.Substring(starts[i].Index, end - starts[i].Index);
                var targets = Regex.Matches(block, @"""([a-z][a-z0-9_]+)""").Select(m => m.Groups[1].Value).Where(addin.Contains);
                result[starts[i].Groups[1].Value] = new HashSet<string>(targets, StringComparer.Ordinal);
            }
        }
        return result;
    }

    [Fact]
    public void EveryBridgeToolThatSendsToOtherAddinToolsIsInTheTable()
    {
        var bridge = BridgeTargets();
        Assert.True(bridge.Count > 100, $"parsed {bridge.Count} bridge tools");
        var problems = new List<string>();
        foreach (var pair in bridge)
        {
            var others = pair.Value.Where(t => t != pair.Key).ToList();
            var route = BridgeToolRoutes.Find(pair.Key);
            if (others.Count == 0)
            {
                if (route != null) problems.Add($"{pair.Key} sends only to itself: remove it from BridgeToolRoutes");
                continue;
            }
            if (route == null)
            {
                problems.Add($"{pair.Key} sends to {string.Join(", ", pair.Value)}: add it to BridgeToolRoutes");
                continue;
            }
            if (!pair.Value.SetEquals(route.Targets))
                problems.Add($"{pair.Key}: bridge sends to {string.Join(", ", pair.Value.OrderBy(t => t))}, table has {string.Join(", ", route.Targets.OrderBy(t => t))}");
        }
        foreach (var route in BridgeToolRoutes.All.Where(r => !bridge.ContainsKey(r.BridgeTool)))
            problems.Add($"{route.BridgeTool} is no bridge tool any more: remove it from BridgeToolRoutes");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void RouteArgumentsAndValuesMatchTheBridgeMethods()
    {
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Root(), "RevitMCP.Bridge"), "RevitMcpTools*.cs"))
        {
            var source = File.ReadAllText(file);
            foreach (var route in BridgeToolRoutes.All.Where(r => r.RouteArgument != null))
            {
                var start = source.IndexOf($"[McpServerTool(Name = \"{route.BridgeTool}\"", StringComparison.Ordinal);
                if (start < 0) continue;
                var next = source.IndexOf("[McpServerTool(Name = ", start + 1, StringComparison.Ordinal);
                var block = source.Substring(start, (next < 0 ? source.Length : next) - start);
                if (!Regex.IsMatch(block, $@"\bstring\??\s+{route.RouteArgument}\b"))
                    problems.Add($"{route.BridgeTool} has no string parameter '{route.RouteArgument}'");
                foreach (var value in route.ByValue.Keys)
                    if (block.IndexOf($"\"{value}\"", StringComparison.OrdinalIgnoreCase) < 0)
                        problems.Add($"{route.BridgeTool}: value '{value}' does not appear in the method");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
