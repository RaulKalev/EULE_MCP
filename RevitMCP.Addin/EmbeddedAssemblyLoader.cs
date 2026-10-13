using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RevitMCP.Addin;

/// <summary>
/// Wires up loading of the dependencies Costura embeds in this assembly.
///
/// Revit 2024 runs every add-in in one AppDomain, and the first AssemblyResolve handler to answer wins. Add-ins
/// loaded earlier bring resolvers that answer by simple name (Costura's own does, and DiRoots' loads its
/// System.Text.Json 4.0.0.0 on demand), so left to the chain this add-in gets someone else's older copy and
/// dies at startup with "Could not load type 'System.Text.Json.Nodes.JsonObject'". On .NET Framework several
/// versions of a strong-named assembly can live side by side, so this add-in's resolver is put at the head of
/// the chain and serves its own requests from its embedded copies, whatever else is or is not loaded.
///
/// Must only touch BCL types: it runs before any embedded assembly can be resolved.
/// </summary>
internal static class EmbeddedAssemblyLoader
{
#pragma warning disable CA2255 // ModuleInitializer in a library: intended, the resolver has to be in place before any type loads
    [ModuleInitializer]
    internal static void Initialize()
    {
#if REVIT2024
        SubscribeFirst(ResolveEmbeddedOverOlderLoaded);
#endif
        // FodyWeavers.xml sets LoadAtModuleInit="false" so the handler above is subscribed first.
        CosturaUtility.Initialize();
    }
#pragma warning restore CA2255

#if REVIT2024
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, Assembly> SideLoaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

    private static string _earlierHandlers = "";

    /// <summary>
    /// Puts the handler at the head of the AppDomain's resolve chain. The first handler to return an assembly
    /// wins, and add-ins loaded before this one (they load alphabetically) often carry a name-only resolver
    /// of their own, Costura's included, which would answer with the older copy before ours is asked.
    /// The event has no public way to prepend, hence the private field; falls back to a normal subscription.
    /// </summary>
    private static void SubscribeFirst(ResolveEventHandler handler)
    {
        var domain = AppDomain.CurrentDomain;
        try
        {
            var field = typeof(AppDomain).GetField("_AssemblyResolve", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(ResolveEventHandler))
            {
                // Same lock the event's own add/remove accessors take.
                lock (domain)
                {
                    var existing = (ResolveEventHandler?)field.GetValue(domain);
                    _earlierHandlers = Describe(existing);
                    field.SetValue(domain, (ResolveEventHandler)Delegate.Combine(handler, existing));
                }
                return;
            }
        }
        catch { }

        _earlierHandlers = "unknown (could not prepend)";
        domain.AssemblyResolve += handler;
    }

    private static string Describe(ResolveEventHandler? handlers)
    {
        if (handlers is null) return "none";
        var owners = new List<string>();
        foreach (var handler in handlers.GetInvocationList())
        {
            string owner;
            try { owner = handler.Method.DeclaringType?.Assembly.GetName().Name ?? "?"; }
            catch { owner = "?"; }
            if (!owners.Contains(owner)) owners.Add(owner);
        }
        return string.Join(", ", owners);
    }

    // This add-in's assembly and every embedded assembly this resolver loaded: requests from these are ours to answer.
    private static readonly HashSet<Assembly> Family = new HashSet<Assembly> { typeof(EmbeddedAssemblyLoader).Assembly };

    /// <summary>
    /// Answers a request with this add-in's embedded copy, and never passes a request of its own down the chain.
    /// Passing it down was the bug behind the Revit 2024 start failure: with no System.Text.Json loaded yet, the
    /// next resolver (DiRoots') loaded its own 4.0.0.0 for it. Who asks decides the answer:
    ///  * this add-in or one of its embedded assemblies: always the embedded copy;
    ///  * another add-in's assembly: nothing — its dependencies are its own business;
    ///  * no requester or a framework assembly (Assembly.Load by name, WPF loading a XAML assembly): the embedded
    ///    copy only when the request names exactly its version, otherwise an already loaded assembly of exactly
    ///    the requested version.
    /// </summary>
    private static Assembly? ResolveEmbeddedOverOlderLoaded(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requested = new AssemblyName(args.Name);
            if (requested.Name is null || !string.IsNullOrEmpty(requested.CultureName))
                return null;

            lock (Gate)
            {
                var requester = args.RequestingAssembly;
                bool ours = requester != null && Family.Contains(requester);
                if (requester != null && !ours && !requester.GlobalAssemblyCache)
                    return null;

                if (!SideLoaded.TryGetValue(requested.Name, out var own))
                {
                    if (!ours)
                    {
                        // Unknown requester: an assembly of exactly that version that is already loaded is the one meant
                        if (requested.Version is null) return null;
                        var exact = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                        {
                            var name = a.GetName();
                            return string.Equals(name.Name, requested.Name, StringComparison.OrdinalIgnoreCase) && name.Version == requested.Version;
                        });
                        if (exact != null) return exact;
                    }

                    var bytes = ReadEmbedded(requested.Name);
                    if (bytes is null) return null;

                    own = Assembly.Load(bytes);
                    SideLoaded[requested.Name] = own;
                    Family.Add(own);

                    var others = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => a != own && string.Equals(a.GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(a => $"{a.GetName().Version} from '{a.Location}'").ToList();
                    if (others.Count > 0)
                    {
                        Log($"Loaded embedded {own.GetName().Name} {own.GetName().Version} next to {string.Join(", ", others)}" +
                            (_loggedHandlers ? "" : $" (resolvers subscribed before this add-in: {_earlierHandlers})"));
                        _loggedHandlers = true;
                    }
                }

                return ours || requested.Version == own.GetName().Version ? own : null;
            }
        }
        catch (Exception ex)
        {
            Log($"Resolving '{args.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool _loggedHandlers;

    // Same file as App.DiagLog, written directly: App cannot be touched from a resolver (it needs RevitAPIUI).
    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP_startup.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static byte[]? ReadEmbedded(string assemblyName)
    {
        var self = typeof(EmbeddedAssemblyLoader).Assembly;
        var resource = "costura." + assemblyName.ToLowerInvariant() + ".dll";

        using var compressed = self.GetManifestResourceStream(resource + ".compressed");
        if (compressed != null)
        {
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            deflate.CopyTo(buffer);
            return buffer.ToArray();
        }

        using var plain = self.GetManifestResourceStream(resource);
        if (plain is null) return null;
        using var copy = new MemoryStream();
        plain.CopyTo(copy);
        return copy.ToArray();
    }
#endif
}
