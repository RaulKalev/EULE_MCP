using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Schedules;

/// <summary>Finds the schedule a tool is about, by id or name, and refuses the kinds the schedule tools do not handle (#103).</summary>
internal static class ScheduleLookup
{
    public const string NotFoundStatus = "schedule_not_found";
    public const string KindUnsupportedStatus = "schedule_kind_unsupported";
    public const string FieldNotFoundStatus = "field_not_found";

    /// <summary>
    /// The schedule named by <paramref name="schedule"/> (element id or name) or <paramref name="scheduleId"/>. For
    /// <paramref name="forEdit"/>, templates, title-block revision schedules and the internal keynote schedule are refused too.
    /// </summary>
    public static ViewSchedule? Resolve(Document doc, McpToolRequest request, string? schedule, long scheduleId, bool forEdit, out McpToolResult? failure)
    {
        failure = null;
        var text = (schedule ?? string.Empty).Trim();
        if (scheduleId <= 0 && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            scheduleId = parsed;

        if (scheduleId <= 0 && text.Length == 0)
        {
            failure = Fail(request, "validation_failed", "Name the schedule: schedule (name or element id) — revit_list_schedules lists them.");
            return null;
        }

        Element? element = null;
        if (scheduleId > 0)
        {
            element = doc.GetElement(new ElementId(scheduleId));
            // A numeric text with no such element may still be a schedule's name.
            if (element == null && text.Length == 0)
            {
                failure = Fail(request, NotFoundStatus, $"No element with id {scheduleId} in '{doc.Title}'.");
                return null;
            }
        }

        if (element == null)
        {
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().ToList();
            element = schedules.FirstOrDefault(s => !s.IsTemplate && string.Equals(s.Name, text, StringComparison.Ordinal))
                      ?? schedules.FirstOrDefault(s => !s.IsTemplate && string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase))
                      ?? schedules.FirstOrDefault(s => !s.IsTemplate && ParameterMatcher.Normalize(s.Name) == ParameterMatcher.Normalize(text));
            if (element == null)
            {
                var panel = new FilteredElementCollector(doc).OfClass(typeof(PanelScheduleView)).Cast<PanelScheduleView>()
                    .FirstOrDefault(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase));
                if (panel != null)
                {
                    failure = PanelSchedule(request, panel);
                    return null;
                }
                var suggestions = SchedulePlanner.Suggest(text, schedules.Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule).Select(s => s.Name));
                failure = Fail(request, NotFoundStatus, $"No schedule named '{text}' in '{doc.Title}'." +
                                                        (suggestions.Count > 0 ? $" Nearest: {string.Join(", ", suggestions.Select(s => $"'{s}'"))}." : " revit_list_schedules lists them."),
                    new { suggestions });
                return null;
            }
        }

        switch (element)
        {
            case PanelScheduleView panelSchedule:
                failure = PanelSchedule(request, panelSchedule);
                return null;
            case ViewSchedule viewSchedule:
                var refusal = Refusal(viewSchedule, forEdit);
                if (refusal != null)
                {
                    failure = Fail(request, KindUnsupportedStatus, refusal);
                    return null;
                }
                return viewSchedule;
            default:
                failure = Fail(request, NotFoundStatus, $"Element {element.Id.Value} ('{element.Name}') is a {element.GetType().Name}, not a schedule.");
                return null;
        }
    }

    /// <summary>Why the schedule tools do not handle this schedule, or null.</summary>
    public static string? Refusal(ViewSchedule schedule, bool forEdit)
    {
        if (schedule.IsTitleblockRevisionSchedule)
            return $"'{schedule.Name}' is the revision schedule of a title block; it is not edited, placed or exported here.";
        if (!forEdit) return null;
        if (schedule.IsTemplate)
            return $"'{schedule.Name}' is a view template, not a schedule.";
        if (schedule.IsInternalKeynoteSchedule)
            return $"'{schedule.Name}' is Revit's internal keynote schedule.";
        if (schedule.Definition?.IsEmbedded == true)
            return $"'{schedule.Name}' is an embedded schedule.";
        return null;
    }

    private static McpToolResult PanelSchedule(McpToolRequest request, PanelScheduleView panel) =>
        Fail(request, KindUnsupportedStatus,
            $"'{panel.Name}' is a panel schedule (PanelScheduleView), which the schedule tools do not handle. Panel schedule data is read with revit_get_circuit_info and the PanelSchematics tools (revit_panelschematics_guide).");

    public static McpToolResult Fail(McpToolRequest request, string status, string message, object? data = null) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = status,
        Message = message,
        Data = data
    };

    /// <summary>Category name of a schedule, or "Multi-Category".</summary>
    public static string CategoryName(Document doc, ScheduleDefinition definition)
    {
        var id = definition.CategoryId;
        if (id == null || id == ElementId.InvalidElementId) return "Multi-Category";
        try { return Category.GetCategory(doc, id)?.Name ?? id.Value.ToString(CultureInfo.InvariantCulture); }
        catch { return id.Value.ToString(CultureInfo.InvariantCulture); }
    }
}
