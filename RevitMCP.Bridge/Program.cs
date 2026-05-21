using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RevitMCP.Bridge;

var clientName = GetArg(args, "--client");
var pipeName = GetArg(args, "--pipe");

var builder = Host.CreateApplicationBuilder(args);

// CLI arguments take precedence over appsettings.json
if (clientName != null)
    builder.Configuration["RevitMCP:ClientName"] = clientName;
if (pipeName != null)
    builder.Configuration["RevitMCP:PipeName"] = pipeName;

builder.Services.AddSingleton<RevitPipeClient>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RevitMcpTools>();

await builder.Build().RunAsync();

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}
