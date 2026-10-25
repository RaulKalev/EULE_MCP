using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Usage reporting (docs/usage-reporting.md).</summary>
internal sealed partial class RevitMcpTools
{
    [McpServerTool(Name = "revit_usage_report"),
     Description("Builds the monthly connector usage report from the shared usage folder (usage.sharedFolder): per user, project, client, tool, read/write and outcome — calls, success rate, writes approved/rejected/expired, elements affected, active days, sessions, response volume, truncated reads, provider cost when an export exists, and an ESTIMATED time saved from the company baseline table. Deterministic, no credits. Writes <reportFolder>\\<yyyy-MM>.html and .xlsx and opens the report as a page. Aggregates only: never quote time saved as measured.")]
    public async Task<string> UsageReport(
        [Description("Month as yyyy-MM. Default: the previous month.")] string? month = null,
        [Description("Shared usage folder overriding usage.sharedFolder for this call.")] string? sharedFolder = null,
        [Description("html, xlsx or both (default).")] string format = "both",
        [Description("Open the report in the user's browser. Default true.")] bool open = true,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["month"] = month ?? string.Empty,
            ["sharedFolder"] = sharedFolder ?? string.Empty,
            ["format"] = format,
            ["open"] = open
        };
        return FormatResult(await pipeClient.SendAsync("revit_usage_report", args, cancellationToken));
    }
}
