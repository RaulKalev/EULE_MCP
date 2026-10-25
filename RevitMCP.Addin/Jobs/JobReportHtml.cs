using System.Globalization;
using System.Text;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Plans;

namespace RevitMCP.Addin.Jobs;

/// <summary>
/// The handoff page of a job (#100): what the connector did while the user was away, as a model page
/// (docs/model-pages.md) — plain semantic HTML, one plain table per element list with <c>data-revit-id</c> rows and
/// <c>data-revit-param</c> cells for changed parameters (live "now" values, drift highlighted). Deterministic, no
/// external URLs, Estonian page language like the team's other pages. Pure.
/// </summary>
public static class JobReportHtml
{
    public const int MaxRows = 2000;

    public static string Build(
        Job job,
        Checkpoint? checkpoint,
        CheckpointDiff? diff,
        WarningDiffResult? warnings,
        Plan? plan,
        DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"et\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
          .Append(E("Töö: " + job.Title)).Append("</title>\n</head>\n<body>\n");

        sb.Append("<h1>").Append(E(job.Title)).Append("</h1>\n<p>")
          .Append(E($"{job.Id} · {StateText(job.State)} · {job.DocumentTitle} · {job.ClientName} · {now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}"))
          .Append("</p>\n");
        if (!string.IsNullOrEmpty(job.StopReason))
            sb.Append("<p><strong>Peatumise põhjus:</strong> ").Append(E(job.StopReason!)).Append("</p>\n");

        Cards(sb, job, diff, warnings, plan);
        Steps(sb, job);
        if (diff != null && checkpoint != null)
        {
            Changed(sb, diff);
            Elements(sb, "Lisatud elemendid", "added", diff.Of(NetChangeKind.Added).ToList());
            Elements(sb, "Kustutatud elemendid", "deleted", diff.Of(NetChangeKind.Deleted).ToList(), readOnly: true);
            if (!checkpoint.TrackingComplete)
                sb.Append("<p>").Append(E(checkpoint.TrackingNote ?? CheckpointStore.RestartNote)).Append("</p>\n");
        }
        else
        {
            sb.Append("<p>Kontrollpunkti ei leitud: muudatuste loetelu puudub.</p>\n");
        }
        if (warnings != null) Warnings(sb, warnings);
        Undo(sb, job, checkpoint);

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void Cards(StringBuilder sb, Job job, CheckpointDiff? diff, WarningDiffResult? warnings, Plan? plan)
    {
        var ok = job.Steps.Count(s => s.State == JobStepState.Succeeded);
        var failed = job.Steps.Count(s => s.State == JobStepState.Failed);
        var skipped = job.Steps.Count(s => s.State == JobStepState.Skipped);
        sb.Append("<section>\n<h2>Kokkuvõte</h2>\n<ul>\n");
        Li(sb, $"Sammud: {ok} õnnestus, {failed} ebaõnnestus, {skipped} vahele jäetud, kokku {job.Steps.Count}");
        if (diff != null)
            Li(sb, $"Elemendid: {diff.Count(NetChangeKind.Added)} lisatud, {ChangedEntries(diff).Count} muudetud, {diff.Count(NetChangeKind.Deleted)} kustutatud");
        if (warnings != null)
            Li(sb, $"Uued Revit hoiatused: {warnings.New.Count} ({warnings.New.Count(WarningDiff.IsErrorSeverity)} viga), lahendatud {warnings.Resolved.Count}");
        Li(sb, $"Kestus: {Math.Round(job.ElapsedMs / 60000.0, 1).ToString(CultureInfo.InvariantCulture)} min");
        if (plan != null)
            Li(sb, $"Plaan {plan.Id}: {plan.Calls}/{plan.Envelope.MaxCalls} kutset, {plan.Touched.Count}/{plan.Envelope.MaxElementsTotal} elementi, {plan.Violations.Count} katmata");
        sb.Append("</ul>\n</section>\n");
    }

    private static void Steps(StringBuilder sb, Job job)
    {
        sb.Append("<section>\n<h2>Sammud</h2>\n<table data-revit-tools=\"rows\">\n<thead><tr><th>#</th><th>Samm</th><th>Tööriist</th><th>Olek</th><th>ms</th><th>Teade</th></tr></thead>\n<tbody>\n");
        foreach (var step in job.Steps)
        {
            sb.Append("<tr><td>").Append(step.Index + 1).Append("</td><td>").Append(E(step.DisplayTitle)).Append("</td><td>").Append(E(step.Tool))
              .Append("</td><td><span class=\"").Append(Badge(step)).Append("\">").Append(E(StepText(step))).Append("</span></td><td>")
              .Append(step.DurationMs).Append("</td><td>").Append(E(Shorten(step.Message, 300))).Append("</td></tr>\n");
        }
        sb.Append("</tbody>\n</table>\n</section>\n");
    }

    /// <summary>One row per changed parameter (or move) of a modified element.</summary>
    private static void Changed(StringBuilder sb, CheckpointDiff diff)
    {
        var entries = ChangedEntries(diff);
        sb.Append("<section>\n<h2>Muudetud elemendid</h2>\n");
        if (entries.Count == 0)
        {
            sb.Append("<p>Muudetud elemente ei ole.</p>\n</section>\n");
            return;
        }
        sb.Append("<table>\n<thead><tr><th>Id</th><th>Kategooria</th><th>Nimi</th><th>Kes</th><th>Mis muutus</th><th>Enne</th><th>Nüüd</th></tr></thead>\n<tbody>\n");
        var rows = 0;
        foreach (var e in entries)
        {
            var lines = new List<(string What, string? Before, string? Now, string? Param)>();
            if (e.Delta != null)
            {
                lines.AddRange(e.Delta.Parameters.Select(p => (p.Name, p.Before, p.Now, (string?)p.Name)));
                if (e.Delta.Moved != null)
                    lines.Add(("asukoht", Mm(e.Delta.Moved.FromMm), Mm(e.Delta.Moved.ToMm) + $" ({e.Delta.Moved.DistanceMm.ToString(CultureInfo.InvariantCulture)} mm)", null));
                if (e.Delta.Rotated)
                    lines.Add(("pööre", Deg(e.Delta.RotationBeforeRad!.Value), Deg(e.Delta.RotationNowRad!.Value), null));
            }
            if (lines.Count == 0) lines.Add(("(muudetud)", null, null, null));

            foreach (var (what, before, nowValue, param) in lines)
            {
                if (rows++ >= MaxRows) break;
                sb.Append("<tr data-revit-id=\"").Append(e.Id).Append("\"><td>").Append(e.Id).Append("</td><td>").Append(E(e.Identity?.Category))
                  .Append("</td><td>").Append(E(e.Identity?.Name)).Append("</td><td>").Append(E(e.By)).Append("</td><td>").Append(E(what))
                  .Append("</td><td>").Append(E(before)).Append("</td>");
                if (param != null) sb.Append("<td data-revit-param=\"").Append(E(param)).Append("\">").Append(E(nowValue)).Append("</td>");
                else sb.Append("<td>").Append(E(nowValue)).Append("</td>");
                sb.Append("</tr>\n");
            }
            if (rows >= MaxRows) break;
        }
        sb.Append("</tbody>\n</table>\n");
        if (rows >= MaxRows) sb.Append("<p>").Append(E($"Näidatud {MaxRows} esimest rida; täielik loetelu: revit_checkpoint_diff include=elements.")).Append("</p>\n");
        sb.Append("</section>\n");
    }

    private static void Elements(StringBuilder sb, string heading, string id, List<DiffEntry> entries, bool readOnly = false)
    {
        sb.Append("<section id=\"").Append(id).Append("\">\n<h2>").Append(E(heading)).Append(" (").Append(entries.Count).Append(")</h2>\n");
        if (entries.Count == 0)
        {
            sb.Append("<p>Ei ole.</p>\n</section>\n");
            return;
        }
        sb.Append(readOnly ? "<table data-revit-readonly>\n" : "<table>\n")
          .Append("<thead><tr><th>Id</th><th>Kategooria</th><th>Nimi</th><th>Tüüp</th><th>Korrus</th><th>Kes</th></tr></thead>\n<tbody>\n");
        foreach (var e in entries.Take(MaxRows))
        {
            sb.Append("<tr data-revit-id=\"").Append(e.Id).Append("\"><td>").Append(e.Id).Append("</td><td>").Append(E(e.Identity?.Category))
              .Append("</td><td>").Append(E(e.Identity?.Name)).Append("</td><td>").Append(E(e.Identity?.TypeName)).Append("</td><td>")
              .Append(E(e.Identity?.Level)).Append("</td><td>").Append(E(e.By)).Append("</td></tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
        if (entries.Count > MaxRows) sb.Append("<p>").Append(E($"Näidatud {MaxRows} esimest {entries.Count}-st.")).Append("</p>\n");
        sb.Append("</section>\n");
    }

    private static void Warnings(StringBuilder sb, WarningDiffResult warnings)
    {
        sb.Append("<section>\n<h2>Uued hoiatused (").Append(warnings.New.Count).Append(")</h2>\n");
        if (warnings.New.Count == 0)
        {
            sb.Append("<p>Uusi Revit hoiatusi ei ole.</p>\n</section>\n");
            return;
        }
        sb.Append("<table>\n<thead><tr><th>Raskus</th><th>Hoiatus</th><th>Elemendid</th></tr></thead>\n<tbody>\n");
        foreach (var w in warnings.New.OrderBy(w => WarningDiff.SeverityRank(w.Severity)).Take(MaxRows))
        {
            sb.Append(w.ElementIds.Length > 0 ? "<tr data-revit-id=\"" + string.Join(",", w.ElementIds.Take(50)) + "\">" : "<tr>").Append("<td>").Append(E(w.Severity))
              .Append("</td><td>").Append(E(w.Description)).Append("</td><td>").Append(E(string.Join(", ", w.ElementIds.Take(50)))).Append("</td></tr>\n");
        }
        sb.Append("</tbody>\n</table>\n</section>\n");
    }

    private static void Undo(StringBuilder sb, Job job, Checkpoint? checkpoint)
    {
        sb.Append("<section>\n<h2>Tagasivõtmine</h2>\n");
        var names = checkpoint?.Batches.Where(b => b.Operation == "TransactionCommitted").SelectMany(b => b.TransactionNames).ToList() ?? new List<string>();
        if (names.Count > 0)
        {
            sb.Append("<p>Revit salvestas need tehingud (Undo menüü nimed), vanimad enne:</p>\n<ol>\n");
            foreach (var name in names.Skip(Math.Max(0, names.Count - 100))) sb.Append("<li>").Append(E(name)).Append("</li>\n");
            sb.Append("</ol>\n");
        }
        if (checkpoint != null)
            sb.Append("<p>").Append(E($"Kontrollpunkt {checkpoint.Id}. Agendi muudatuste tagasivõtmiseks: revit_preview_checkpoint_rollback checkpointId={checkpoint.Id}, seejärel revit_checkpoint_rollback (vajab käsitsi kinnitust; kustutatud elemente ei taastata).")).Append("</p>\n");
        sb.Append("</section>\n");
    }

    private static List<DiffEntry> ChangedEntries(CheckpointDiff diff)
        => diff.Of(NetChangeKind.Modified).Where(e => !CheckpointDiffReport.IsTouchedUnchanged(e)).ToList();

    private static string Badge(JobStep step) => step.State switch
    {
        JobStepState.Succeeded => "ok",
        JobStepState.Failed => step.Tolerated ? "warn" : "bad",
        JobStepState.Skipped => "warn",
        _ => "warn"
    };

    private static string StepText(JobStep step) => step.State switch
    {
        JobStepState.Succeeded => "õnnestus",
        JobStepState.Failed => step.Tolerated ? "ebaõnnestus (jätkati)" : "ebaõnnestus",
        JobStepState.Skipped => "vahele jäetud",
        JobStepState.WaitingApproval => "ootab kinnitust",
        JobStepState.Running => "töös",
        JobStepState.Cancelled => "katkestatud",
        _ => "ootel"
    };

    private static string StateText(JobState state) => state switch
    {
        JobState.Completed => "valmis",
        JobState.Failed => "ebaõnnestus",
        JobState.Cancelled => "katkestatud",
        JobState.Paused => "peatatud",
        JobState.Suspended => "ootel (taasta revit_job_resume)",
        JobState.WaitingApproval => "ootab kinnitust",
        JobState.Running => "töös",
        _ => state.ToString()
    };

    private static void Li(StringBuilder sb, string text) => sb.Append("<li>").Append(E(text)).Append("</li>\n");

    private static string Mm(double[] values) => string.Join(", ", values.Select(v => v.ToString("0", CultureInfo.InvariantCulture)));

    private static string Deg(double rad) => (rad * 180 / Math.PI).ToString("0.##", CultureInfo.InvariantCulture) + "°";

    private static string Shorten(string? text, int max) => string.IsNullOrEmpty(text) ? string.Empty : text!.Length <= max ? text : text.Substring(0, max) + "…";

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
