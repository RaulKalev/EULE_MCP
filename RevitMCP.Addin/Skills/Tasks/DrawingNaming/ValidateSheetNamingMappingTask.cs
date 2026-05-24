using System.Diagnostics;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.DrawingNaming.Models;

namespace RevitMCP.Addin.Skills.Tasks.DrawingNaming;

/// <summary>
/// Task ID: naming.validate-mapping
/// Read-only. Computes the expected parameter value for every sheet using the naming mapping
/// defined in task settings, then reports any sheet whose current value does not match.
/// </summary>
internal sealed class ValidateSheetNamingMappingTask : ISkillTask
{
    public string Id           => "naming.validate-mapping";
    public string Name         => "Valideeri lehtede nimetamise vastavus";
    public bool   ChangesModel => false;

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (target, tokens) = ReadSettings(taskDef.Settings);
            if (tokens.Count == 0)
                return Skip("Mapping tokens are empty — configure 'tokens' in the task settings.", sw.ElapsedMilliseconds);

            var sheets = new FilteredElementCollector(ctx.Document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate && !s.IsPlaceholder)
                .ToList();

            var issues   = new List<SkillIssue>();
            var paramNames = tokens.Where(t => t.Type.Equals("Parameter", StringComparison.OrdinalIgnoreCase))
                                   .Select(t => t.Value)
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToList();

            foreach (var sheet in sheets)
            {
                var paramValues = ReadParamValues(sheet, paramNames);
                var expected    = NamingMappingEngine.BuildName(tokens, paramValues);
                var current     = GetTargetValue(sheet, target);

                if (string.IsNullOrEmpty(expected))
                    continue; // All source params are empty — skip this sheet

                if (string.Equals(current, expected, StringComparison.Ordinal))
                    continue;

                issues.Add(new SkillIssue
                {
                    Id              = $"naming-mismatch_{sheet.SheetNumber}",
                    Severity        = "Viga",
                    Category        = "naming.mismatch",
                    Description     = $"[{sheet.SheetNumber}] {target} ei vasta nimistule. " +
                                      $"Praegune: '{current}' → Oodatav: '{expected}'.",
                    SuggestedAction = $"Käivita naming.apply-mapping, et nimetamine automaatselt rakendada.",
                    HostElementId   = sheet.Id.Value,
                });
            }

            return new SkillTaskResult
            {
                TaskId     = Id,
                TaskName   = Name,
                Success    = true,
                IssueCount = issues.Count,
                Message    = issues.Count == 0
                    ? $"Kõik {sheets.Count} lehe {target} vastas nimistule."
                    : $"{issues.Count} lehel {sheets.Count}-st ei vasta {target} nimistule.",
                Issues     = issues,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId          = Id, TaskName = Name, Success = false, CriticalFailure = true,
                Message         = $"Valideerimine ebaõnnestus: {ex.Message}",
                DurationMs      = sw.ElapsedMilliseconds,
            };
        }
    }

    // --- Shared helpers (used by both tasks) ---

    internal static (string Target, List<NamingMappingToken> Tokens) ReadSettings(Dictionary<string, object?> settings)
    {
        var target = SkillSettings.GetString(settings, "target", "Sheet Number");
        var tokens = new List<NamingMappingToken>();

        if (!settings.TryGetValue("tokens", out var raw) || raw is null)
            return (target, tokens);

        try
        {
            JArray ja = raw switch
            {
                JArray arr => arr,
                _ => JArray.Parse(JsonConvert.SerializeObject(raw))
            };
            tokens = ja.ToObject<List<NamingMappingToken>>() ?? tokens;
        }
        catch { /* return empty list — task will skip gracefully */ }

        return (target, tokens);
    }

    internal static IReadOnlyDictionary<string, string> ReadParamValues(ViewSheet sheet, IEnumerable<string> paramNames)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in paramNames)
        {
            var p = sheet.LookupParameter(name);
            dict[name] = (p is not null && p.StorageType == StorageType.String)
                ? p.AsString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        return dict;
    }

    internal static string GetTargetValue(ViewSheet sheet, string target)
    {
        // "Sheet Number" and "Sheet Name" are built-in properties
        if (target.Equals("Sheet Number", StringComparison.OrdinalIgnoreCase))
            return sheet.SheetNumber?.Trim() ?? string.Empty;
        if (target.Equals("Sheet Name", StringComparison.OrdinalIgnoreCase))
            return sheet.Name?.Trim() ?? string.Empty;

        var p = sheet.LookupParameter(target);
        return (p is not null && p.StorageType == StorageType.String)
            ? p.AsString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private SkillTaskResult Skip(string message, long ms) => new()
    {
        TaskId = Id, TaskName = Name, Success = true, WasSkipped = true,
        Message = message, DurationMs = ms,
    };
}
