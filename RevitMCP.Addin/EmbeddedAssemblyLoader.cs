using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RevitMCP.Addin;

/// <summary>
/// Wires up loading of the dependencies Costura embeds in this assembly.
///
/// Costura's own resolver matches already-loaded assemblies by simple name only. Revit 2024 runs every
/// add-in in one AppDomain, so when another add-in has already loaded an older copy of a shared library
/// (DiRoots ships System.Text.Json 4.0.0.0), Costura hands that copy back and the add-in dies at startup
/// with "Could not load type 'System.Text.Json.Nodes.JsonObject'". On .NET Framework several versions of
/// a strong-named assembly can live side by side, so a version-aware resolver runs ahead of Costura and
/// loads the embedded copy when only older ones are present.
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
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedOverOlderLoaded;
#endif
        // FodyWeavers.xml sets LoadAtModuleInit="false" so the handler above is subscribed first.
        CosturaUtility.Initialize();
    }
#pragma warning restore CA2255

#if REVIT2024
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, Assembly> SideLoaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

    private static Assembly? ResolveEmbeddedOverOlderLoaded(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requested = new AssemblyName(args.Name);
            if (requested.Name is null || requested.Version is null || !string.IsNullOrEmpty(requested.CultureName))
                return null;

            lock (Gate)
            {
                if (SideLoaded.TryGetValue(requested.Name, out var own) && own.GetName().Version >= requested.Version)
                    return own;

                Assembly? best = null;
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = loaded.GetName();
                    if (!string.Equals(name.Name, requested.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Version == requested.Version) return loaded;
                    if (best is null || name.Version > best.GetName().Version) best = loaded;
                }

                // Nothing loaded yet: Costura's default path handles it.
                if (best is null) return null;
                if (best.GetName().Version > requested.Version) return best;

                var bytes = ReadEmbedded(requested.Name);
                if (bytes is null) return null;

                var assembly = Assembly.Load(bytes);
                SideLoaded[requested.Name] = assembly;
                Log($"Loaded embedded {assembly.GetName().Name} {assembly.GetName().Version} next to {best.GetName().Version} from '{best.Location}'");
                return assembly;
            }
        }
        catch
        {
            return null;
        }
    }

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
