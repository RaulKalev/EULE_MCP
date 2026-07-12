using System.IO;

namespace RevitMCP.Setup.Services;

/// <summary>
/// Resolves and validates the deployed package folder (typically a Dropbox folder
/// populated by Publish-To-Dropbox.bat). All Revit manifests and MCP registrations
/// point directly at these paths.
/// </summary>
public class PackageLayout
{
    public string Root { get; }

    public string Addin2026Dll => Path.Combine(Root, "Addin", "2026", "RevitMCP.Addin.dll");
    public string Addin2024Dll => Path.Combine(Root, "Addin", "2024", "RevitMCP.Addin.dll");
    public string BridgeExe => Path.Combine(Root, "Bridge", "RevitMCP.Bridge.exe");
    public string VersionFile => Path.Combine(Root, "version.txt");

    public PackageLayout(string root)
    {
        Root = root;
    }

    public bool HasAddin2026 => File.Exists(Addin2026Dll);
    public bool HasAddin2024 => File.Exists(Addin2024Dll);
    public bool HasBridge => File.Exists(BridgeExe);
    public bool IsValid => HasAddin2026 && HasAddin2024 && HasBridge;

    public string? Version
    {
        get
        {
            try { return File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null; }
            catch { return null; }
        }
    }

    /// <summary>Human-readable validation summary for the status panel.</summary>
    public string Describe()
    {
        if (string.IsNullOrWhiteSpace(Root)) return "No package folder selected.";
        if (!Directory.Exists(Root)) return "Folder does not exist.";
        if (IsValid)
        {
            var v = Version;
            return v == null ? "Package found." : $"Package found — version {v}.";
        }

        var missing = new List<string>();
        if (!HasAddin2026) missing.Add(@"Addin\2026\RevitMCP.Addin.dll");
        if (!HasAddin2024) missing.Add(@"Addin\2024\RevitMCP.Addin.dll");
        if (!HasBridge) missing.Add(@"Bridge\RevitMCP.Bridge.exe");
        return "Missing: " + string.Join(", ", missing);
    }
}
