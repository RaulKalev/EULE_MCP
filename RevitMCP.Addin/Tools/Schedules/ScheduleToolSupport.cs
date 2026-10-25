using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Schedules;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Schedules;

/// <summary>Shared pieces of the schedule tools (#103): results, category resolution, plan shaping.</summary>
internal static class ScheduleToolSupport
{
    public static McpToolResult Ok(McpToolRequest request, Stopwatch sw, string message, object data, List<string>? warnings = null)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = message,
            Data = data,
            Warnings = warnings ?? new List<string>(),
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    public static McpToolResult Fail(McpToolRequest request, string status, string message, object? data = null, List<string>? warnings = null, Stopwatch? sw = null)
    {
        sw?.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Status = status,
            Message = message,
            Data = data,
            Warnings = warnings ?? new List<string>(),
            DurationMs = sw?.ElapsedMilliseconds ?? 0
        };
    }

    /// <summary>Why the document cannot take a (trial) transaction now, or null.</summary>
    public static string? TransactionBlocker(Document doc)
    {
        if (doc.IsReadOnly) return $"'{doc.Title}' is read-only.";
        if (doc.IsModifiable) return $"'{doc.Title}' has an open transaction (Revit is in an edit mode or another command runs). Finish it and try again.";
        return null;
    }

    /// <summary>
    /// The category a schedule is made for: <paramref name="multiCategory"/> gives InvalidElementId; otherwise the
    /// category by name (CategoryResolver), checked against what Revit can schedule (or key-schedule).
    /// </summary>
    public static ElementId? ResolveCategory(Document doc, string name, bool multiCategory, bool keySchedule, out string? error, out List<string> suggestions)
    {
        error = null;
        suggestions = new List<string>();
        if (multiCategory) return ElementId.InvalidElementId;

        var resolved = new CategoryResolver().Resolve(doc, name);
        if (resolved.Category == null)
        {
            suggestions = resolved.Suggestions;
            error = $"Category '{name}' not found." + (suggestions.Count > 0 ? $" Nearest: {string.Join(", ", suggestions)}." : string.Empty);
            return null;
        }

        var id = resolved.Category.Id;
        var valid = keySchedule ? ViewSchedule.IsValidCategoryForKeySchedule(id) : ViewSchedule.IsValidCategoryForSchedule(id);
        if (!valid)
        {
            error = $"Revit cannot make a {(keySchedule ? "key schedule" : "schedule")} for '{resolved.Category.Name}'.";
            return null;
        }
        return id;
    }

    public static ViewSchedule CreateView(Document doc, ElementId categoryId, bool keySchedule) =>
        keySchedule ? ViewSchedule.CreateKeySchedule(doc, categoryId) : ViewSchedule.CreateSchedule(doc, categoryId);

    /// <summary>Every view name in the document (schedule names must not repeat one).</summary>
    public static HashSet<string> TakenViewNames(Document doc) =>
        new(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);

    public static object DescribeMissing(SchedulePlan plan) => plan.MissingFields.Select(m => new
    {
        name = m.Name,
        role = m.Role,
        reason = m.Reason,
        suggestions = m.Suggestions
    }).ToList();

    public static object DescribePlan(SchedulePlan plan) => new
    {
        name = plan.Name,
        fields = plan.Fields.Select(f => new
        {
            name = f.QualifiedName,
            heading = f.Heading,
            fieldType = f.FieldType,
            parameterId = f.ParameterId,
            hidden = f.Hidden,
            source = f.Source,
            addedForFilterOrSort = f.AddedForFilterOrSort ? true : (bool?)null
        }).ToList(),
        steps = plan.Actions.Select(a => a.Description).ToList(),
        missingFields = DescribeMissing(plan),
        errors = plan.Errors
    };

    public static object DescribeExecution(ScheduleExecution execution) => new
    {
        steps = execution.Results.Count,
        failed = execution.Failed.Select(r => new { step = r.Description, error = r.Error }).ToList(),
        notes = execution.Results.Where(r => r.Ok && r.Note != null).Select(r => $"{r.Description}: {r.Note}").ToList()
    };

    /// <summary>The message for a plan that cannot be applied.</summary>
    public static string PlanRefusal(SchedulePlan plan)
    {
        if (plan.Errors.Count > 0) return string.Join(" ", plan.Errors);
        var parts = plan.MissingFields.Select(m =>
            $"'{m.Name}' ({m.Role})" + (m.Reason != null ? $": {m.Reason}" : m.Suggestions.Count > 0 ? $" — nearest: {string.Join(", ", m.Suggestions)}" : string.Empty));
        return $"{plan.MissingFields.Count} name(s) not found: {string.Join("; ", parts)}. Fix them, or pass allowMissingFields=true to build without them.";
    }

    /// <summary>Data rows of a table, first <paramref name="max"/>, as string arrays.</summary>
    public static List<string[]> Sample(ScheduleTable table, int max) => table.DataRows.Take(max).ToList();
}

/// <summary>Thrown inside a schedule transaction to roll it back after the plan or a step was refused.</summary>
internal sealed class ScheduleAbortException : Exception
{
    public ScheduleAbortException(string message) : base(message) { }
}
