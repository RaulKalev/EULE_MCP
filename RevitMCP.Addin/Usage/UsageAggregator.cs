using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Usage;

/// <summary>What became of one logged call.</summary>
public static class UsageOutcomes
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string Expired = "expired";

    /// <summary>The placeholder answer of an approval-gated call; the decision is logged separately.</summary>
    public const string Requested = "approval requested";

    public static string Of(bool success, string? status) => status switch
    {
        "approval_required" => Requested,
        "approval_rejected" => Rejected,
        "approval_expired" => Expired,
        _ => success ? Success : Failed
    };
}

/// <summary>One log line, reduced to what the report uses.</summary>
public sealed class UsageRecord
{
    public DateTimeOffset Time { get; set; }
    public string User { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string Tool { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public bool IsWrite { get; set; }
    public bool Approved { get; set; }
    public string Outcome { get; set; } = UsageOutcomes.Success;
    public long DurationMs { get; set; }
    public long ResponseBytes { get; set; }
    public long? Affected { get; set; }
    public bool Truncated { get; set; }
    public bool Summarised { get; set; }

    /// <summary>Assigned by the aggregator: user, client and start of the session the call belongs to.</summary>
    public string Session { get; set; } = string.Empty;
    public double SavedSeconds { get; set; }
}

/// <summary>The metrics of one group (a user, a project, a tool, …).</summary>
public sealed class UsageGroup
{
    public string Key { get; set; } = string.Empty;
    public int Calls { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public int Writes { get; set; }
    public int Approved { get; set; }
    public int Rejected { get; set; }
    public int Expired { get; set; }

    /// <summary>Approval-gated calls that were asked for (each is followed by its decision).</summary>
    public int ApprovalsRequested { get; set; }
    public long Affected { get; set; }
    public int ActiveDays { get; set; }
    public int Sessions { get; set; }
    public DateTimeOffset? First { get; set; }
    public DateTimeOffset? Last { get; set; }
    public long ResponseBytes { get; set; }
    public int Truncated { get; set; }
    public int Summarised { get; set; }
    public double SavedSeconds { get; set; }

    /// <summary>Successes / (successes + failures); rejected and expired requests are decisions, not failures.</summary>
    public double? SuccessRate => Successes + Failures == 0 ? null : (double)Successes / (Successes + Failures);
}

public sealed class UsageReportData
{
    public string Month { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public int Files { get; set; }
    public int Lines { get; set; }
    public int Unreadable { get; set; }
    public UsageGroup Total { get; set; } = new() { Key = "All" };
    public List<UsageGroup> ByUser { get; set; } = new();
    public List<UsageGroup> ByProject { get; set; } = new();
    public List<UsageGroup> ByClient { get; set; } = new();
    public List<UsageGroup> ByTool { get; set; } = new();
    public List<UsageGroup> ByPermission { get; set; } = new();
    public List<UsageGroup> ByOutcome { get; set; } = new();
    public Dictionary<string, TimeSavedBaseline> Baselines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public UsageCosts Costs { get; set; } = new();
}

/// <summary>
/// Deterministic aggregation over the shared usage files of one month (docs/usage-reporting.md, "Monthly report"):
/// groupings by user, project, client, tool, permission and outcome with calls, success rate, writes and approval
/// outcomes, elements affected, active days, sessions, first/last use, response volume, truncated reads and the
/// time-saved estimate. No agent and no credits. Pure — no Revit API.
/// </summary>
public static class UsageAggregator
{
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);

    /// <summary>The <c>.jsonl</c> files of <paramref name="month"/> (yyyy-MM) under every user folder.</summary>
    public static List<string> FilesOf(string sharedFolder, string month)
    {
        var files = new List<string>();
        if (!Directory.Exists(sharedFolder)) return files;
        foreach (var userDir in Directory.GetDirectories(sharedFolder))
        {
            var name = Path.GetFileName(userDir);
            if (name.Equals("reports", StringComparison.OrdinalIgnoreCase) || name.Equals("costs", StringComparison.OrdinalIgnoreCase)) continue;
            files.AddRange(Directory.GetFiles(userDir, month + "-*" + UsageShare.Extension));
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>Parses log lines; unreadable lines are counted in <paramref name="unreadable"/> and skipped.</summary>
    public static List<UsageRecord> Parse(IEnumerable<string> lines, out int unreadable)
    {
        unreadable = 0;
        var records = new List<UsageRecord>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject o;
            try { o = JObject.Load(new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None }); }
            catch (JsonException) { unreadable++; continue; }

            var tool = (string?)o["tool"] ?? string.Empty;
            if (tool.Length == 0 || tool == "system") continue;
            var status = (string?)o["status"] ?? string.Empty;
            var detail = (string?)o["approvalStatus"] ?? string.Empty;
            var errorStatus = ErrorStatus(o);
            var success = status.Equals("Success", StringComparison.OrdinalIgnoreCase);
            var outcome = UsageOutcomes.Of(success, DetailStatus(detail) ?? errorStatus);
            var model = (string?)o["model"];
            records.Add(new UsageRecord
            {
                Time = ParseTime((string?)o["timestamp"]),
                User = (string?)o["user"] ?? string.Empty,
                Client = (string?)o["client"] ?? string.Empty,
                Tool = tool,
                Project = (string?)o["projectId"] ?? UsageProjectId.FromModelName(model) ?? "(no model)",
                IsWrite = !string.Equals((string?)o["permission"], "ReadOnly", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty((string?)o["permission"]),
                Approved = detail.Equals("Approved", StringComparison.OrdinalIgnoreCase),
                Outcome = outcome,
                DurationMs = (long?)o["durationMs"] ?? 0,
                ResponseBytes = (long?)o["responseSizeBytes"] ?? 0,
                Affected = (long?)o["affectedCount"],
                Truncated = (bool?)o["querySafety"]?["truncated"] ?? false,
                Summarised = (bool?)o["querySafety"]?["summaryMode"] ?? false
            });
        }
        return records;
    }

    /// <summary>The approval detail the logger writes (Pending, Approved, Rejected, Expired) as a result status.</summary>
    private static string? DetailStatus(string detail) => detail switch
    {
        "Pending" => "approval_required",
        "Rejected" => "approval_rejected",
        "Expired" => "approval_expired",
        _ => null
    };

    /// <summary>Older lines carry the approval status only in the message; read the well-known codes from it.</summary>
    private static string? ErrorStatus(JObject o)
    {
        var message = (string?)o["message"] ?? string.Empty;
        if (message.IndexOf("is pending approval in Revit", StringComparison.Ordinal) >= 0) return "approval_required";
        if (message.StartsWith("Action rejected by user", StringComparison.Ordinal)) return "approval_rejected";
        if (message.StartsWith("Approval expired", StringComparison.Ordinal)) return "approval_expired";
        return null;
    }

    private static DateTimeOffset ParseTime(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;

    public static UsageReportData Aggregate(string month, IReadOnlyList<UsageRecord> records, Dictionary<string, TimeSavedBaseline> baselines, UsageCosts costs, DateTimeOffset now)
    {
        AssignSessions(records);
        var estimator = new TimeSavedEstimator(baselines);
        foreach (var r in records) r.SavedSeconds = estimator.Estimate(r);

        return new UsageReportData
        {
            Month = month,
            GeneratedAt = now,
            Total = Build("All", records),
            ByUser = Group(records, r => r.User),
            ByProject = Group(records, r => r.Project),
            ByClient = Group(records, r => r.Client.Length == 0 ? "(unknown)" : r.Client),
            ByTool = Group(records, r => r.Tool),
            ByPermission = Group(records, r => r.IsWrite ? "write" : "read"),
            ByOutcome = Group(records, r => r.Outcome),
            Baselines = baselines,
            Costs = costs
        };
    }

    /// <summary>A session is a run of calls by one user and client with no gap longer than 30 minutes.</summary>
    public static void AssignSessions(IReadOnlyList<UsageRecord> records)
    {
        foreach (var run in records.GroupBy(r => r.User + "|" + r.Client))
        {
            DateTimeOffset? last = null;
            var session = string.Empty;
            foreach (var r in run.OrderBy(r => r.Time))
            {
                if (last == null || r.Time - last.Value > SessionGap)
                    session = run.Key + "|" + r.Time.ToString("o", CultureInfo.InvariantCulture);
                r.Session = session;
                last = r.Time;
            }
        }
    }

    private static List<UsageGroup> Group(IReadOnlyList<UsageRecord> records, Func<UsageRecord, string> key) =>
        records.GroupBy(r => string.IsNullOrWhiteSpace(key(r)) ? "(unknown)" : key(r), StringComparer.OrdinalIgnoreCase)
            .Select(g => Build(g.Key, g.ToList()))
            .OrderByDescending(g => g.Calls)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static UsageGroup Build(string key, IReadOnlyList<UsageRecord> records)
    {
        var g = new UsageGroup { Key = key };
        foreach (var r in records)
        {
            if (r.Outcome == UsageOutcomes.Requested)
            {
                g.ApprovalsRequested++;
                continue;   // a placeholder: the execution or the decision is its own line
            }
            g.Calls++;
            switch (r.Outcome)
            {
                case UsageOutcomes.Success: g.Successes++; break;
                case UsageOutcomes.Rejected: g.Rejected++; break;
                case UsageOutcomes.Expired: g.Expired++; break;
                default: g.Failures++; break;
            }
            if (r.IsWrite) g.Writes++;
            if (r.Approved) g.Approved++;
            if (r.Outcome == UsageOutcomes.Success && r.Affected is > 0) g.Affected += r.Affected.Value;
            g.ResponseBytes += r.ResponseBytes;
            if (r.Truncated) g.Truncated++;
            if (r.Summarised) g.Summarised++;
            g.SavedSeconds += r.SavedSeconds;
            if (g.First == null || r.Time < g.First) g.First = r.Time;
            if (g.Last == null || r.Time > g.Last) g.Last = r.Time;
        }
        var counted = records.Where(r => r.Outcome != UsageOutcomes.Requested).ToList();
        g.ActiveDays = counted.Select(r => r.Time.ToLocalTime().Date).Distinct().Count();
        g.Sessions = counted.Select(r => r.Session).Where(s => s.Length > 0).Distinct().Count();
        return g;
    }

    /// <summary>Reads, parses and aggregates one month of the shared folder.</summary>
    public static UsageReportData Run(string sharedFolder, string month, UsageSettings settings, DateTimeOffset now)
    {
        var files = FilesOf(sharedFolder, month);
        var lines = new List<string>();
        foreach (var f in files)
        {
            try { lines.AddRange(File.ReadAllLines(f, Encoding.UTF8)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var records = Parse(lines, out var unreadable);
        var costs = UsageCosts.Load(settings.CostExportFolder, month);
        var data = Aggregate(month, records, settings.Baselines, costs, now);
        data.Files = files.Count;
        data.Lines = records.Count;
        data.Unreadable = unreadable;
        return data;
    }
}

/// <summary>
/// The time-saved estimate (docs/usage-reporting.md, "Theoretical time saved"): per successful call,
/// <c>baseline × affectedCount (or 1) − duration</c>, floored at zero. Only tools in the baseline table count. It is an
/// estimate and every report says so next to the baseline table. Pure.
/// </summary>
public sealed class TimeSavedEstimator
{
    private readonly Dictionary<string, TimeSavedBaseline> _baselines;

    public TimeSavedEstimator(Dictionary<string, TimeSavedBaseline> baselines) => _baselines = baselines;

    public double Estimate(UsageRecord r)
    {
        if (r.Outcome != UsageOutcomes.Success || !_baselines.TryGetValue(r.Tool, out var b)) return 0;
        var manual = b.SecondsPerElement != null
            ? b.SecondsPerElement.Value * (r.Affected is > 0 ? r.Affected.Value : 1)
            : b.SecondsPerCall ?? 0;
        return Math.Max(0, manual - r.DurationMs / 1000.0);
    }
}

/// <summary>One row of a provider cost export.</summary>
public sealed class UsageCostRow
{
    public string User { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>
/// The provider spend for a month (docs/usage-reporting.md, "Credit costs"): <c>&lt;costExportFolder&gt;\&lt;yyyy-MM&gt;.csv</c>
/// with the columns user, client, cost, inputTokens, outputTokens (comma or semicolon separated, header row, any column
/// order). Joined to activity by user; a user written as an e-mail address matches the part before the @. Pure.
/// </summary>
public sealed class UsageCosts
{
    public string? Source { get; set; }
    public List<UsageCostRow> Rows { get; set; } = new();
    public List<string> Problems { get; set; } = new();
    public bool Present => Rows.Count > 0;

    public static UsageCosts Load(string? folder, string month)
    {
        var costs = new UsageCosts();
        if (string.IsNullOrWhiteSpace(folder)) return costs;
        var path = Path.Combine(folder!, month + ".csv");
        try
        {
            if (!File.Exists(path)) return costs;
            costs.Source = path;
            costs.Rows = Parse(File.ReadAllLines(path, Encoding.UTF8), costs.Problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            costs.Problems.Add("The cost export could not be read: " + ex.Message);
        }
        return costs;
    }

    public static List<UsageCostRow> Parse(IReadOnlyList<string> lines, List<string> problems)
    {
        var rows = new List<UsageCostRow>();
        if (lines.Count == 0) return rows;
        var separator = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var header = lines[0].Split(separator).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name.ToLowerInvariant());
        int user = Col("user"), client = Col("client"), cost = Col("cost"), input = Col("inputTokens"), output = Col("outputTokens");
        if (user < 0 || cost < 0)
        {
            problems.Add("The cost export needs at least the columns user and cost.");
            return rows;
        }
        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = lines[i].Split(separator).Select(c => c.Trim().Trim('"')).ToList();
            string Cell(int index) => index >= 0 && index < cells.Count ? cells[index] : string.Empty;
            if (!decimal.TryParse(Cell(cost).Replace(',', '.'), NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out var amount))
            {
                problems.Add($"Line {i + 1}: cost '{Cell(cost)}' is not a number.");
                continue;
            }
            long.TryParse(Cell(input), NumberStyles.Integer, CultureInfo.InvariantCulture, out var inTok);
            long.TryParse(Cell(output), NumberStyles.Integer, CultureInfo.InvariantCulture, out var outTok);
            rows.Add(new UsageCostRow { User = Cell(user), Client = Cell(client), Cost = amount, InputTokens = inTok, OutputTokens = outTok });
        }
        return rows;
    }

    /// <summary>The rows of one connector user: same name, or the export's e-mail address with that name before the @.</summary>
    public List<UsageCostRow> For(string user) =>
        Rows.Where(r => Same(r.User, user)).ToList();

    public static bool Same(string exportUser, string user)
    {
        var a = exportUser.Trim();
        var at = a.IndexOf('@');
        if (at > 0) a = a.Substring(0, at);
        return string.Equals(a, user.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
