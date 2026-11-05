using System.Diagnostics;
using System.IO;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>
/// The free ODA File Converter, used as EliteSheets uses it to turn merged DXF drawings into DWG (AutoCAD 2018).
/// </summary>
public static class OdaFileConverter
{
    private const string ExeName = "ODAFileConverter.exe";
    private const string OutputVersion = "ACAD2018";
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The newest ODAFileConverter.exe under Program Files\ODA\ODAFileConverter*\, or null when not installed.</summary>
    public static string? Find()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        var candidates = new List<string>();
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var odaRoot = Path.Combine(root, "ODA");
            if (!Directory.Exists(odaRoot)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(odaRoot, "ODAFileConverter*"))
                {
                    var exe = Path.Combine(dir, ExeName);
                    if (File.Exists(exe)) candidates.Add(exe);
                }
            }
            catch { /* unreadable folder: treat as not installed there */ }
        }
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>Converts every *.dxf in <paramref name="inputFolder"/> to DWG in <paramref name="outputFolder"/>. Null on success, else the error.</summary>
    public static string? ConvertFolder(string converterPath, string inputFolder, string outputFolder)
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            // Args: <in folder> <out folder> <version> <type> <recurse> <audit> [filter]
            var psi = new ProcessStartInfo
            {
                FileName = converterPath,
                Arguments = $"\"{inputFolder.TrimEnd('\\')}\" \"{outputFolder.TrimEnd('\\')}\" {OutputVersion} DWG 0 1 \"*.DXF\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "ODA File Converter could not be started.";
            if (!proc.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { proc.Kill(); } catch { /* already gone */ }
                return "ODA File Converter timed out.";
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
