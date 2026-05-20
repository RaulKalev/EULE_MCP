using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RevitMCP.Bridge;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<RevitPipeClient>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RevitMcpTools>();

await builder.Build().RunAsync();
