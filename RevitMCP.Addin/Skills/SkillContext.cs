using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// Shared runtime context passed to every <see cref="ISkillTask"/> during a skill run.
/// Tasks may read from and write to <see cref="SharedData"/> to pass information to
/// subsequent tasks in the same run (e.g. the 3D view element id produced by
/// CreateReview3DViewTask consumed by downstream visualisation tasks).
/// </summary>
public class SkillContext
{
    public UIApplication UiApplication { get; set; } = null!;
    public Document Document { get; set; } = null!;

    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string SkillId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;

    /// <summary>Absolute path where reports for this run are written.</summary>
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Arbitrary data that tasks can place here for later tasks to read.</summary>
    public Dictionary<string, object> SharedData { get; } = new();
}
