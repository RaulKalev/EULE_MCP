using System.Globalization;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.ModelState;

/// <summary>One figure of an audit section (a count, a share or a size) with its badge when a threshold covers it.</summary>
public sealed class AuditCount
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Value { get; set; }
    public string? Unit { get; set; }
    /// <summary>ok | warn | bad, or null when no threshold covers the figure.</summary>
    public string? Status { get; set; }
    /// <summary>The threshold the value went above (for messages and issues).</summary>
    public double? Threshold { get; set; }

    public string ValueText => AuditFormat.Number(Value) + (string.IsNullOrEmpty(Unit) ? string.Empty : " " + Unit);
}

/// <summary>A row of an audit list; <see cref="ElementId"/> is set when the row is a model element (it selects it).</summary>
public sealed class AuditRow
{
    public long? ElementId { get; set; }
    public List<string> Cells { get; set; } = new();
}

/// <summary>One list of an audit section, rendered as one plain table.</summary>
public sealed class AuditTable
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>Column keys used in the JSON items.</summary>
    public List<string> Columns { get; set; } = new();
    /// <summary>Column headers shown on the page (Estonian).</summary>
    public List<string> Headers { get; set; } = new();
    public List<AuditRow> Rows { get; set; } = new();
    /// <summary>Rows before the collector's cap; more than <see cref="Rows"/> when the list was cut.</summary>
    public int Total { get; set; }

    public AuditRow Add(long? elementId, params object?[] cells)
    {
        var row = new AuditRow { ElementId = elementId, Cells = cells.Select(AuditFormat.Cell).ToList() };
        Rows.Add(row);
        return row;
    }
}

/// <summary>One section of the audit (warnings, views, rooms, ...).</summary>
public sealed class AuditSection
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = AuditStatus.Ok;
    public List<AuditCount> Counts { get; set; } = new();
    public List<AuditTable> Tables { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    /// <summary>Some figures come from a sample or a capped scan (the notes say which).</summary>
    public bool Sampled { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }

    public AuditCount Count(string key, string label, double value, string? unit = null)
    {
        var count = new AuditCount { Key = key, Label = label, Value = value, Unit = unit };
        Counts.Add(count);
        return count;
    }

    public AuditTable Table(string key, string title, params (string Key, string Header)[] columns)
    {
        var table = new AuditTable
        {
            Key = key,
            Title = title,
            Columns = columns.Select(c => c.Key).ToList(),
            Headers = columns.Select(c => c.Header).ToList()
        };
        Tables.Add(table);
        return table;
    }

    /// <summary>Badges every figure a rule covers and sets the section status (an error or skip wins).</summary>
    public void Finish(AuditRules rules)
    {
        foreach (var count in Counts)
        {
            count.Status = rules.Evaluate(count.Key, count.Value);
            var rule = rules.Get(count.Key);
            count.Threshold = count.Status == AuditStatus.Bad ? rule?.Bad : count.Status == AuditStatus.Warn ? rule?.Warn : null;
        }
        if (Error != null) Status = AuditStatus.Error;
        else if (Status != AuditStatus.Skipped) Status = AuditStatus.Worst(Counts.Select(c => c.Status));
    }
}

/// <summary>The whole audit: what was read, when, and the sections in page order.</summary>
public sealed class AuditReport
{
    public string DocumentTitle { get; set; } = string.Empty;
    public string? CentralPath { get; set; }
    public string? LocalPath { get; set; }
    public string RevitVersion { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public long DurationMs { get; set; }
    public List<AuditSection> Sections { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>Where the thresholds came from (defaults, then each config scope that set audit.thresholds).</summary>
    public List<string> ThresholdSources { get; set; } = new();

    public string Status => AuditStatus.Worst(Sections.Select(s => s.Status == AuditStatus.Skipped ? null : s.Status));
}

/// <summary>Section names, titles and order (#109).</summary>
public static class AuditSections
{
    public const string Summary = "summary";
    public const string Warnings = "warnings";
    public const string Types = "types";
    public const string Views = "views";
    public const string Sheets = "sheets";
    public const string Rooms = "rooms";
    public const string Imports = "imports";
    public const string Families = "families";
    public const string Groups = "groups";
    public const string Phases = "phases";
    public const string DesignOptions = "designOptions";
    public const string Worksets = "worksets";
    public const string Links = "links";
    public const string Graph = "graph";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Summary, Warnings, Types, Views, Sheets, Rooms, Imports, Families, Groups, Phases, DesignOptions, Worksets, Links, Graph
    };

    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        [Summary] = "Mudel",
        [Warnings] = "Hoiatused",
        [Types] = "Kasutamata tüübid",
        [Views] = "Vaated",
        [Sheets] = "Lehed",
        [Rooms] = "Ruumid",
        [Imports] = "Impordid ja CAD",
        [Families] = "Kohapealsed perekonnad",
        [Groups] = "Grupid",
        [Phases] = "Faasid",
        [DesignOptions] = "Kavandivariandid",
        [Worksets] = "Töökomplektid ja omandus",
        [Links] = "Lingid",
        [Graph] = "Mudeligraaf ja agendi olek"
    };

    public static string TitleOf(string name) => Titles.TryGetValue(name, out var t) ? t : name;

    /// <summary>The canonical names of the requested sections in page order; every section when none is given.</summary>
    public static List<string> Resolve(IEnumerable<string>? requested, out List<string> unknown)
    {
        unknown = new List<string>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested ?? Array.Empty<string>())
        {
            var name = raw?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;
            var match = All.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(s, name.Replace("_", string.Empty).Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase));
            if (match == null) unknown.Add(name);
            else wanted.Add(match);
        }
        return wanted.Count == 0 ? All.ToList() : All.Where(wanted.Contains).ToList();
    }
}

/// <summary>Culture-invariant formatting of audit values.</summary>
public static class AuditFormat
{
    public static string Number(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);

    public static string Cell(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "jah" : "ei",
        double d => Number(d),
        float f => Number(f),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}

/// <summary>The JSON result of revit_audit_model: it mirrors the page (sections, figures, first items of each list).</summary>
public static class AuditJson
{
    public const int MaxItemsPerSection = 200;

    /// <summary>Items per section in the JSON by default: the page lists every row, so the agent needs only a sample.</summary>
    public const int DefaultItemsPerSection = 20;

    /// <summary>itemsPerSection clamped to 0..<see cref="MaxItemsPerSection"/>.</summary>
    public static int ClampItems(int itemsPerSection) => Math.Max(0, Math.Min(MaxItemsPerSection, itemsPerSection));

    public static object Describe(AuditReport report, string? pageUrl, string? pageId, int itemsPerSection = MaxItemsPerSection) => new
    {
        document = report.DocumentTitle,
        status = report.Status,
        generatedAt = report.GeneratedAt.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture),
        durationMs = report.DurationMs,
        pageUrl,
        pageId,
        thresholds = report.ThresholdSources,
        itemsPerSection = ClampItems(itemsPerSection),
        sections = report.Sections.Select(section => DescribeSection(section, itemsPerSection)).ToList()
    };

    public static object DescribeSection(AuditSection section) => DescribeSection(section, MaxItemsPerSection);

    public static object DescribeSection(AuditSection section, int itemsPerSection)
    {
        var maxItems = ClampItems(itemsPerSection);
        var items = new List<Dictionary<string, object?>>();
        var total = 0;
        foreach (var table in section.Tables)
        {
            total += Math.Max(table.Total, table.Rows.Count);
            foreach (var row in table.Rows)
            {
                if (items.Count >= maxItems) break;
                var item = new Dictionary<string, object?> { ["list"] = table.Key };
                if (row.ElementId != null) item["id"] = row.ElementId;
                for (var i = 0; i < table.Columns.Count && i < row.Cells.Count; i++)
                    item[table.Columns[i]] = row.Cells[i];
                items.Add(item);
            }
        }
        return new
        {
            name = section.Name,
            title = section.Title,
            status = section.Status,
            sampled = section.Sampled ? true : (bool?)null,
            error = section.Error,
            durationMs = section.DurationMs,
            counts = section.Counts.ToDictionary(c => c.Key, c => (object)c.Value),
            flagged = section.Counts.Where(c => c.Status is AuditStatus.Warn or AuditStatus.Bad)
                .Select(c => new { key = c.Key, label = c.Label, value = c.Value, status = c.Status, above = c.Threshold })
                .ToList(),
            notes = section.Notes.Count > 0 ? section.Notes : null,
            items,
            itemsTotal = total,
            itemsTruncated = total > items.Count ? true : (bool?)null
        };
    }
}

/// <summary>Audit findings as issues for skill reports (audit.model task, #109): one per warn/bad figure, one per failed section.</summary>
public static class AuditIssues
{
    public const string CategoryPrefix = "ModelAudit.";

    public static List<IssueDto> Build(AuditReport report, string runId, string sourceTool, DateTimeOffset now)
    {
        var issues = new List<IssueDto>();
        foreach (var section in report.Sections)
        {
            if (section.Error != null)
            {
                issues.Add(New(runId, sourceTool, now, issues.Count, IssueSeverity.Warning, section,
                    $"{section.Title}: kontroll ebaõnnestus", section.Error));
                continue;
            }
            foreach (var count in section.Counts.Where(c => c.Status is AuditStatus.Warn or AuditStatus.Bad))
            {
                var severity = count.Status == AuditStatus.Bad ? IssueSeverity.Error : IssueSeverity.Warning;
                var above = count.Threshold != null ? $" (lävi {AuditFormat.Number(count.Threshold.Value)})" : string.Empty;
                var description = $"{count.Label}: {count.ValueText}{above}." + (section.Sampled ? " Osa näitajaid on valimist." : string.Empty);
                issues.Add(New(runId, sourceTool, now, issues.Count, severity, section, $"{section.Title}: {count.Label} {count.ValueText}", description));
            }
        }
        return issues;
    }

    private static IssueDto New(string runId, string sourceTool, DateTimeOffset now, int index, IssueSeverity severity,
        AuditSection section, string title, string description) => new()
    {
        IssueId = $"{runId}-A{(index + 1).ToString("D3", CultureInfo.InvariantCulture)}",
        RunId = runId,
        SourceTool = sourceTool,
        Severity = severity,
        Status = IssueStatus.Open,
        Category = CategoryPrefix + section.Name,
        Title = title,
        Description = description,
        CreatedAt = now
    };
}
