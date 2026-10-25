using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Schedules;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Approval summaries of the schedule write tools (#103), chained into ApprovalSummaryBuilder's default case.
/// Pure (arguments only), so the wording is unit-tested.
/// </summary>
public static class SchedulesApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request) => request.ToolName switch
    {
        "revit_create_schedule" => Create(request.Arguments),
        "revit_edit_schedule" => Edit(request.Arguments),
        "revit_place_schedule_on_sheet" => Place(request.Arguments),
        "revit_export_schedule" => Export(request.Arguments),
        _ => null
    };

    private static string Create(Dictionary<string, object?> args)
    {
        var errors = new List<string>();
        var spec = ScheduleSpecParser.ParseCreate(args, errors);
        var name = string.IsNullOrWhiteSpace(spec.Name) ? "(default name)" : $"'{spec.Name}'";
        var kind = spec.MultiCategory ? "multi-category" : spec.KeySchedule ? $"key schedule of {spec.Category}" : spec.Category;
        var parts = new List<string> { $"{spec.Fields.Count} field(s){Names(spec.Fields.Select(f => f.Heading ?? f.Label))}" };
        if (spec.Filters.Count > 0)
            parts.Add($"{spec.Filters.Count} filter(s): {string.Join("; ", spec.Filters.Select(f => $"{f.Field} {f.Type}{(f.HasValue ? $" '{f.Value}'" : string.Empty)}"))}");
        if (spec.Sort.Count > 0)
            parts.Add("sorted by " + string.Join(", ", spec.Sort.Select(s => s.Field + (s.Descending ? " (descending)" : string.Empty) + (s.Header ? " with headers" : string.Empty))));
        parts.Add(spec.Itemize ? "itemized" : "not itemized");
        if (spec.GrandTotal != null) parts.Add($"grand total: {spec.GrandTotal}");
        if (spec.Template != null) parts.Add($"template '{spec.Template}'");
        if (spec.AllowMissingFields) parts.Add("missing fields allowed");
        return $"Create schedule {name} ({kind}): {string.Join(", ", parts)}.";
    }

    private static string Edit(Dictionary<string, object?> args)
    {
        var errors = new List<string>();
        var edit = ScheduleSpecParser.ParseEdit(args, errors);
        var parts = new List<string>();
        if (edit.AddFields.Count > 0) parts.Add($"add {edit.AddFields.Count} field(s){Names(edit.AddFields.Select(f => f.Heading ?? f.Label))}");
        if (edit.RemoveFields.Count > 0) parts.Add($"remove {edit.RemoveFields.Count} field(s){Names(edit.RemoveFields)}");
        if (edit.Order.Count > 0) parts.Add($"reorder ({string.Join(", ", edit.Order.Take(8))}{(edit.Order.Count > 8 ? ", …" : string.Empty)})");
        if (edit.Rename.Count > 0) parts.Add($"rename {edit.Rename.Count} heading(s){Names(edit.Rename.Select(r => $"{r.Key} → {r.Value}"))}");
        if (edit.Hide.Count > 0) parts.Add($"hide{Names(edit.Hide)}");
        if (edit.Show.Count > 0) parts.Add($"show{Names(edit.Show)}");
        if (edit.Format.Count > 0) parts.Add($"format {edit.Format.Count} field(s)");
        if (edit.Widths.Count > 0) parts.Add($"set {edit.Widths.Count} column width(s)");
        if (edit.SetFilters != null) parts.Add(edit.SetFilters.Count == 0 ? "remove every filter" : $"replace the filters with {edit.SetFilters.Count}");
        if (edit.SetSort != null) parts.Add(edit.SetSort.Count == 0 ? "remove the sorting" : "sort by " + string.Join(", ", edit.SetSort.Select(s => s.Field)));
        if (edit.Itemize.HasValue) parts.Add(edit.Itemize.Value ? "itemize" : "stop itemizing");
        if (edit.GrandTotal != null) parts.Add($"grand total: {edit.GrandTotal}");
        if (edit.AllowMissingFields) parts.Add("missing fields allowed");
        return $"Edit schedule {Schedule(args)}: {(parts.Count == 0 ? "no changes" : string.Join(", ", parts))}.";
    }

    private static string Place(Dictionary<string, object?> args)
    {
        var sheet = Text(args, "sheet");
        string where;
        if (ScheduleSpecParser.Token(Get(args, "nextTo")) is JObject nextTo && nextTo["viewportId"] is { } viewport)
        {
            var side = (string?)nextTo["side"];
            var gap = nextTo["gapMm"]?.ToString();
            where = $"next to viewport {viewport} ({(string.IsNullOrWhiteSpace(side) ? "right" : side)}, gap {(string.IsNullOrWhiteSpace(gap) ? SchedulePlacement.DefaultGapMm.ToString(CultureInfo.InvariantCulture) : gap)} mm)";
        }
        else if (Get(args, "xMm") != null && Get(args, "yMm") != null)
            where = $"with its top-left corner at ({Text(args, "xMm")}, {Text(args, "yMm")}) mm";
        else
            where = "centred on the title block";
        var duplicate = Flag(args, "allowDuplicate") ? " (a second copy allowed)" : string.Empty;
        return $"Place schedule {Schedule(args)} on sheet {(sheet.Length == 0 ? "(none given)" : sheet)} {where}{duplicate}.";
    }

    private static string Export(Dictionary<string, object?> args)
    {
        var format = Text(args, "format");
        var folder = Text(args, "folder");
        var fileName = Text(args, "fileName");
        return $"Export schedule {Schedule(args)} as {(format.Length == 0 ? "csv" : format).ToUpperInvariant()} to " +
               $"{(folder.Length == 0 ? "the default export folder (Documents\\RKTools\\RevitMCP\\Exports)" : folder)}" +
               $"{(fileName.Length > 0 ? $" as '{fileName}'" : string.Empty)}. An existing file is not overwritten.";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Schedule(Dictionary<string, object?> args)
    {
        var schedule = Text(args, "schedule");
        if (schedule.Length > 0) return $"'{schedule}'";
        var id = Text(args, "scheduleId");
        return id.Length > 0 && id != "0" ? $"id {id}" : "(none given)";
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (list.Count == 0) return string.Empty;
        return " (" + string.Join(", ", list.Take(10)) + (list.Count > 10 ? ", …" : string.Empty) + ")";
    }

    private static object? Get(Dictionary<string, object?> args, string key) => args.TryGetValue(key, out var value) ? value : null;

    private static string Text(Dictionary<string, object?> args, string key)
    {
        var token = ScheduleSpecParser.Token(Get(args, key));
        return token == null || token.Type == JTokenType.Null ? string.Empty : token.ToString().Trim();
    }

    private static bool Flag(Dictionary<string, object?> args, string key)
    {
        var token = ScheduleSpecParser.Token(Get(args, key));
        return token != null && (token.Type == JTokenType.Boolean ? token.Value<bool>() : string.Equals(token.ToString(), "true", StringComparison.OrdinalIgnoreCase));
    }
}
