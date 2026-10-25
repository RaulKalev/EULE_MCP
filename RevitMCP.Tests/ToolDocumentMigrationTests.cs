using System.Text.RegularExpressions;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// #101: every tool finds its document through ToolDocument, so <c>document="&lt;title&gt;"</c> works outside the
/// documents group. The tools that still read the active document directly do so on purpose and are listed here with the reason;
/// any other tool doing so fails the test, and the number of direct uses may only go down.
/// </summary>
public class ToolDocumentMigrationTests
{
    /// <summary>Uses of ActiveUIDocument left in RevitMCP.Addin/Tools: only the exceptions on purpose remain.</summary>
    private const int MaxRemainingUses = 28;

    /// <summary>Files (under RevitMCP.Addin/Tools) allowed to read the active document directly, by reason.</summary>
    private static readonly string[] AllowedActiveDocument =
    {
        // The user's selection is the point: these read or change what is selected in the active document.
        "GetSelectedElementsTool.cs", "InspectSelectedElementsTool.cs", "GetCircuitsForSelectedElementsTool.cs",
        "SelectedTagTemplateTools.cs", "SelectCircuitElementsTool.cs", "SelectClashElementsTool.cs",
        "SelectElementsByPanelTool.cs", "SelectElementsByQueryTool.cs", "SelectElementsTool.cs",
        "SelectLinkedElementsTool.cs", "SelectUncircuitedElementsTool.cs", "FocusClashTool.cs",
        // The model graph describes the active model only (its gate steps aside for other documents).
        Path.Combine("Graph", "GraphBuildTool.cs"), Path.Combine("Graph", "GraphQueryTool.cs"), Path.Combine("Graph", "GraphRouteTool.cs"),
        Path.Combine("Graph", "GraphRoutingSupport.cs"), Path.Combine("Graph", "GraphStatusTool.cs"), Path.Combine("Graph", "GraphSummaryTool.cs"),
        // Pages, memory and plans bind to the model they were made from, which is the active one by design.
        Path.Combine("ModelPages", "ModelPageTools.cs"), Path.Combine("ModelPages", "PageSetValuesTool.cs"),
        Path.Combine("Memory", "MemoryReviewTools.cs"), Path.Combine("Memory", "MemoryTools.cs"), Path.Combine("Plans", "PlanTools.cs"),
        // Documents group: the active document is the subject (activate, open-transaction check).
        Path.Combine("Documents", "DocumentTools.cs")
    };

    private static string ToolsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "RevitMCP.Addin", "Tools");
    }

    private static IEnumerable<(string Relative, string Source)> ToolSources()
    {
        var root = ToolsFolder();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            yield return (Path.GetRelativePath(root, file), File.ReadAllText(file));
    }

    [Fact]
    public void OnlyTheListedToolsReadTheActiveDocumentDirectly()
    {
        var allowed = new HashSet<string>(AllowedActiveDocument, StringComparer.OrdinalIgnoreCase);
        var offenders = ToolSources()
            .Where(t => t.Source.Contains("ActiveUIDocument") && !allowed.Contains(t.Relative))
            .Select(t => t.Relative)
            .ToList();
        Assert.True(offenders.Count == 0,
            "These tools read the active document directly; resolve it with ToolDocument.Resolve / UiDocumentOf: " + string.Join(", ", offenders));
    }

    [Fact]
    public void AllowListHasNoStaleEntries()
    {
        var stale = AllowedActiveDocument
            .Where(relative => !File.Exists(Path.Combine(ToolsFolder(), relative)) ||
                               !File.ReadAllText(Path.Combine(ToolsFolder(), relative)).Contains("ActiveUIDocument"))
            .ToList();
        Assert.True(stale.Count == 0, "Remove from AllowedActiveDocument (migrated or gone): " + string.Join(", ", stale));
    }

    /// <summary>
    /// Approvals bind to the <c>document</c> argument, so a bridge method may only offer it when every add-in tool it
    /// sends to resolves it; a tool that ignored it would act on the active document under an approval for another one.
    /// </summary>
    [Fact]
    public void BridgeOffersDocumentOnlyForToolsThatResolveIt()
    {
        // Resolve `document` themselves although their files also read the active document for other reasons.
        var resolveItThemselves = new HashSet<string>(StringComparer.Ordinal) { "revit_activate_document", "revit_plan_submit" };
        var resolving = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, source) in ToolSources())
        {
            // PanelSchematicsToolSupport.ActiveDocument (called as Support.ActiveDocument) resolves through ToolDocument.
            var ok = Regex.IsMatch(source, @"ToolDocument\.|WorksharingSupport\.Workshared\(|Support\.ActiveDocument\(|RevitDocumentResolver\.Resolve|DocumentToolSupport\.Resolve|CheckpointSupport\.Resolve|SaveSupport\.Resolve")
                     && !source.Contains("ActiveUIDocument");
            if (!ok) continue;
            foreach (Match m in Regex.Matches(source, @"(?:Name\s*=>\s*|ToolName\s*=\s*)""([a-z][a-z0-9_]+)"""))
                resolving.Add(m.Groups[1].Value);
        }

        var bridge = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ToolsFolder())!, "..", "RevitMCP.Bridge", "RevitMcpTools.cs"));
        var starts = Regex.Matches(bridge, @"\[McpServerTool\(Name = ""([^""]+)""").Cast<Match>().ToList();
        var offenders = new List<string>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : bridge.Length;
            var block = bridge.Substring(starts[i].Index, end - starts[i].Index);
            if (!Regex.IsMatch(block, @"\bstring\??\s+document\b")) continue;
            var targets = Regex.Matches(block, @"(?:SendAsync|Send\w+)\(\s*""([a-z][a-z0-9_]+)""").Select(m => m.Groups[1].Value).Distinct();
            offenders.AddRange(targets.Where(t => !resolving.Contains(t) && !resolveItThemselves.Contains(t))
                .Select(t => $"{starts[i].Groups[1].Value} -> {t}"));
        }
        Assert.True(offenders.Count == 0,
            "These bridge methods take document but send to a tool that does not resolve it (migrate the tool or drop the parameter): " + string.Join(", ", offenders));
    }

    [Fact]
    public void DirectActiveDocumentUsesOnlyGoDown()
    {
        var count = ToolSources().Sum(t => Regex.Matches(t.Source, "ActiveUIDocument").Count);
        Assert.True(count <= MaxRemainingUses,
            $"RevitMCP.Addin/Tools uses ActiveUIDocument {count} times; at most {MaxRemainingUses} are allowed. Resolve the document with ToolDocument instead.");
    }
}
