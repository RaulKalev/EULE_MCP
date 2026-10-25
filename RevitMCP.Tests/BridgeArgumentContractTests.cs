using System.Text.RegularExpressions;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Batch A of the 2026-10-25 live test: arguments the bridge sends under a name the add-in tool never reads, so the
/// caller's value is silently dropped (M4: <c>revit_trace_circuit</c> sent <c>includeConnectedElements</c>, the tool
/// reads <c>includeElements</c>). An argument counts as read when its name appears as a string literal in the target
/// tool's class, or in a class that tool refers to by name (shared helpers such as <c>TaggingToolSupport</c>).
/// </summary>
public class BridgeArgumentContractTests
{
    /// <summary>
    /// Arguments the request pipeline reads before the tool runs, not the tool's own class: the target document, plan
    /// and approval routing, and the graph-first gate (<c>GraphRouting.GateArg</c>, <c>GraphRoutingSupport.OptOutArg</c>).
    /// </summary>
    private static readonly HashSet<string> PipelineArguments = new(StringComparer.Ordinal)
    {
        "document", "planId", "requestId", "expectedStateToken", "graphFirst", "graphHint"
    };

    /// <summary>
    /// Each add-in class with the rest of its file: the scan splits files at every <c>class</c> keyword, so a nested
    /// class would otherwise cut its outer class's argument reads off.
    /// </summary>
    private static readonly Lazy<Dictionary<string, string>> ClassesByName = new(() =>
    {
        var classes = ToolSourceScan.AddinClasses.Value;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < classes.Count; i++)
        {
            var rest = string.Concat(classes.Skip(i).TakeWhile(c => c.File == classes[i].File).Select(c => c.Body));
            map[classes[i].Class] = map.TryGetValue(classes[i].Class, out var earlier) ? earlier + rest : rest;
        }
        return map;
    });

    /// <summary>
    /// Names a tool can read: string literals and public property names (arguments bound to an options class) of the
    /// tool class and of the add-in classes it names, two hops deep. Case-insensitive, as JSON binding is.
    /// </summary>
    private static HashSet<string> NamesReadBy(ToolSourceScan.AddinTool tool)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal) { tool.Class };
        var frontier = new List<string> { ClassesByName.Value[tool.Class] };
        for (var hop = 0; hop <= 2 && frontier.Count > 0; hop++)
        {
            var next = new List<string>();
            foreach (var body in frontier)
            {
                names.UnionWith(Regex.Matches(body, @"""([A-Za-z_][A-Za-z0-9_]*)""").Select(m => m.Groups[1].Value));
                names.UnionWith(Regex.Matches(body, @"\bpublic\s+[\w<>\[\]?,. ]+\s+(\w+)\s*\{\s*get").Select(m => m.Groups[1].Value));
                foreach (Match m in Regex.Matches(body, @"\b([A-Z]\w+)\b"))
                    if (seen.Add(m.Groups[1].Value) && ClassesByName.Value.TryGetValue(m.Groups[1].Value, out var other))
                        next.Add(other);
            }
            frontier = next;
        }
        return names;
    }

    /// <summary>The bridge's private and internal static helpers (<c>RoomArgs</c>, <c>BuildSheetDuplicateArgs</c>) by name.</summary>
    private static readonly Lazy<Dictionary<string, string>> BridgeHelpers = new(() =>
    {
        var folder = Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge");
        var helpers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(folder, "RevitMcpTools*.cs"))
        {
            var text = File.ReadAllText(file);
            var starts = Regex.Matches(text, @"\n    (?:private|internal) static [^\n(=]+?\s(\w+)\(").Cast<Match>().ToList();
            foreach (var start in starts)
            {
                var end = Regex.Match(text.Substring(start.Index + 1), @"\n    (?:\[|private |internal |public )");
                var body = end.Success ? text.Substring(start.Index, end.Index + 1) : text.Substring(start.Index);
                helpers[start.Groups[1].Value] = helpers.TryGetValue(start.Groups[1].Value, out var other) ? other + body : body;
            }
        }
        return helpers;
    });

    /// <summary>A bridge method's source up to its first send, plus the helpers it calls there.</summary>
    private static string ArgumentSource(ToolSourceScan.BridgeTool tool)
    {
        var send = tool.Source.IndexOf("SendAsync(", StringComparison.Ordinal);
        var body = send < 0 ? tool.Source : tool.Source.Substring(0, send);
        var helpers = Regex.Matches(body, @"\b(\w+)\(").Select(m => m.Groups[1].Value).Distinct()
            .Where(BridgeHelpers.Value.ContainsKey).Select(n => BridgeHelpers.Value[n]);
        return body + string.Concat(helpers);
    }

    /// <summary>
    /// Keys the bridge method puts into the argument dictionary before it sends it, its helpers included. The scan
    /// stops at the first send, so keys of the result it builds afterwards are not counted.
    /// </summary>
    private static IEnumerable<string> SentKeys(ToolSourceScan.BridgeTool tool)
        => Regex.Matches(ArgumentSource(tool), @"\[""([A-Za-z_][A-Za-z0-9_]*)""\]\s*=").Select(m => m.Groups[1].Value).Distinct();

    [Fact]
    public void EverySentArgumentIsReadByItsTarget()
    {
        var offenders = new List<string>();
        foreach (var bridge in ToolSourceScan.Bridge.Value)
        {
            if (bridge.Targets.Count == 0) continue;
            var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in bridge.Targets) read.UnionWith(NamesReadBy(ToolSourceScan.Addin.Value[target]));
            foreach (var key in SentKeys(bridge))
                if (!PipelineArguments.Contains(key) && !read.Contains(key))
                    offenders.Add($"{bridge.Name} sends '{key}', which {string.Join("/", bridge.Targets)} never reads");
        }
        Assert.True(offenders.Count == 0, $"{offenders.Count} dropped argument(s):\n" + string.Join("\n", offenders));
    }

    /// <summary>Infrastructure parameters of a bridge method, never forwarded as tool arguments.</summary>
    private static readonly HashSet<string> BridgeInfrastructure = new(StringComparer.Ordinal)
    {
        "pipeClient", "cancellationToken", "server", "session", "catalog", "progress", "services", "context"
    };

    /// <summary>Parameter names of a C# parameter list: attributes and string literals removed, split outside generics.</summary>
    private static IEnumerable<string> ParameterNames(string parameters)
    {
        var text = Regex.Replace(parameters, @"""(?:[^""\\]|\\.)*""", "\"\"");
        text = Regex.Replace(text, @"\[[^\[\]]*\]", " ");
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && (text[i] == '<' || text[i] == '(')) depth++;
            else if (i < text.Length && (text[i] == '>' || text[i] == ')')) depth--;
            else if (i == text.Length || (text[i] == ',' && depth == 0))
            {
                var part = text.Substring(start, i - start);
                var eq = part.IndexOf('=');
                var declaration = (eq < 0 ? part : part.Substring(0, eq)).Trim();
                var name = Regex.Match(declaration, @"(\w+)$");
                if (name.Success && declaration.Contains(' ')) yield return name.Groups[1].Value;
                start = i + 1;
            }
        }
    }

    [Fact]
    public void EveryBridgeParameterIsUsed()
    {
        // M2: a parameter the caller can set but the method never forwards is silently ignored.
        var offenders = new List<string>();
        var checkedCount = 0;
        foreach (var bridge in ToolSourceScan.Bridge.Value)
        {
            var signature = Regex.Match(bridge.Source, @"\bTask<\w+>\s+\w+\s*\((.*?)\)\s*\{", RegexOptions.Singleline);
            if (!signature.Success) continue;
            var body = bridge.Source.Substring(signature.Index + signature.Length);
            foreach (var name in ParameterNames(signature.Groups[1].Value))
            {
                if (BridgeInfrastructure.Contains(name)) continue;
                checkedCount++;
                if (!Regex.IsMatch(body, $@"\b{Regex.Escape(name)}\b"))
                    offenders.Add($"{bridge.Name} takes '{name}' and never uses it");
            }
        }
        Assert.True(checkedCount > 1000, $"only {checkedCount} bridge parameters were read");
        Assert.True(offenders.Count == 0, $"{offenders.Count} unused parameter(s):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void MessagesNameToolsAnAgentCanCall()
    {
        // X6: "use revit_delete_sheets" sent agents to a tool the bridge does not have; the bridge has revit_delete
        // target=sheets. A message or description may name a bridge tool, or an add-in tool no bridge method sends to
        // (those only refer to each other).
        var bridgeNames = ToolSourceScan.Bridge.Value.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var routed = ToolSourceScan.Bridge.Value.SelectMany(b => b.Targets).Where(t => !bridgeNames.Contains(t)).ToHashSet(StringComparer.Ordinal);
        var files = Directory.GetFiles(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Addin"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(ToolSourceScan.Root(), "RevitMCP.Bridge"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match literal in Regex.Matches(text, @"""((?:[^""\\\r\n]|\\.)*)"""))
            {
                if (!literal.Groups[1].Value.Contains(' ')) continue;   // a sentence, not a tool-name key
                foreach (Match name in Regex.Matches(literal.Groups[1].Value, @"\brevit_[a-z0-9_]+\b"))
                    if (routed.Contains(name.Value))
                        offenders.Add($"{Path.GetFileName(file)}: '{name.Value}' (the bridge sends to it from another tool name)");
            }
        }
        Assert.True(offenders.Count == 0, $"{offenders.Count} message(s) name tools an agent cannot call:\n" + string.Join("\n", offenders.Distinct()));
    }

    [Fact]
    public void DescriptionsPromiseOnlyTheirOwnSelectors()
    {
        // X6: descriptions said "Also accepts: useSelection, elementIds" on methods without those parameters.
        var offenders = new List<string>();
        foreach (var bridge in ToolSourceScan.Bridge.Value)
        {
            var signature = Regex.Match(bridge.Source, @"\bTask<\w+>\s+\w+\s*\((.*?)\)\s*\{", RegexOptions.Singleline);
            if (!signature.Success) continue;
            var description = Regex.Match(bridge.Source, @"^\s*\[McpServerTool[^\]]*?Description\(((?:""(?:[^""\\]|\\.)*""\s*\+?\s*)+)\)", RegexOptions.Singleline);
            if (!description.Success) continue;
            var parameters = ParameterNames(signature.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var selector in new[] { "useSelection", "elementIds" })
                if (Regex.IsMatch(description.Groups[1].Value, $@"\b{selector}\b") && !parameters.Contains(selector))
                    offenders.Add($"{bridge.Name} describes '{selector}' but has no such parameter");
        }
        Assert.True(offenders.Count == 0, $"{offenders.Count} description(s) promise missing arguments:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Arguments an add-in tool reads that its bridge method does not offer on purpose. A job step calls the add-in tool
    /// directly, so these stay reachable there. Key: <c>tool:argument</c>, or <c>*:argument</c> for every tool.
    /// </summary>
    private static readonly Dictionary<string, string> AddinOnlyArguments = new(StringComparer.Ordinal)
    {
        ["*:useSelection"] = "offered on the bridge only where the description says so (DescriptionsPromiseOnlyTheirOwnSelectors)",
        ["revit_get_approval_status:approvalId"] = "alias of requestId",
        ["revit_duplicate_views:fallbackToDuplicate"] = "the add-in default (true) is the only useful value",
        ["revit_preview_duplicate_views:fallbackToDuplicate"] = "the add-in default (true) is the only useful value",
        ["revit_detect_clearance_clashes:distanceMode"] = "its only alternative is a bounding-box-centre approximation",
        ["revit_get_element_parameters:includeInstanceParameters"] = "M35: batch G",
        ["revit_get_element_parameters:includeTypeParameters"] = "M35: batch G",
        ["revit_get_element_parameters:parameterNames"] = "M35: batch G",
        ["ifc_preview_space_geometry:minimumAreaM2"] = "M15: batch F (IFC spaces)"
    };

    [Fact]
    public void EveryArgumentAToolReadsCanBeSent()
    {
        // M1: the add-in reads an argument that no bridge method sending to the tool can pass.
        var senders = ToolSourceScan.Bridge.Value.SelectMany(b => b.Targets.Select(t => (Target: t, Bridge: b)))
            .GroupBy(x => x.Target, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Bridge).ToList(), StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var (name, tool) in ToolSourceScan.Addin.Value)
        {
            if (!senders.TryGetValue(name, out var bridges)) continue;
            var sent = bridges.SelectMany(b => Regex.Matches(b.Source + ArgumentSource(b), @"""([A-Za-z_][A-Za-z0-9_]*)""").Select(m => m.Groups[1].Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (bridges.Any(b => Regex.IsMatch(b.Source, @"\bSendAsync\(\s*""[^""]+""\s*,\s*(arguments|rawArguments|extra)\b"))) continue;
            foreach (Match m in Regex.Matches(tool.Source, @"ToolArguments\.\w+\(\s*(?:request\.Arguments|args|arguments)\s*,\s*""([A-Za-z_][A-Za-z0-9_]*)"""))
            {
                var key = m.Groups[1].Value;
                if (!PipelineArguments.Contains(key) && !sent.Contains(key) &&
                    !AddinOnlyArguments.ContainsKey($"{name}:{key}") && !AddinOnlyArguments.ContainsKey($"*:{key}"))
                    offenders.Add($"{name} reads '{key}', which {string.Join("/", bridges.Select(b => b.Name).Distinct())} never sends");
            }
        }
        Assert.True(offenders.Count == 0, $"{offenders.Count} unreachable argument(s):\n" + string.Join("\n", offenders.Distinct()));
    }
}
