using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Task ID: collect.revit.sheets
/// Collects all ViewSheets from the active document and stores them in SharedData["sheets"].
/// Also attempts to detect the project number from common sheet number prefixes.
/// </summary>
internal sealed class CollectRevitSheetsTask : SheetNamingBaseTask
{
    public override string Id   => "collect.revit.sheets";
    public override string Name => "Kogu Revit lehed";

    public override SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var includePlaceholders = SkillSettings.GetBool(taskDef.Settings, "includePlaceholders", false);
            var nimetusParam   = SkillSettings.GetString(taskDef.Settings, "nimetusParamName", "Nimetus");
            var markusParam    = SkillSettings.GetString(taskDef.Settings, "markusParamName",  "Märkus");
            var extraParamNames = SkillSettings.GetStringArray(taskDef.Settings, "extraParamNames", Array.Empty<string>());

            var sheets = new FilteredElementCollector(ctx.Document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate && (includePlaceholders || !s.IsPlaceholder))
                .Select(s => BuildItem(s, nimetusParam, markusParam, extraParamNames))
                .OrderBy(s => s.SheetNumber)
                .ToList();

            ctx.SharedData["sheets"]      = sheets;
            ctx.SharedData["sheetIssues"] = new List<SheetNamingIssue>();

            var projectNumber = DetectProjectNumber(sheets);
            if (!string.IsNullOrEmpty(projectNumber))
                ctx.SharedData["projectNumber"] = projectNumber;

            return OkResult(0, $"Kogutud {sheets.Count} lehte." +
                (string.IsNullOrEmpty(projectNumber) ? "" : $" Projekti tähis: {projectNumber}."), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return FailResult($"Lehtede kogumine ebaõnnestus: {ex.Message}", sw.ElapsedMilliseconds, critical: true);
        }
    }

    private static string DetectProjectNumber(List<SheetNamingItem> sheets) =>
        sheets
            .Where(s => s.SheetNumber.Contains('_'))
            .Select(s => s.SheetNumber.Split('_')[0])
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? string.Empty;

    private static SheetNamingItem BuildItem(ViewSheet s, string nimetusParamName, string markusParamName, string[] extraParamNames) => new()
    {
        SheetId       = s.Id.Value,
        SheetNumber   = s.SheetNumber?.Trim() ?? string.Empty,
        SheetName     = s.Name?.Trim() ?? string.Empty,
        IsPlaceholder = s.IsPlaceholder,
        Nimetus       = ReadParam(s, nimetusParamName),
        Markus        = ReadParam(s, markusParamName),
        ExtParams     = extraParamNames.ToDictionary(p => p, p => ReadParam(s, p)),
    };

    private static string ReadParam(ViewSheet s, string paramName)
    {
        if (string.IsNullOrWhiteSpace(paramName)) return string.Empty;
        var p = s.LookupParameter(paramName);
        if (p is null || p.StorageType != Autodesk.Revit.DB.StorageType.String) return string.Empty;
        return p.AsString()?.Trim() ?? string.Empty;
    }
}
