using System.IO;
using System.Xml.Linq;

namespace RevitMCP.Setup.Services;

/// <summary>
/// Writes the RevitMCP .addin manifests for Revit 2024 and 2026, pointing at the
/// package folder's DLLs. Prefers the machine-wide ProgramData addin folder and
/// falls back to the per-user %AppData% folder when ProgramData is not writable.
/// Uses the same AddInId as the historical install scripts so re-running never
/// creates a duplicate registration.
/// </summary>
public class RevitAddinInstaller
{
    private const string AddInId = "A1B2C3D4-1111-2222-3333-444455556666";
    private const string ManifestFileName = "RevitMCP.addin";

    public record ManifestStatus(string Year, bool Installed, bool PathMatches, string? CurrentAssembly, string Location);

    private static string ProgramDataDir(string year) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Autodesk", "Revit", "Addins", year);

    private static string AppDataDir(string year) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", year);

    /// <summary>Reads the current state of the manifest for one Revit version.</summary>
    public ManifestStatus GetStatus(string year, string expectedDllPath)
    {
        foreach (var dir in new[] { ProgramDataDir(year), AppDataDir(year) })
        {
            var path = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(path)) continue;

            string? assembly = null;
            try
            {
                assembly = XDocument.Load(path).Descendants("Assembly").FirstOrDefault()?.Value;
            }
            catch { /* unreadable manifest counts as not matching */ }

            var matches = assembly != null &&
                          string.Equals(assembly, expectedDllPath, StringComparison.OrdinalIgnoreCase);
            return new ManifestStatus(year, Installed: true, PathMatches: matches, assembly, path);
        }

        return new ManifestStatus(year, Installed: false, PathMatches: false, null, ProgramDataDir(year));
    }

    /// <summary>
    /// Writes (or rewrites) the manifest for one Revit version. Returns the path written.
    /// </summary>
    public string Install(string year, string dllPath)
    {
        var xml = BuildManifestXml(dllPath);

        try
        {
            return WriteTo(ProgramDataDir(year), xml);
        }
        catch (UnauthorizedAccessException)
        {
            // ProgramData not writable without elevation on this machine — use the per-user folder.
            return WriteTo(AppDataDir(year), xml);
        }
    }

    private static string WriteTo(string dir, string xml)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ManifestFileName);
        File.WriteAllText(path, xml);
        return path;
    }

    private static string BuildManifestXml(string dllPath) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <RevitAddIns>
           <AddIn Type="Application">
             <Name>RevitMCP</Name>
             <Assembly>{dllPath}</Assembly>
             <AddInId>{AddInId}</AddInId>
             <FullClassName>RevitMCP.Addin.App</FullClassName>
             <VendorId>RKTools</VendorId>
             <VendorDescription>RK Tools</VendorDescription>
           </AddIn>
         </RevitAddIns>
         """;
}
