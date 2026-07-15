using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Parses and validates the 'tasks' argument shared by revit_create_skill and
/// revit_update_skill: an array of { id, enabled?, settings? } objects.
/// </summary>
internal static class SkillTaskArguments
{
    /// <summary>Returns the parsed tasks, or null with an error message when invalid.</summary>
    public static (List<SkillTaskDefinition>? Tasks, string? Error) Parse(
        Dictionary<string, object?> args, SkillTaskRegistry taskRegistry, string key = "tasks")
    {
        if (!args.TryGetValue(key, out var val) || val is null)
            return (null, null);

        JArray? ja = val is JArray j ? j
                   : val is string s ? TryParse(s)
                   : val is System.Collections.IEnumerable e ? TryFromEnumerable(e)
                   : null;
        if (ja is null)
            return (null, "tasks must be a JSON array of { id, enabled, settings } objects.");

        var tasks = new List<SkillTaskDefinition>();
        var unknown = new List<string>();
        foreach (var item in ja.Children<JObject>())
        {
            var id = item.Value<string>("id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
                return (null, "Every task needs an 'id'. Call revit_list_skill_tasks for the catalog.");
            if (!taskRegistry.HasTask(id))
            {
                unknown.Add(id);
                continue;
            }

            var settings = item["settings"] is JObject so
                ? so.Properties().ToDictionary(p => p.Name, p => (object?)p.Value)
                : new Dictionary<string, object?>();

            tasks.Add(new SkillTaskDefinition
            {
                Id = id,
                Enabled = item.Value<bool?>("enabled") ?? true,
                Settings = settings
            });
        }

        if (unknown.Count > 0)
        {
            var available = string.Join(", ", taskRegistry.GetAll().Select(t => t.Id).OrderBy(i => i));
            return (null, $"Unknown task id(s): {string.Join(", ", unknown)}. Available: {available}.");
        }
        if (tasks.Count == 0)
            return (null, "tasks must contain at least one task. Call revit_list_skill_tasks for the catalog.");

        return (tasks, null);
    }

    private static JArray? TryParse(string s) { try { return JArray.Parse(s); } catch { return null; } }
    private static JArray? TryFromEnumerable(System.Collections.IEnumerable e) { try { return JArray.FromObject(e); } catch { return null; } }
}
