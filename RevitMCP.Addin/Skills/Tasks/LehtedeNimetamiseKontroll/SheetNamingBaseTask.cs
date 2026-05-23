using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Base class for all Lehtede Nimetamise Kontroll tasks.
/// Provides shared helpers for accessing and converting SharedData.
/// </summary>
internal abstract class SheetNamingBaseTask : ISkillTask
{
    public abstract string Id   { get; }
    public abstract string Name { get; }
    public virtual  bool ChangesModel => false;

    public abstract SkillTaskResult Run(SkillContext context, SkillTaskDefinition taskDef);

    // --- SharedData helpers ---

    protected static List<SheetNamingIssue> GetOrCreateIssuesList(SkillContext ctx)
    {
        if (ctx.SharedData.TryGetValue("sheetIssues", out var existing) && existing is List<SheetNamingIssue> list)
            return list;
        var newList = new List<SheetNamingIssue>();
        ctx.SharedData["sheetIssues"] = newList;
        return newList;
    }

    protected static List<SheetNamingItem>? GetSheets(SkillContext ctx) =>
        ctx.SharedData.TryGetValue("sheets", out var s) ? s as List<SheetNamingItem> : null;

    protected static List<DocumentRegisterItem>? GetDocumentRegister(SkillContext ctx) =>
        ctx.SharedData.TryGetValue("documentRegister", out var d) ? d as List<DocumentRegisterItem> : null;

    protected static string GetProjectNumber(SkillContext ctx) =>
        ctx.SharedData.TryGetValue("projectNumber", out var p) && p is string s ? s : string.Empty;

    // --- Result factory helpers ---

    protected SkillTaskResult SkipResult(string message, long ms) => new()
    {
        TaskId = Id, TaskName = Name, Success = true, WasSkipped = true,
        Message = message, DurationMs = ms
    };

    protected SkillTaskResult FailResult(string message, long ms, bool critical = false) => new()
    {
        TaskId = Id, TaskName = Name, Success = false, CriticalFailure = critical,
        Message = message, DurationMs = ms
    };

    protected SkillTaskResult OkResult(int issueCount, string message, long ms,
        List<SheetNamingIssue>? addedIssues = null) => new()
    {
        TaskId    = Id,
        TaskName  = Name,
        Success   = true,
        IssueCount = issueCount,
        Message   = message,
        DurationMs = ms,
        Issues    = addedIssues is null ? new() : addedIssues.Select(ToSkillIssue).ToList()
    };

    // --- Conversion ---

    protected static SkillIssue ToSkillIssue(SheetNamingIssue si) => new()
    {
        Id            = $"{si.RuleId}_{si.SheetNumber}",
        Severity      = si.Severity,
        Category      = si.RuleId,
        Description   = string.IsNullOrEmpty(si.SheetNumber)
                          ? si.MessageEt
                          : $"[{si.SheetNumber}] {si.MessageEt}",
        SuggestedAction = si.RecommendationEt,
    };
}
