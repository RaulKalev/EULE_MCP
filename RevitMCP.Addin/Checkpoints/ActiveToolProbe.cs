namespace RevitMCP.Addin.Checkpoints;

/// <summary>
/// The connector tool running on the Revit API thread right now, set by ExternalEventHandler around each
/// tool execution, so a DocumentChanged event raised during it can be attributed (#98). Everything else that
/// changes the model while no tool runs is the user.
/// </summary>
public static class ActiveToolProbe
{
    /// <summary>Client name the model page listener uses for its writes.</summary>
    public const string ModelPageClient = "Model page";

    private static volatile Running? _current;

    public static void Begin(string tool, string? client) => _current = new Running(tool, client);

    public static void End() => _current = null;

    /// <summary>"agent:&lt;tool&gt; (&lt;client&gt;)" while a connector tool runs, "page" for a model page write, else "user".</summary>
    public static string CurrentAttribution()
    {
        var running = _current;
        if (running == null) return ChangeAttribution.User;
        if (string.Equals(running.Client, ModelPageClient, StringComparison.Ordinal)) return ChangeAttribution.Page;
        return ChangeAttribution.Agent(running.Tool, running.Client);
    }

    private sealed class Running
    {
        public Running(string tool, string? client)
        {
            Tool = tool;
            Client = client;
        }

        public string Tool { get; }
        public string? Client { get; }
    }
}
