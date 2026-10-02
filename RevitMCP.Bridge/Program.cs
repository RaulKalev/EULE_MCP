using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RevitMCP.Bridge;

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

var mcpBuilder = builder.Services
    .AddMcpServer(options => options.ServerInstructions = ServerInstructions.Text)
    .WithStdioServerTransport();

var configuredProfile = builder.Configuration["RevitMCP:ToolProfile"] ?? "full";
var configuredToolNames = builder.Configuration["RevitMCP:ToolNames"];
var configuredToolGroups = builder.Configuration["RevitMCP:ToolGroups"];

if (McpToolCatalog.IsFullProfile(configuredProfile, configuredToolNames, configuredToolGroups))
{
    // Preserve the existing registration path and complete 193-tool surface by default.
    mcpBuilder.WithTools<RevitMcpTools>();
}
else
{
    builder.Services.AddTransient<RevitMcpTools>();
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
