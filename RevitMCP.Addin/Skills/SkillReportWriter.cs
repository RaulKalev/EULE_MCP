using System.IO;
using Newtonsoft.Json;
using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Writes the final <see cref="SkillRunResult"/> as an indented JSON file to:
/// %AppData%\RKTools\RevitMCP\SkillRuns\{runId}.json
/// </summary>
public class SkillReportWriter
{
    private static readonly string ReportRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RKTools", "RevitMCP", "SkillRuns");

    public string Write(SkillRunResult result)
    {
        Directory.CreateDirectory(ReportRoot);
        var path = Path.Combine(ReportRoot, $"{SanitiseName(result.RunId)}.json");
        var json = JsonConvert.SerializeObject(result, Formatting.Indented);
        File.WriteAllText(path, json);
        return path;
    }

    private static string SanitiseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
