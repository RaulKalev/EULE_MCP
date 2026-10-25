using System.Text.RegularExpressions;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// The add-in's tools and the bridge's MCP methods, read from source. Neither project is referenced by the test project,
/// so tests that check the two sides against each other (<see cref="ReadOnlyFlagTests"/>,
/// <see cref="BridgeArgumentContractTests"/>) parse them here.
/// </summary>
internal static class ToolSourceScan
{
    internal sealed class AddinTool
    {
        public string Name = string.Empty;
        public string Class = string.Empty;
        public string Permission = string.Empty;
        public string Source = string.Empty;
        public string File = string.Empty;
    }

    internal sealed class BridgeTool
    {
        public string Name = string.Empty;
        public bool ReadOnly;
        public string Source = string.Empty;
        public List<string> Targets = new();
    }

    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitMCP.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>Every class of the add-in: name, source up to the next class in its file, and the file.</summary>
    internal static readonly Lazy<List<(string Class, string Body, string File)>> AddinClasses = new(() =>
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "RevitMCP.Addin"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();
        var classes = new List<(string Class, string Body, string File)>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var starts = Regex.Matches(text, @"\bclass\s+(\w+)").Cast<Match>().ToList();
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
                classes.Add((starts[i].Groups[1].Value, text.Substring(starts[i].Index, end - starts[i].Index), file));
            }
        }
        return classes;
    });

    internal static readonly Lazy<Dictionary<string, AddinTool>> Addin = new(() =>
    {
        var classes = AddinClasses.Value;

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
            tools[name!] = new AddinTool { Name = name!, Class = cls, Permission = permission.Groups[1].Value, Source = body, File = file };
        }
        return tools;
    });

    internal static readonly Lazy<List<BridgeTool>> Bridge = new(() =>
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
}
