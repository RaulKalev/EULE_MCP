using System.Globalization;
using System.Net;
using System.Text;
using RevitMCP.Addin.Memory.Hosting;

namespace RevitMCP.Addin.Memory;

/// <summary>
/// The memory review page (docs/shared-memory.md, "Human review"), served as a model page. Plain semantic HTML in the
/// model-page theme: decisions waiting (consolidation proposals, flip-flops) at the top, the digest (unused,
/// contradicted, flagged, waiting for confirmation), then every entry with its state, source, counters and staleness.
/// Buttons call <c>revit.memory(...)</c> from the page helper, which sends the action through the connector's approval
/// path and reloads the page once it is done. Pure — no Revit API.
/// </summary>
public static class MemoryPageHtml
{
    /// <summary>
    /// Page ids starting with this are reserved for the review page: revit_publish_page refuses them, and the listener
    /// accepts memory actions only from them, so a page an agent generated cannot change memory.
    /// </summary>
    public const string PageIdPrefix = "memory-";

    public static bool IsMemoryPage(string? pageId) => pageId != null && pageId.StartsWith(PageIdPrefix, StringComparison.Ordinal);

    public static string PageId(string? project)
    {
        var slug = MemoryPolicy.Slug(project ?? string.Empty).Replace('.', '-').Replace('_', '-');
        if (slug.Length == 0) slug = "unfiled";
        if (slug.Length > 30) slug = slug.Substring(0, 30).TrimEnd('-');
        return PageIdPrefix + slug;
    }

    public static string Title(string? project) => string.IsNullOrWhiteSpace(project) ? "Memory" : "Memory — " + project;

    public static string Build(MemoryReviewData data)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(Title(data.Project))).Append("</title>");
        sb.Append("<style>td.text{min-width:16em;max-width:32em}td.actions{white-space:nowrap}.memory-key{font-family:ui-monospace,monospace;font-size:.85em}</style>");
        sb.Append("</head><body><main>");
        sb.Append("<h1>").Append(E(Title(data.Project))).Append("</h1>");
        sb.Append("<p class=\"muted\">").Append(E(MemoryRecall.Note)).Append(" Actions here need approval in Revit, like an agent's write. ")
          .Append("Generated ").Append(E(data.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append(".</p>");

        Stats(sb, data);
        foreach (var w in data.Warnings) sb.Append("<p class=\"badge warn\">").Append(E(w)).Append("</p>");

        Decisions(sb, data);
        Digest(sb, data);

        sb.Append("<h2>All entries</h2>");
        Table(sb, data.Entries, data, "all", "rows");

        sb.Append("<h2>Where it is stored</h2><ul>");
        foreach (var t in data.Tiers)
            sb.Append("<li>").Append(E(t.Scope)).Append(": <code>").Append(E(t.Folder)).Append("</code> (").Append(t.Entries)
              .Append(" entries").Append(t.FromMirror ? ", read from the local mirror" : string.Empty).Append(")</li>");
        sb.Append("</ul>");

        sb.Append("</main>").Append(Script).Append("</body></html>");
        return sb.ToString();
    }

    private static void Stats(StringBuilder sb, MemoryReviewData data)
    {
        var active = data.Entries.Where(h => !h.Entry.IsRetired).ToList();
        var shown = active.Sum(h => h.Entry.Counters.Shown);
        var used = active.Sum(h => h.Entry.Counters.Used);
        sb.Append("<div class=\"grid\">");
        Card(sb, "Active entries", active.Count.ToString(CultureInfo.InvariantCulture));
        Card(sb, "Confirmed", active.Count(h => h.Entry.State == MemoryStates.Confirmed).ToString(CultureInfo.InvariantCulture));
        Card(sb, "Waiting for confirmation", data.Waiting.Count.ToString(CultureInfo.InvariantCulture));
        Card(sb, "Dormant", active.Count(h => h.Entry.State == MemoryStates.Dormant).ToString(CultureInfo.InvariantCulture));
        Card(sb, "Hit rate (used / shown)", shown == 0 ? "–" : (100.0 * used / shown).ToString("0", CultureInfo.InvariantCulture) + " %");
        Card(sb, "Proposals waiting", data.Proposals.Count(p => p.Status == MemoryProposalStatus.Pending).ToString(CultureInfo.InvariantCulture));
        sb.Append("</div>");
    }

    private static void Card(StringBuilder sb, string label, string value) =>
        sb.Append("<div class=\"card\"><div class=\"stat\">").Append(E(value)).Append("</div><div class=\"muted\">").Append(E(label)).Append("</div></div>");

    private static void Decisions(StringBuilder sb, MemoryReviewData data)
    {
        var pending = data.Proposals.Where(p => p.Status == MemoryProposalStatus.Pending).ToList();
        sb.Append("<h2>Needs a decision</h2>");
        if (pending.Count == 0 && data.Flagged.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nothing is waiting: no consolidation proposals and no disagreements.</p>");
            return;
        }

        if (pending.Count > 0)
        {
            var byId = data.Entries.ToDictionary(h => h.Entry.Id, h => h.Entry, StringComparer.OrdinalIgnoreCase);
            sb.Append("<h3>Consolidation proposals</h3><table data-revit-tools=\"none\"><thead><tr><th>Kind</th><th>Entries now</th><th>Proposed</th><th>Why</th><th>By</th><th></th></tr></thead><tbody>");
            foreach (var p in pending)
            {
                sb.Append("<tr data-memory-proposal-row=\"").Append(E(p.Id)).Append("\"><td><span class=\"badge\">").Append(E(p.Kind)).Append("</span>");
                if (!string.IsNullOrEmpty(p.Target)) sb.Append(" → ").Append(E(p.Target!));
                sb.Append("</td><td class=\"text\"><ul>");
                foreach (var id in p.EntryIds)
                    sb.Append("<li>").Append(byId.TryGetValue(id, out var e) ? "<span class=\"memory-key\">" + E(e.Key) + "</span>: " + E(e.Value) : E(id) + " (another project)").Append("</li>");
                sb.Append("</ul></td><td class=\"text\">");
                if (!string.IsNullOrEmpty(p.Value)) sb.Append(E(p.Value!));
                if (!string.IsNullOrEmpty(p.Reason)) sb.Append("<br><span class=\"muted\">").Append(E(p.Reason!)).Append("</span>");
                if (!string.IsNullOrEmpty(p.Key)) sb.Append("<br><span class=\"memory-key\">").Append(E(p.Key!)).Append("</span>");
                sb.Append("</td><td class=\"text\">").Append(E(p.Note)).Append("</td><td>").Append(E(p.CreatedBy))
                  .Append("<br><span class=\"muted\">").Append(E(p.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append("</span></td><td class=\"actions\">")
                  .Append(Button(MemoryReviewActions.Accept, "Accept", proposal: p.Id))
                  .Append(Button(MemoryReviewActions.Reject, "Reject", proposal: p.Id, ghost: true))
                  .Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        if (data.Flagged.Count > 0)
        {
            sb.Append("<h3>Disagreements (the value went back and forth between people)</h3>");
            Table(sb, data.Flagged, data, "flagged", "none");
        }
    }

    private static void Digest(StringBuilder sb, MemoryReviewData data)
    {
        sb.Append("<h2>Digest</h2><p class=\"muted\">The monthly look: what is not used, what people contradict, and what waits for confirmation.</p>");
        Section(sb, data, "Unused for 30 days or dormant", data.Unused, "unused");
        Section(sb, data, "Contradicted by later actions", data.Contradicted, "contradicted");
        Section(sb, data, "Waiting for confirmation", data.Waiting, "waiting");
    }

    private static void Section(StringBuilder sb, MemoryReviewData data, string title, List<MemoryHit> hits, string id)
    {
        sb.Append("<h3>").Append(E(title)).Append(" <span class=\"badge\">").Append(hits.Count).Append("</span></h3>");
        if (hits.Count == 0) sb.Append("<p class=\"muted\">None.</p>");
        else Table(sb, hits, data, id, "none");
    }

    private static void Table(StringBuilder sb, List<MemoryHit> hits, MemoryReviewData data, string id, string tools)
    {
        sb.Append("<table id=\"memory-").Append(E(id)).Append("\" data-revit-tools=\"").Append(tools).Append("\"><thead><tr>")
          .Append("<th>Key</th><th>Value</th><th>Reason</th><th>Tier</th><th>State</th><th>Source</th><th>Author</th>")
          .Append("<th>Shown</th><th>Used</th><th>Worked</th><th>Contradicted</th><th>Stale</th><th>Updated</th><th></th></tr></thead><tbody>");
        foreach (var h in hits)
        {
            var e = h.Entry;
            var badge = e.State switch
            {
                MemoryStates.Confirmed => "ok",
                MemoryStates.Candidate => "warn",
                MemoryStates.Dormant => "warn",
                _ => "bad"
            };
            sb.Append("<tr data-memory-id=\"").Append(E(e.Id)).Append("\">")
              .Append("<td class=\"memory-key\">").Append(E(e.Key)).Append("</td>")
              .Append("<td class=\"text\">").Append(E(e.Value)).Append("</td>")
              .Append("<td class=\"text muted\">").Append(E(e.Reason)).Append("</td>")
              .Append("<td>").Append(E(e.Scope)).Append("</td>")
              .Append("<td><span class=\"badge ").Append(badge).Append("\">").Append(E(e.State)).Append("</span>");
            if (!string.IsNullOrEmpty(e.Lifecycle)) sb.Append("<br><span class=\"muted\">").Append(E(e.Lifecycle!)).Append("</span>");
            foreach (var f in e.Flags) sb.Append(" <span class=\"badge ").Append(f == MemoryPolicy.FlagFlipFlop ? "bad" : "").Append("\">").Append(E(f)).Append("</span>");
            sb.Append("</td><td>").Append(E(e.Source)).Append("</td>")
              .Append("<td>").Append(E(string.Join(", ", e.Authors.Count > 0 ? e.Authors : new List<string> { e.Author }))).Append("</td>")
              .Append(Num(e.Counters.Shown)).Append(Num(e.Counters.Used)).Append(Num(e.Counters.Worked)).Append(Num(e.Counters.Contradicted))
              .Append("<td>").Append(h.Stale ? "<span class=\"badge warn\">stale</span>" : string.Empty).Append("</td>")
              .Append("<td data-sort=\"").Append(e.Updated.ToString("o", CultureInfo.InvariantCulture)).Append("\">")
              .Append(E(e.Updated.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append("</td><td class=\"actions\">");
            if (e.Scope != MemoryScopes.Company && !e.IsRetired)
            {
                if (e.State != MemoryStates.Confirmed || e.Flags.Contains(MemoryPolicy.FlagFlipFlop))
                    sb.Append(Button(MemoryReviewActions.Confirm, "Confirm", e.Id));
                sb.Append(Button(MemoryReviewActions.Retire, "Retire", e.Id, ghost: true));
                if (e.Scope == MemoryScopes.Project && !e.Flags.Contains(MemoryService.FlagPromoted))
                    sb.Append(Button(MemoryReviewActions.Promote, "Promote", e.Id, ghost: true));
            }
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static string Num(int n) => "<td class=\"num\">" + n.ToString(CultureInfo.InvariantCulture) + "</td>";

    private static string Button(string action, string label, string? id = null, string? proposal = null, bool ghost = false)
    {
        var sb = new StringBuilder("<button type=\"button\"");
        if (ghost) sb.Append(" class=\"ghost\"");
        sb.Append(" data-memory-action=\"").Append(action).Append('"');
        if (id != null) sb.Append(" data-memory-id=\"").Append(E(id)).Append('"');
        if (proposal != null) sb.Append(" data-memory-proposal=\"").Append(E(proposal)).Append('"');
        return sb.Append('>').Append(E(label)).Append("</button> ").ToString();
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    // Clicks go through revit.memory (pages.js): approval in Revit, then the page is republished and reloaded.
    private const string Script =
        "<script>document.addEventListener('click',function(ev){" +
        "var b=ev.target&&ev.target.closest&&ev.target.closest('[data-memory-action]');if(!b||!window.revit||!revit.memory)return;" +
        "ev.preventDefault();var a=b.getAttribute('data-memory-action');" +
        "if(a==='retire'&&!confirm('Retire this entry? It stays as history.'))return;" +
        "if(a==='promote'&&!confirm('Propose this entry for company memory? An administrator applies it.'))return;" +
        "b.disabled=true;var id=b.getAttribute('data-memory-id'),p=b.getAttribute('data-memory-proposal');" +
        "revit.memory(a,{ids:id?[id]:[],proposal:p||null}).then(function(r){b.disabled=false;if(r&&r.ok)setTimeout(function(){location.reload();},600);});" +
        "});</script>";
}
