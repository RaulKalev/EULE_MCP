using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;

namespace RevitMCP.Addin.Usage;

/// <summary>
/// Writes a month's usage report (docs/usage-reporting.md, "Monthly report") as HTML in the model-page styling — plain
/// semantic markup, one plain table per grouping, so a served copy gets search, filters, sorting and grouping — and as
/// an Excel workbook with the same tables. Time saved is labelled an estimate wherever it appears, with the baseline
/// table beside it. Pure — no Revit API.
/// </summary>
public static class UsageReportWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] Columns =
    {
        "Calls", "Success rate", "Failures", "Writes", "Approved", "Rejected", "Expired", "Approvals asked",
        "Elements affected", "Active days", "Sessions", "First use", "Last use", "Response KB", "Truncated reads",
        "Summarised reads", "Time saved, h (estimate)"
    };

    public static string PageId(string month) => "usage-" + month;

    public static string Title(string month) => "Connector usage " + month;

    /// <param name="css">The model-page stylesheet to embed for the standalone file; null when the page is served.</param>
    public static string Html(UsageReportData data, string? css = null)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(Title(data.Month))).Append("</title>");
        if (!string.IsNullOrEmpty(css)) sb.Append("<style>").Append(css).Append("</style>");
        sb.Append("</head><body><main>");
        sb.Append("<h1>").Append(E(Title(data.Month))).Append("</h1>");
        sb.Append("<p class=\"muted\">Generated ").Append(E(data.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv)))
          .Append(" from ").Append(data.Files).Append(" log files, ").Append(Num(data.Lines)).Append(" calls")
          .Append(data.Unreadable > 0 ? $", {data.Unreadable} unreadable lines skipped" : string.Empty)
          .Append(". Produced by code from the activity log: no prompts, responses, arguments or results are included.</p>");

        var t = data.Total;
        sb.Append("<div class=\"grid\">");
        Card(sb, "Calls", Num(t.Calls));
        Card(sb, "Users", data.ByUser.Count.ToString(Inv));
        Card(sb, "Projects", data.ByProject.Count.ToString(Inv));
        Card(sb, "Success rate", Percent(t.SuccessRate));
        Card(sb, "Elements affected", Num(t.Affected));
        Card(sb, "Rejected writes", Num(t.Rejected));
        Card(sb, "Time saved (estimate)", Hours(t.SavedSeconds) + " h");
        if (data.Costs.Present) Card(sb, "Provider cost", data.Costs.Rows.Sum(r => r.Cost).ToString("0.00", Inv));
        sb.Append("</div>");

        Section(sb, "By user", data.ByUser, data, user: true);
        Section(sb, "By project", data.ByProject, data);
        Section(sb, "By client", data.ByClient, data);
        Section(sb, "By tool", data.ByTool, data);
        Section(sb, "Read and write", data.ByPermission, data);
        Section(sb, "By outcome", data.ByOutcome, data);

        Costs(sb, data);
        Baselines(sb, data);

        sb.Append("<h2>How to read this</h2><ul>")
          .Append("<li><b>Calls</b> count executed calls and approval decisions; the placeholder answer of an approval-gated call is counted only under <i>Approvals asked</i>.</li>")
          .Append("<li><b>Success rate</b> is successes ÷ (successes + failures); rejected and expired requests are a person's decision, not a failure.</li>")
          .Append("<li><b>Elements affected</b> sums the counts tools report (placed, updated, created, deleted, returned …) for successful calls.</li>")
          .Append("<li><b>Sessions</b> are runs of calls by one user and client without a gap longer than 30 minutes.</li>")
          .Append("<li><b>Time saved</b> is a theoretical estimate: the baseline below × elements affected (or per call), minus the call's own duration, for successful calls of tools in the table. It is not measured time.</li>")
          .Append("</ul>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<UsageGroup> groups, UsageReportData data, bool user = false)
    {
        sb.Append("<h2>").Append(E(title)).Append("</h2>");
        if (groups.Count == 0)
        {
            sb.Append("<p class=\"muted\">No calls.</p>");
            return;
        }
        // 20+ columns: the table scrolls sideways inside the page instead of widening it.
        sb.Append("<div style=\"overflow-x:auto\"><table data-revit-tools=\"rows\"><thead><tr><th>").Append(E(title.Replace("By ", string.Empty))).Append("</th>");
        foreach (var c in Columns) sb.Append("<th>").Append(E(c)).Append("</th>");
        if (user) sb.Append(data.Costs.Present ? "<th>Cost</th><th>Input tokens</th><th>Output tokens</th>" : "<th>≈ Output tokens (proxy)</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var g in groups)
        {
            sb.Append("<tr><td>").Append(E(g.Key)).Append("</td>");
            foreach (var v in Values(g)) sb.Append(v.Sort == null ? "<td class=\"num\">" : $"<td class=\"num\" data-sort=\"{E(v.Sort)}\">").Append(E(v.Text)).Append("</td>");
            if (user)
            {
                if (data.Costs.Present)
                {
                    var rows = data.Costs.For(g.Key);
                    sb.Append("<td class=\"num\">").Append(rows.Count == 0 ? "–" : rows.Sum(r => r.Cost).ToString("0.00", Inv)).Append("</td>")
                      .Append("<td class=\"num\">").Append(rows.Count == 0 ? "–" : Num(rows.Sum(r => r.InputTokens))).Append("</td>")
                      .Append("<td class=\"num\">").Append(rows.Count == 0 ? "–" : Num(rows.Sum(r => r.OutputTokens))).Append("</td>");
                }
                else sb.Append("<td class=\"num\">").Append(Num(g.ResponseBytes / 4)).Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    /// <summary>The cells of one group in <see cref="Columns"/> order, with a sort value where the text is not one.</summary>
    private static IEnumerable<(string Text, string? Sort)> Values(UsageGroup g)
    {
        yield return (Num(g.Calls), null);
        yield return (Percent(g.SuccessRate), g.SuccessRate?.ToString("0.0000", Inv) ?? "-1");
        yield return (Num(g.Failures), null);
        yield return (Num(g.Writes), null);
        yield return (Num(g.Approved), null);
        yield return (Num(g.Rejected), null);
        yield return (Num(g.Expired), null);
        yield return (Num(g.ApprovalsRequested), null);
        yield return (Num(g.Affected), null);
        yield return (Num(g.ActiveDays), null);
        yield return (Num(g.Sessions), null);
        yield return (Date(g.First), g.First?.ToString("o", Inv));
        yield return (Date(g.Last), g.Last?.ToString("o", Inv));
        yield return ((g.ResponseBytes / 1024).ToString("N0", Inv), g.ResponseBytes.ToString(Inv));
        yield return (Num(g.Truncated), null);
        yield return (Num(g.Summarised), null);
        yield return (Hours(g.SavedSeconds), g.SavedSeconds.ToString("0", Inv));
    }

    private static void Costs(StringBuilder sb, UsageReportData data)
    {
        sb.Append("<h2>Credit costs</h2>");
        if (data.Costs.Present)
            sb.Append("<p>From the provider export <code>").Append(E(data.Costs.Source)).Append("</code>, joined by user. The connector itself never sees tokens or money.</p>");
        else
            sb.Append("<p class=\"muted\">No provider export for this month (<code>").Append(E(data.Month)).Append(".csv</code> in the cost export folder, columns user, client, cost, inputTokens, outputTokens). ")
              .Append("The user table shows response size ÷ 4 as a proxy for the output tokens the connector caused — labelled as such, not a cost.</p>");
        foreach (var p in data.Costs.Problems) sb.Append("<p class=\"badge warn\">").Append(E(p)).Append("</p>");
    }

    private static void Baselines(StringBuilder sb, UsageReportData data)
    {
        sb.Append("<h2>Time-saved baselines (estimate)</h2>");
        if (data.Baselines.Count == 0)
        {
            sb.Append("<p class=\"muted\">No baselines configured (<code>usage.timeSavedBaselines</code> in the company config), so no time is estimated.</p>");
            return;
        }
        sb.Append("<p class=\"muted\">Manual time a person would spend without the connector. Agreed in the company config, not measured.</p>");
        sb.Append("<table data-revit-tools=\"rows\"><thead><tr><th>Tool</th><th>Seconds per element</th><th>Seconds per call</th></tr></thead><tbody>");
        foreach (var b in data.Baselines.OrderBy(b => b.Key, StringComparer.Ordinal))
            sb.Append("<tr><td>").Append(E(b.Key)).Append("</td><td class=\"num\">").Append(b.Value.SecondsPerElement?.ToString("0.##", Inv) ?? "–")
              .Append("</td><td class=\"num\">").Append(b.Value.SecondsPerCall?.ToString("0.##", Inv) ?? "–").Append("</td></tr>");
        sb.Append("</tbody></table>");
    }

    // ── Excel ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same tables as a workbook: one sheet per grouping, then costs, baselines and notes.</summary>
    public static void Excel(UsageReportData data, string path)
    {
        using var wb = new XLWorkbook();
        Sheet(wb, "Users", data.ByUser, data, user: true);
        Sheet(wb, "Projects", data.ByProject, data);
        Sheet(wb, "Clients", data.ByClient, data);
        Sheet(wb, "Tools", data.ByTool, data);
        Sheet(wb, "Read-write", data.ByPermission, data);
        Sheet(wb, "Outcomes", data.ByOutcome, data);

        var baselines = wb.Worksheets.Add("Baselines (estimate)");
        baselines.Cell(1, 1).Value = "Tool";
        baselines.Cell(1, 2).Value = "Seconds per element";
        baselines.Cell(1, 3).Value = "Seconds per call";
        var row = 2;
        foreach (var b in data.Baselines.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            baselines.Cell(row, 1).Value = b.Key;
            if (b.Value.SecondsPerElement != null) baselines.Cell(row, 2).Value = b.Value.SecondsPerElement.Value;
            if (b.Value.SecondsPerCall != null) baselines.Cell(row, 3).Value = b.Value.SecondsPerCall.Value;
            row++;
        }
        Style(baselines, 3);

        var about = wb.Worksheets.Add("About");
        about.Cell(1, 1).Value = Title(data.Month);
        about.Cell(2, 1).Value = $"Generated {data.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm} from {data.Files} log files, {data.Lines} calls.";
        about.Cell(3, 1).Value = "Time saved is a theoretical estimate from the baseline table, not measured time.";
        about.Cell(4, 1).Value = data.Costs.Present
            ? "Costs from the provider export " + data.Costs.Source + ", joined by user."
            : "No provider export: the Users sheet shows response bytes ÷ 4 as a proxy for output tokens.";
        about.Column(1).Width = 110;
        wb.SaveAs(path);
    }

    private static void Sheet(XLWorkbook wb, string name, List<UsageGroup> groups, UsageReportData data, bool user = false)
    {
        var ws = wb.Worksheets.Add(name);
        ws.Cell(1, 1).Value = name.TrimEnd('s');
        for (var i = 0; i < Columns.Length; i++) ws.Cell(1, i + 2).Value = Columns[i];
        var extra = user ? (data.Costs.Present ? new[] { "Cost", "Input tokens", "Output tokens" } : new[] { "≈ Output tokens (proxy)" }) : Array.Empty<string>();
        for (var i = 0; i < extra.Length; i++) ws.Cell(1, Columns.Length + 2 + i).Value = extra[i];

        var row = 2;
        foreach (var g in groups)
        {
            var c = 1;
            ws.Cell(row, c++).Value = g.Key;
            ws.Cell(row, c++).Value = g.Calls;
            if (g.SuccessRate != null) ws.Cell(row, c).Value = Math.Round(g.SuccessRate.Value, 4);
            ws.Cell(row, c++).Style.NumberFormat.Format = "0.0%";
            ws.Cell(row, c++).Value = g.Failures;
            ws.Cell(row, c++).Value = g.Writes;
            ws.Cell(row, c++).Value = g.Approved;
            ws.Cell(row, c++).Value = g.Rejected;
            ws.Cell(row, c++).Value = g.Expired;
            ws.Cell(row, c++).Value = g.ApprovalsRequested;
            ws.Cell(row, c++).Value = g.Affected;
            ws.Cell(row, c++).Value = g.ActiveDays;
            ws.Cell(row, c++).Value = g.Sessions;
            if (g.First != null) ws.Cell(row, c).Value = g.First.Value.LocalDateTime;
            c++;
            if (g.Last != null) ws.Cell(row, c).Value = g.Last.Value.LocalDateTime;
            c++;
            ws.Cell(row, c++).Value = Math.Round(g.ResponseBytes / 1024.0, 1);
            ws.Cell(row, c++).Value = g.Truncated;
            ws.Cell(row, c++).Value = g.Summarised;
            ws.Cell(row, c++).Value = Math.Round(g.SavedSeconds / 3600.0, 2);
            if (user)
            {
                if (data.Costs.Present)
                {
                    var rows = data.Costs.For(g.Key);
                    if (rows.Count > 0)
                    {
                        ws.Cell(row, c).Value = rows.Sum(r => r.Cost);
                        ws.Cell(row, c + 1).Value = rows.Sum(r => r.InputTokens);
                        ws.Cell(row, c + 2).Value = rows.Sum(r => r.OutputTokens);
                    }
                }
                else ws.Cell(row, c).Value = g.ResponseBytes / 4;
            }
            row++;
        }
        Style(ws, Columns.Length + 1 + extra.Length);
    }

    private static void Style(IXLWorksheet ws, int columns)
    {
        var header = ws.Range(1, 1, 1, columns);
        header.Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, columns).AdjustToContents();
    }

    // ── Formatting ───────────────────────────────────────────────────────────────────────────────────────

    private static void Card(StringBuilder sb, string label, string value) =>
        sb.Append("<div class=\"card\"><div class=\"stat\">").Append(E(value)).Append("</div><div class=\"muted\">").Append(E(label)).Append("</div></div>");

    private static string Num(long n) => n.ToString("N0", Inv);

    private static string Percent(double? rate) => rate == null ? "–" : (rate.Value * 100).ToString("0.0", Inv) + " %";

    private static string Hours(double seconds) => (seconds / 3600.0).ToString("0.0", Inv);

    private static string Date(DateTimeOffset? d) => d == null ? "–" : d.Value.ToLocalTime().ToString("yyyy-MM-dd", Inv);

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
