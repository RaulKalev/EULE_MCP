using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>
/// Every connector tool, whether or not the active profile advertises it (#65). Built once from the
/// same [McpServerTool] methods the profiles use, so a discovered or dispatched tool behaves exactly
/// like a directly advertised one (argument binding, image results, approval in the add-in).
/// </summary>
internal sealed class ToolCatalogRegistry
{
    private readonly Dictionary<string, McpServerTool> _tools;

    public ToolCatalogRegistry()
    {
        _tools = McpToolCatalog.CreateAllTools().ToDictionary(t => t.ProtocolTool.Name, StringComparer.OrdinalIgnoreCase);
        Entries = _tools.Values
            .Select(t => new ToolEntry
            {
                Name = t.ProtocolTool.Name,
                Group = BridgeToolGroups.GroupOf(t.ProtocolTool.Name) ?? "other",
                Description = t.ProtocolTool.Description ?? string.Empty,
                ReadOnly = t.ProtocolTool.Annotations?.ReadOnlyHint == true
            })
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<ToolEntry> Entries { get; }

    public McpServerTool? Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;
}

/// <summary>
/// Tool discovery for small profiles (#65): search the catalog by intent, describe only the tools
/// you need, then call them — directly when advertised, through revit_tools_call otherwise, or after
/// revit_tools_load for clients that refresh their tool list on notifications/tools/list_changed.
/// </summary>
[McpServerToolType]
internal sealed class ToolDiscoveryTools(ToolCatalogRegistry registry)
{
    private static readonly HashSet<string> NotDispatchable = new(StringComparer.OrdinalIgnoreCase)
    {
        "revit_tools_call", "revit_tools_load", "revit_tools_search", "revit_tools_describe"
    };

    [McpServerTool(Name = "revit_tools_search", ReadOnly = true),
     Description("Search the connector's tools by intent (e.g. \"circuit info\", \"place devices in rooms\", \"ahela kaabel\"). Returns compact matches (name, group, read-only, one-line summary, whether advertised in this session). Without a query lists the tool groups. Then revit_tools_describe for the schema, and call the tool directly if advertised or with revit_tools_call.")]
    public string Search(
        McpServer server,
        [Description("What you want to do, in plain words (English or Estonian).")] string? query = null,
        [Description("Optional group to search in: core, graph, query, edit, devices, electrical, ifc, views, tags, coordination, cad, skills, office.")] string? group = null,
        [Description("Maximum results (default 10, max 50).")] int limit = 10)
    {
        var advertised = AdvertisedNames(server);
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(group))
        {
            var groups = registry.Entries
                .GroupBy(e => e.Group)
                .Select(g => new
                {
                    group = g.Key,
                    description = BridgeToolGroups.Descriptions.TryGetValue(g.Key, out var d) ? d : string.Empty,
                    tools = g.Count(),
                    advertised = g.Count(e => advertised.Contains(e.Name))
                })
                .OrderBy(g => g.group)
                .ToList();
            return JsonSerializer.Serialize(new { success = true, groups, next = "revit_tools_search query=\"<what you need>\" (optionally group=<name>)" });
        }

        var matches = ToolSearchIndex.Search(registry.Entries, query, group, limit);
        return JsonSerializer.Serialize(new
        {
            success = true,
            query,
            group,
            results = matches.Select(m => new
            {
                name = m.Name,
                group = m.Group,
                readOnly = m.ReadOnly,
                advertised = advertised.Contains(m.Name),
                summary = m.Summary()
            }),
            next = "revit_tools_describe names=[...] for argument schemas; call advertised tools directly, others with revit_tools_call name=... arguments={...}."
        });
    }

    [McpServerTool(Name = "revit_tools_describe", ReadOnly = true),
     Description("Full description and JSON input schema of the named connector tools (any tool, advertised or not). Use after revit_tools_search, before revit_tools_call.")]
    public string Describe(
        McpServer server,
        [Description("Tool names, e.g. [\"revit_get_circuit_info\"].")] string[] names)
    {
        var advertised = AdvertisedNames(server);
        var tools = new List<object>();
        var unknown = new List<string>();
        foreach (var name in names ?? [])
        {
            var tool = registry.Get(name);
            if (tool == null) { unknown.Add(name); continue; }
            var p = tool.ProtocolTool;
            tools.Add(new
            {
                name = p.Name,
                group = BridgeToolGroups.GroupOf(p.Name),
                readOnly = p.Annotations?.ReadOnlyHint == true,
                advertised = advertised.Contains(p.Name),
                description = p.Description,
                inputSchema = p.InputSchema
            });
        }
        return JsonSerializer.Serialize(new
        {
            success = unknown.Count == 0,
            tools,
            unknown = unknown.Count > 0 ? unknown : null,
            hint = unknown.Count > 0 ? "Unknown names — find tools with revit_tools_search." : null
        });
    }

    [McpServerTool(Name = "revit_tools_call"),
     Description("Run any connector tool by name with its arguments, also tools this session does not advertise (find them with revit_tools_search, see their schema with revit_tools_describe). Behaves exactly like calling the tool directly: write tools still need approval in Revit and act on the live model.")]
    public async ValueTask<CallToolResult> Call(
        McpServer server,
        [Description("Tool name, e.g. \"revit_get_circuit_info\".")] string name,
        [Description("The tool's arguments as an object, e.g. {\"circuitId\": 123456}.")] JsonElement? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (NotDispatchable.Contains(name))
            return Error($"'{name}' is a discovery tool — call it directly.");
        var tool = registry.Get(name);
        if (tool == null)
            return Error($"Unknown tool '{name}'. Find tools with revit_tools_search.");

        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (arguments is { ValueKind: JsonValueKind.Object } obj)
            foreach (var p in obj.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        else if (arguments is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
            return Error("arguments must be a JSON object, e.g. {\"elementIds\": [123]}.");

        var callParams = new CallToolRequestParams { Name = tool.ProtocolTool.Name, Arguments = dict };
        var rpc = new JsonRpcRequest
        {
            Id = new RequestId(Guid.NewGuid().ToString("N")),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(callParams, McpJsonUtilities.DefaultOptions)
        };
        var context = new RequestContext<CallToolRequestParams>(server, rpc, callParams)
        {
            Services = server.Services
        };
        try
        {
            return await tool.InvokeAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error($"{name} failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "revit_tools_load"),
     Description("Add tools (by name and/or whole groups) to this session's advertised tool list. Clients that support notifications/tools/list_changed (e.g. Claude Code) then show them as normal tools; for other clients use revit_tools_call. Loads nothing into Revit and changes no model data.")]
    public string Load(
        McpServer server,
        [Description("Tool names to add.")] string[]? names = null,
        [Description("Whole groups to add, e.g. [\"electrical\"].")] string[]? groups = null)
    {
        var collection = server.ServerOptions.ToolCollection;
        if (collection == null)
            return JsonSerializer.Serialize(new { success = false, message = "This session has no dynamic tool list; use revit_tools_call." });

        var wanted = new HashSet<string>(names ?? [], StringComparer.OrdinalIgnoreCase);
        var unknownGroups = new List<string>();
        foreach (var g in groups ?? [])
        {
            if (!BridgeToolGroups.IsKnownGroup(g)) { unknownGroups.Add(g); continue; }
            foreach (var e in registry.Entries.Where(e => string.Equals(e.Group, g, StringComparison.OrdinalIgnoreCase)))
                wanted.Add(e.Name);
        }

        var added = new List<string>();
        var already = new List<string>();
        var unknown = new List<string>();
        foreach (var name in wanted.OrderBy(n => n, StringComparer.Ordinal))
        {
            var tool = registry.Get(name);
            if (tool == null) { unknown.Add(name); continue; }
            if (collection.TryAdd(tool)) added.Add(tool.ProtocolTool.Name);
            else already.Add(tool.ProtocolTool.Name);
        }

        return JsonSerializer.Serialize(new
        {
            success = unknown.Count == 0 && unknownGroups.Count == 0,
            added,
            alreadyAdvertised = already,
            unknown = unknown.Count > 0 ? unknown : null,
            unknownGroups = unknownGroups.Count > 0 ? unknownGroups : null,
            note = added.Count > 0
                ? "The server sent notifications/tools/list_changed. If the new tools do not appear in your tool list, call them with revit_tools_call."
                : null
        });
    }

    private static HashSet<string> AdvertisedNames(McpServer server) =>
        new(server.ServerOptions.ToolCollection?.PrimitiveNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { success = false, message }) }]
    };
}
