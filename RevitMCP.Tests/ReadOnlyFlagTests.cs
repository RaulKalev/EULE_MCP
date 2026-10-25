using System.Text.RegularExpressions;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// H1: a tool advertised as read-only (the bridge's <c>ReadOnly = true</c>, which <c>revit_tools_search</c> reports as
/// <c>readOnly</c> and the read-only profile selects, and the add-in's <c>ToolPermission.ReadOnly</c>) never changes the
/// model, files, configuration, stored records or connector state: a client may run it without asking. The rule:
/// <list type="bullet">
/// <item>Read-only: reads, previews (temporary changes rolled back) and additive connector output that changes nothing
/// a later call depends on (see <see cref="AdditiveOutput"/>).</item>
/// <item>Changes connector state, files, configuration or only the UI (selection, active view) without touching the
/// model: <c>ToolPermission.DirectEdit</c> (runs without a prompt) and not read-only on the bridge.</item>
/// <item>Changes the model: <c>RequiresApproval</c> or <c>DestructiveRequiresManualApproval</c>.</item>
/// </list>
/// Neither the bridge nor the add-in is referenced by the test project, so both are read from source, as in
/// <see cref="ToolDocumentMigrationTests"/>.
/// </summary>
public class ReadOnlyFlagTests
{
    /// <summary>Tools that change something and therefore must never be read-only on either side.</summary>
    private static readonly string[] StateChanging =
    {
        // Jobs and plans: cancelling stops a running job, closing or switching ends or changes a plan.
        "revit_job_cancel", "revit_plan_close", "revit_plan_use",
        // Skill runs can execute a task that changes the model (naming.apply-mapping).
        "revit_run_skill", "revit_run_skill_task",
        // Skills: create, update or reset project overrides, write a proposal file.
        "revit_manage_project_skill_override", "revit_configure_sheet_naming_skill", "revit_propose_master_skill_update",
        "revit_create_project_skill_override", "revit_update_project_skill_override", "revit_reset_project_skill_override",
        // Standards: the index files; usage: report files and the browser.
        "standards_index_sources", "revit_usage_report",
        // Record removal split off the list tools.
        "revit_checkpoint_remove", "revit_job_remove", "revit_remove_page",
        // Selection changes the user's UI; focus_clash also sets up a review view (a model change).
        "revit_select_elements", "revit_select_elements_by_query", "revit_select_linked_elements",
        "revit_select_circuit_elements", "revit_select_elements_by_panel", "revit_select_uncircuited_elements",
        "revit_select_clash_elements", "revit_plan_show_changes", "revit_focus_clash", "revit_select_instance",
        // Config reads never create a file; the writers do.
        "config_write", "config_update", "config_set_project_config"
    };

    /// <summary>
    /// Read-only tools that write new output into the connector's own folders without changing anything another call
    /// depends on: exports to a new file, the regenerable graph cache, published pages and checkpoint records.
    /// They stay read-only; anything that overwrites configuration, deletes records or changes the model may not be here.
    /// </summary>
    private static readonly HashSet<string> AdditiveOutput = new(StringComparer.Ordinal)
    {
        "revit_graph_build", "revit_checkpoint_create", "revit_publish_page", "revit_job_report", "revit_memory_page",
        "revit_export_view_image"
    };

    /// <summary>Bridge methods that are not read-only although every add-in tool they send to is: the bridge itself changes state.</summary>
    private static readonly HashSet<string> BridgeSideWrites = new(StringComparer.Ordinal)
    {
        "revit_select_instance", "revit_tools_call", "revit_tools_load"
    };

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private sealed class AddinTool
    {
        public string Name = string.Empty;
        public string Permission = string.Empty;
        public string Source = string.Empty;
        public string File = string.Empty;
    }

    private sealed class BridgeTool
    {
        public string Name = string.Empty;
        public bool ReadOnly;
        public string Source = string.Empty;
        public List<string> Targets = new();
    }

    private static readonly Lazy<Dictionary<string, AddinTool>> Addin = new(() =>
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "RevitMCP.Addin"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();
        var classes = new List<(string Class, string Body, string File)>();
        foreach (var file in files)
        {
            var text = System.IO.File.ReadAllText(file);
            var starts = Regex.Matches(text, @"\bclass\s+(\w+)").Cast<Match>().ToList();
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
                classes.Add((starts[i].Groups[1].Value, text.Substring(starts[i].Index, end - starts[i].Index), file));
            }
        }

        // ClassName.Const -> value, for Name => SomeService.ToolConst.
        var consts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (cls, body, _) in classes)
            foreach (Match m in Regex.Matches(body, @"const\s+string\s+(\w+)\s*=\s*""([^""]+)"""))
                consts[cls + "." + m.Groups[1].Value] = m.Groups[2].Value;

        var tools = new Dictionary<string, AddinTool>(StringComparer.Ordinal);
        foreach (var (cls, body, file) in classes)
        {
            var permission = Regex.Match(body, @"ToolPermission\s+Permission\s*=>\s*ToolPermission\.(\w+)");
            if (!permission.Success) continue;
            string? name = null;
            var literal = Regex.Match(body, @"\bstring\s+Name\s*=>\s*""([^""]+)""");
            if (literal.Success) name = literal.Groups[1].Value;
            else
            {
                var reference = Regex.Match(body, @"\bstring\s+Name\s*=>\s*(?:(\w+)\.)?(\w+)\s*;");
                if (reference.Success)
                {
                    var owner = reference.Groups[1].Success ? reference.Groups[1].Value : cls;
                    consts.TryGetValue(owner + "." + reference.Groups[2].Value, out name);
                }
            }
            Assert.True(name != null, $"Could not read the tool name of {cls} in {file}.");
            tools[name!] = new AddinTool { Name = name!, Permission = permission.Groups[1].Value, Source = body, File = file };
        }
        return tools;
    });

    private static readonly Lazy<List<BridgeTool>> Bridge = new(() =>
    {
        var folder = Path.Combine(Root(), "RevitMCP.Bridge");
        var source = string.Concat(Directory.GetFiles(folder, "RevitMcpTools*.cs").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText)) +
                     File.ReadAllText(Path.Combine(folder, "ToolDiscoveryTools.cs"));
        var starts = Regex.Matches(source, @"\[McpServerTool\(Name = ""([^""]+)""([^)]*)\)").Cast<Match>().ToList();
        var tools = new List<BridgeTool>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : source.Length;
            var block = source.Substring(starts[i].Index, end - starts[i].Index);
            tools.Add(new BridgeTool
            {
                Name = starts[i].Groups[1].Value,
                ReadOnly = Regex.IsMatch(starts[i].Groups[2].Value, @"ReadOnly\s*=\s*true"),
                Source = block,
                // Every string literal naming an add-in tool is a tool the method may send to.
                Targets = Regex.Matches(block, @"""([a-z][a-z0-9_]+)""").Select(m => m.Groups[1].Value)
                    .Where(t => Addin.Value.ContainsKey(t)).Distinct().ToList()
            });
        }
        return tools;
    });

    [Fact]
    public void Parsing_FindsTheWholeSurface()
    {
        Assert.True(Addin.Value.Count > 250, $"only {Addin.Value.Count} add-in tools were read");
        Assert.True(Bridge.Value.Count > 250, $"only {Bridge.Value.Count} bridge tools were read");
        Assert.Equal("ReadOnly", Addin.Value["revit_get_elements_info"].Permission);
        Assert.Contains(Bridge.Value, t => t.Name == "revit_get_elements_info" && t.ReadOnly);
    }

    [Fact]
    public void StateChangingTools_AreNeverReadOnly()
    {
        var offenders = new List<string>();
        foreach (var name in StateChanging)
        {
            var addin = Addin.Value.TryGetValue(name, out var a) ? a : null;
            var bridge = Bridge.Value.FirstOrDefault(t => t.Name == name);
            if (addin == null && bridge == null) offenders.Add($"{name} (neither an add-in nor a bridge tool: remove it from StateChanging)");
            if (addin?.Permission == "ReadOnly") offenders.Add($"{name} (add-in ToolPermission.ReadOnly)");
            if (bridge?.ReadOnly == true) offenders.Add($"{name} (bridge ReadOnly = true)");
        }
        Assert.True(offenders.Count == 0, "These tools change something but are marked read-only: " + string.Join(", ", offenders));
    }

    [Fact]
    public void BridgeReadOnly_AgreesWithTheAddinPermission()
    {
        var offenders = new List<string>();
        foreach (var tool in Bridge.Value.Where(t => t.Targets.Count > 0))
        {
            var notReadOnly = tool.Targets.Where(t => Addin.Value[t].Permission != "ReadOnly").ToList();
            if (tool.ReadOnly && notReadOnly.Count > 0)
                offenders.Add($"{tool.Name} is ReadOnly on the bridge but sends to {string.Join("/", notReadOnly.Select(t => t + " (" + Addin.Value[t].Permission + ")"))}");
            if (!tool.ReadOnly && notReadOnly.Count == 0 && !BridgeSideWrites.Contains(tool.Name))
                offenders.Add($"{tool.Name} is not ReadOnly on the bridge but every add-in tool it sends to is ({string.Join("/", tool.Targets)})");
        }
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void ReadOnlyListTools_DoNotDelete()
    {
        var bridge = Bridge.Value.Where(t => t.ReadOnly && Regex.IsMatch(t.Source, @"\bstring\??\s+(remove|delete|clear)\w*\s*="))
            .Select(t => t.Name);
        var addin = Addin.Value.Values.Where(t => t.Permission == "ReadOnly" && Regex.IsMatch(t.Source, @"Arguments,\s*""(remove|delete|clear)\w*"""))
            .Select(t => t.Name);
        var offenders = bridge.Concat(addin).Distinct().ToList();
        Assert.True(offenders.Count == 0, "Read-only tools that take a remove/delete argument: " + string.Join(", ", offenders));
    }

    [Fact]
    public void SelectionTools_FollowOneRule()
    {
        // Selection changes only the UI: it runs without a prompt (DirectEdit) and is not read-only. A tool that sets the
        // selection directly is a selection tool; revit_focus_clash goes through ClashFocusService, which also commits a
        // review view, and stays approval-gated.
        var offenders = new List<string>();
        var selecting = Addin.Value.Values
            .Where(t => t.Name.StartsWith("revit_select_", StringComparison.Ordinal) ||
                        Regex.IsMatch(t.Source, @"Selection\.SetElementIds\(|TrySetReferences\("))
            .ToList();
        Assert.True(selecting.Count >= 8, $"only {selecting.Count} selection tools were found");
        foreach (var tool in selecting)
        {
            if (tool.Permission != "DirectEdit") offenders.Add($"{tool.Name} is {tool.Permission} in the add-in");
            if (Regex.IsMatch(tool.Source, @"Requires (user )?approval", RegexOptions.IgnoreCase)) offenders.Add($"{tool.Name}'s description says it requires approval");
        }
        foreach (var tool in Bridge.Value.Where(t => t.Name.StartsWith("revit_select_", StringComparison.Ordinal)))
        {
            if (tool.ReadOnly) offenders.Add($"{tool.Name} is ReadOnly on the bridge");
            if (Regex.IsMatch(tool.Source.Split(new[] { "public async" }, StringSplitOptions.None)[0], @"Requires (user )?approval", RegexOptions.IgnoreCase))
                offenders.Add($"{tool.Name}'s bridge description says it requires approval");
        }
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void ConfigReads_CannotCreateFiles()
    {
        foreach (var name in new[] { "config_read", "config_get_project_config" })
        {
            Assert.DoesNotMatch(@"bool\s+createIfMissing|\[""createIfMissing""\]", Bridge.Value.Single(t => t.Name == name).Source);
            Assert.DoesNotContain("\"createIfMissing\"", Addin.Value[name].Source);
        }
    }

    [Fact]
    public void ReadOnlyTools_RollBackWhatTheyChange()
    {
        // A read-only tool may change the model only temporarily: a Transaction it rolls back (no DocumentChanged), or a
        // TransactionGroup it rolls back inside a TemporaryModelChanges scope so the trackers ignore the inner commit.
        var offenders = new List<string>();
        foreach (var tool in Addin.Value.Values.Where(t => t.Permission == "ReadOnly"))
        {
            if (tool.Source.Contains("TransactionGroup") && !tool.Source.Contains("TemporaryModelChanges.Begin()"))
                offenders.Add($"{tool.Name} uses a TransactionGroup outside a TemporaryModelChanges scope");
            if (Regex.IsMatch(tool.Source, @"CommitOrThrow\(") ||
                (Regex.IsMatch(tool.Source, @"\.Commit\(\)") && !tool.Source.Contains("TransactionGroup")))
                offenders.Add($"{tool.Name} commits a transaction");
        }
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void ValidateSourceConfig_DoesNotCreateTheConfig()
    {
        var source = Addin.Value["standards_validate_source_config"].Source;
        Assert.Equal("ReadOnly", Addin.Value["standards_validate_source_config"].Permission);
        Assert.DoesNotMatch(@"File\.Write|SaveExample|Directory\.Create", source);

        var example = Newtonsoft.Json.JsonConvert.DeserializeObject<RevitMCP.Addin.Standards.StandardsSourceConfig>(
            RevitMCP.Addin.Standards.StandardsSourceService.ExampleJson());
        Assert.NotNull(example);
        Assert.Single(example!.Sources);
        Assert.Equal("company.standards", example.Sources[0].Id);
    }

    [Fact]
    public void TemporaryModelChanges_IsActiveOnlyInsideItsScopes()
    {
        Assert.False(RevitMCP.Addin.Checkpoints.TemporaryModelChanges.Active);
        var outer = RevitMCP.Addin.Checkpoints.TemporaryModelChanges.Begin();
        using (RevitMCP.Addin.Checkpoints.TemporaryModelChanges.Begin())
            Assert.True(RevitMCP.Addin.Checkpoints.TemporaryModelChanges.Active);
        Assert.True(RevitMCP.Addin.Checkpoints.TemporaryModelChanges.Active);
        outer.Dispose();
        outer.Dispose(); // a second dispose does not close someone else's scope
        Assert.False(RevitMCP.Addin.Checkpoints.TemporaryModelChanges.Active);
    }

    [Fact]
    public void RemovalStaysReachable()
    {
        // The list tools no longer delete; the removal moved to these tools, which take the id the list returns.
        foreach (var (tool, argument) in new[] { ("revit_checkpoint_remove", "checkpointId"), ("revit_job_remove", "jobId"), ("revit_remove_page", "pageId") })
        {
            var bridge = Bridge.Value.Single(t => t.Name == tool);
            Assert.False(bridge.ReadOnly);
            Assert.Contains(tool, bridge.Targets);
            Assert.Equal("DirectEdit", Addin.Value[tool].Permission);
            Assert.Contains($"\"{argument}\"", Addin.Value[tool].Source);
        }
    }

    [Fact]
    public void AdditiveOutputList_HasNoStaleEntries()
    {
        foreach (var name in AdditiveOutput)
            Assert.True(Addin.Value.TryGetValue(name, out var tool) && tool.Permission == "ReadOnly", $"{name} is no longer a read-only add-in tool: remove it from AdditiveOutput.");
        foreach (var name in BridgeSideWrites)
            Assert.Contains(Bridge.Value, t => t.Name == name && !t.ReadOnly);
    }
}
