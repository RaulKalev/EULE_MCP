namespace RevitMCP.Core.Instances;

/// <summary>
/// Routing decision for one bridge request (see <see cref="RevitInstanceRegistry.ResolveRoute"/>):
/// a single target instance, the legacy shared pipe, or an error to return instead of connecting.
/// </summary>
public sealed class InstanceRoute
{
    private InstanceRoute(RevitInstanceInfo? target, bool useLegacyPipe, string? error)
    {
        Target = target;
        UseLegacyPipe = useLegacyPipe;
        Error = error;
    }

    /// <summary>The instance to connect to, or null for the legacy pipe or an error.</summary>
    public RevitInstanceInfo? Target { get; }

    /// <summary>True when no instance is registered and the legacy shared pipe should be tried.</summary>
    public bool UseLegacyPipe { get; }

    /// <summary>Why the request must not be sent; null when there is a target.</summary>
    public string? Error { get; }

    public static InstanceRoute To(RevitInstanceInfo target) => new(target, false, null);
    public static InstanceRoute Legacy() => new(null, true, null);
    public static InstanceRoute Fail(string error) => new(null, false, error);
}
