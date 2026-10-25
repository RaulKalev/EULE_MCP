using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RevitMCP.Bridge;

// X1: the bridge only talks to agents; its numbers read the same on every machine ("45.21", not "45,21").
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var clientName = GetArg(args, "--client");
var pipeName = GetArg(args, "--pipe");
var toolProfile = GetArg(args, "--tool-profile");
var toolNames = GetArg(args, "--tool-names");
var toolGroups = GetArg(args, "--tool-groups");

var builder = Host.CreateApplicationBuilder(args);

// Suppress all console logging — stdout is reserved for MCP JSON-RPC protocol.
// Any text written to stdout corrupts the stdio transport and causes JSON parse errors.
builder.Logging.ClearProviders();

// CLI arguments take precedence over appsettings.json
if (clientName != null)
    builder.Configuration["RevitMCP:ClientName"] = clientName;
if (pipeName != null)
    builder.Configuration["RevitMCP:PipeName"] = pipeName;
if (toolProfile != null)
    builder.Configuration["RevitMCP:ToolProfile"] = toolProfile;
if (toolNames != null)
    builder.Configuration["RevitMCP:ToolNames"] = toolNames;
if (toolGroups != null)
    builder.Configuration["RevitMCP:ToolGroups"] = toolGroups;

builder.Services.AddSingleton<RevitPipeClient>();
builder.Services.AddSingleton<ToolCatalogRegistry>();
builder.Services.AddTransient<RevitMcpTools>();
builder.Services.AddTransient<ToolDiscoveryTools>();

var mcpBuilder = builder.Services
    .AddMcpServer(options => options.ServerInstructions = ServerInstructions.Text)
    .WithStdioServerTransport()
    .WithPrompts<MemoryPrompts>();

// Default: the compact graph-first core profile (#66) with tool discovery and dispatch (#65).
// "full" advertises every tool for clients/automations that call tools by name.
var configuredProfile = builder.Configuration["RevitMCP:ToolProfile"] ?? "core";
var configuredToolNames = builder.Configuration["RevitMCP:ToolNames"];
var configuredToolGroups = builder.Configuration["RevitMCP:ToolGroups"];

if (McpToolCatalog.IsFullProfile(configuredProfile, configuredToolNames, configuredToolGroups))
{
    // Every tool. Built by the catalog like the other profiles, so numeric ids sent for string parameters are
    // accepted and a failure's real message reaches the caller (LenientArgumentsTool).
    mcpBuilder.WithTools(McpToolCatalog.CreateAllTools());
}
else
{
    mcpBuilder.WithTools(McpToolCatalog.CreateSelectedTools(
        configuredProfile,
        configuredToolNames,
        configuredToolGroups));
}

await builder.Build().RunAsync();

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}
