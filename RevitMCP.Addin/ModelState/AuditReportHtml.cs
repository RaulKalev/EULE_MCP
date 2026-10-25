using System.Globalization;
using System.Text;

namespace RevitMCP.Addin.ModelState;

/// <summary>
/// The model health audit page (#109): a model page (docs/model-pages.md) of plain semantic HTML — an overview, then
/// one section per audit section with its figures (ok / warn / bad badges) and one plain table per list. Rows that are
/// model elements carry <c>data-revit-id</c>, so the served page selects them in Revit. Estonian page language, no
/// external URLs, kept under the model page size cap by shortening the lists. Pure.
/// </summary>
public static class AuditReportHtml
{
    public const int DefaultMaxRows = 500;
    public const string PageIdPrefix = "audit-";

    private static readonly int[] RowSteps = { DefaultMaxRows, 200, 100, 50, 20, 5, 0 };

    /// <summary>The page, with as many list rows per table as fit in <paramref name="maxBytes"/> (UTF-8).</summary>
    public static string Build(AuditReport report, int maxBytes = RevitMCP.Addin.ModelPages.ModelPageStore.MaxHtmlBytes)
    {
        string html = string.Empty;
        foreach (var rows in RowSteps)
        {
            html = Build(report, rows, rows < DefaultMaxRows);
            if (Encoding.UTF8.GetByteCount(html) <= maxBytes) return html;
        }
        return html;
    }

    /// <summary>The page with at most <paramref name="maxRows"/> rows per table.</summary>
    public static string Build(AuditReport report, int maxRows, bool shortened)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"et\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
          .Append(E(Title(report.DocumentTitle))).Append("</title>\n</head>\n<body>\n");

        sb.Append("<h1>").Append(E(Title(report.DocumentTitle))).Append("</h1>\n<p>")
          .Append(E($"{report.DocumentTitle} · Revit {report.RevitVersion} · {report.GeneratedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} · " +
                    $"{Math.Round(report.DurationMs / 1000.0, 1).ToString("0.0", CultureInfo.InvariantCulture)} s"))
          .Append("</p>\n");
        sb.Append("<p>Üldhinnang: ").Append(Badge(report.Status)).Append("</p>\n");
        if (shortened)
            sb.Append("<p>").Append(E($"Loetelud on lühendatud {maxRows} reani tabeli kohta, et leht mahuks; täielikud arvud on näitajates.")).Append("</p>\n");

        Overview(sb, report);
        foreach (var section in report.Sections)
            Section(sb, section, maxRows);

        if (report.ThresholdSources.Count > 0 || report.Warnings.Count > 0)
        {
            sb.Append("<section id=\"audit-settings\">\n<h2>Läved ja märkused</h2>\n<ul>\n");
            foreach (var source in report.ThresholdSources) Li(sb, "Läved: " + source);
            foreach (var warning in report.Warnings) Li(sb, warning);
            sb.Append("</ul>\n</section>\n");
        }

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    public static string Title(string documentTitle) => "Mudeli tervisekontroll: " + documentTitle;

    /// <summary>One page per model: re-running the audit replaces it and keeps the link.</summary>
    public static string PageId(string modelId)
    {
        var sb = new StringBuilder(PageIdPrefix);
        foreach (var c in (modelId ?? string.Empty).ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            if (sb.Length >= 40) break;
        }
        if (sb.Length == PageIdPrefix.Length) sb.Append("model");
        return sb.ToString();
    }

    private static void Overview(StringBuilder sb, AuditReport report)
    {
        sb.Append("<section id=\"overview\">\n<h2>Ülevaade</h2>\n<table>\n<thead><tr><th>Jaotis</th><th>Olek</th><th>Tähelepanu</th><th>ms</th></tr></thead>\n<tbody>\n");
        foreach (var section in report.Sections)
        {
            var flagged = section.Counts.Where(c => c.Status is AuditStatus.Warn or AuditStatus.Bad)
                .Select(c => $"{c.Label}: {c.ValueText}").ToList();
            var attention = section.Error != null ? "Kontroll ebaõnnestus: " + section.Error : string.Join("; ", flagged);
            sb.Append("<tr><td><a href=\"#").Append(E(section.Name)).Append("\">").Append(E(section.Title)).Append("</a></td><td>")
              .Append(Badge(section.Status)).Append("</td><td>").Append(E(attention)).Append("</td><td>")
              .Append(section.DurationMs.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
        }
        sb.Append("</tbody>\n</table>\n</section>\n");
    }

    private static void Section(StringBuilder sb, AuditSection section, int maxRows)
    {
        sb.Append("<section id=\"").Append(E(section.Name)).Append("\">\n<h2>").Append(E(section.Title)).Append(' ')
          .Append(Badge(section.Status)).Append("</h2>\n");
        if (section.Error != null)
            sb.Append("<p><strong>Kontroll ebaõnnestus:</strong> ").Append(E(section.Error)).Append("</p>\n");

        if (section.Counts.Count > 0)
        {
            sb.Append("<ul>\n");
            foreach (var count in section.Counts)
            {
                sb.Append("<li>").Append(E(count.Label)).Append(": <strong>").Append(E(count.ValueText)).Append("</strong>");
                if (count.Status != null) sb.Append(' ').Append(Badge(count.Status));
                if (count.Threshold != null)
                    sb.Append(' ').Append(E($"(lävi {AuditFormat.Number(count.Threshold.Value)})"));
                sb.Append("</li>\n");
            }
            sb.Append("</ul>\n");
        }

        if (section.Sampled)
            sb.Append("<p>").Append(E("Osa näitajaid on valimist või piiratud skaneeringust — vaata märkusi.")).Append("</p>\n");
        foreach (var note in section.Notes)
            sb.Append("<p>").Append(E(note)).Append("</p>\n");

        foreach (var table in section.Tables)
            Table(sb, table, maxRows);
        sb.Append("</section>\n");
    }

    private static void Table(StringBuilder sb, AuditTable table, int maxRows)
    {
        var total = Math.Max(table.Total, table.Rows.Count);
        sb.Append("<h3>").Append(E(table.Title)).Append(" (").Append(total.ToString(CultureInfo.InvariantCulture)).Append(")</h3>\n");
        if (table.Rows.Count == 0)
        {
            sb.Append("<p>Ei ole.</p>\n");
            return;
        }
        if (maxRows <= 0)
        {
            sb.Append("<p>").Append(E("Loetelu jäeti lehe suuruse tõttu välja; vaata tööriista JSON tulemust.")).Append("</p>\n");
            return;
        }

        sb.Append("<table>\n<thead><tr>");
        foreach (var header in table.Headers) sb.Append("<th>").Append(E(header)).Append("</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in table.Rows.Take(maxRows))
        {
            sb.Append(row.ElementId != null
                ? "<tr data-revit-id=\"" + row.ElementId.Value.ToString(CultureInfo.InvariantCulture) + "\">"
                : "<tr>");
            for (var i = 0; i < table.Headers.Count; i++)
                sb.Append("<td>").Append(E(i < row.Cells.Count ? row.Cells[i] : string.Empty)).Append("</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
        var shown = Math.Min(maxRows, table.Rows.Count);
        if (total > shown)
            sb.Append("<p>").Append(E($"Näidatud {shown} rida {total}-st.")).Append("</p>\n");
    }

    private static string Badge(string status) => "<span class=\"" + BadgeClass(status) + "\">" + E(StatusText(status)) + "</span>";

    private static string BadgeClass(string status) => status switch
    {
        AuditStatus.Ok => "ok",
        AuditStatus.Warn => "warn",
        AuditStatus.Bad => "bad",
        AuditStatus.Error => "bad",
        _ => "info"
    };

    public static string StatusText(string status) => status switch
    {
        AuditStatus.Ok => "korras",
        AuditStatus.Warn => "tähelepanu",
        AuditStatus.Bad => "probleem",
        AuditStatus.Error => "viga",
        AuditStatus.Skipped => "ei kohaldu",
        _ => status
    };

    private static void Li(StringBuilder sb, string text) => sb.Append("<li>").Append(E(text)).Append("</li>\n");

    /// <summary>Escapes the HTML specials only: WebUtility would turn õ, ä, ö, ü into numeric entities.</summary>
    private static string E(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text!.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
