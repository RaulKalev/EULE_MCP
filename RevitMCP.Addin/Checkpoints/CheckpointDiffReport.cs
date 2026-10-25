namespace RevitMCP.Addin.Checkpoints;

public sealed class DiffReportOptions
{
    /// <summary>"summary" (counts, transactions, warnings), "elements" (plus element lists) or "all" (plus transaction ids).</summary>
    public string Include { get; set; } = "summary";
    public HashSet<string>? Categories { get; set; }

    /// <summary>"agent" | "user" | "page" | "all" | a tool name.</summary>
    public string? Attribution { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;

    public bool WithElements => Include is "elements" or "all";
    public bool WithAll => Include == "all";
}

/// <summary>
/// Shapes a checkpoint diff into the revit_checkpoint_diff result (#98): counts, transactions, element lists
/// (paged), warnings and what a rollback can and cannot undo. Pure — no Revit API.
/// </summary>
public static class CheckpointDiffReport
{
    public const int MaxTransactions = 50;
    public const int MaxWarningGroups = 50;

    public static Dictionary<string, object?> Build(
        Checkpoint checkpoint,
        CheckpointDiff diff,
        WarningDiffResult? warnings,
        DiffReportOptions options,
        DateTimeOffset now)
    {
        var entries = diff.Entries.Values
            .Where(e => ChangeNetting.MatchesAttribution(e.By, options.Attribution))
            .Where(e => options.Categories is not { Count: > 0 } cats ||
                        cats.Contains(e.Identity?.Category ?? string.Empty))
            .ToList();

        // A snapshotted element that was touched but is back to its checkpoint values is not a modification.
        var unchanged = entries.Where(IsTouchedUnchanged).Select(e => e.Id).ToHashSet();
        var changed = entries.Where(e => !unchanged.Contains(e.Id)).ToList();

        var added = changed.Where(e => e.Kind == NetChangeKind.Added).OrderBy(e => e.Id).ToList();
        var modified = changed.Where(e => e.Kind == NetChangeKind.Modified).OrderBy(e => e.Id).ToList();
        var deleted = changed.Where(e => e.Kind == NetChangeKind.Deleted).OrderBy(e => e.Id).ToList();

        var report = new Dictionary<string, object?>
        {
            ["checkpoint"] = Describe(checkpoint, now),
            ["counts"] = new Dictionary<string, object?>
            {
                ["added"] = added.Count,
                ["modified"] = modified.Count,
                ["deleted"] = deleted.Count,
                ["distinctTouched"] = changed.Count,
                ["touchedButUnchanged"] = unchanged.Count,
                ["byAttribution"] = changed.GroupBy(e => e.By, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                ["byCategory"] = changed.GroupBy(e => e.Identity?.Category is { Length: > 0 } c ? c : "(unknown)", StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
            },
            ["transactions"] = Transactions(checkpoint, options),
            ["transactionCount"] = checkpoint.Batches.Count
        };

        if (options.WithElements)
        {
            var pageSize = Math.Max(1, options.PageSize);
            var page = Math.Max(1, options.Page);
            report["paging"] = new Dictionary<string, object?>
            {
                ["page"] = page,
                ["pageSize"] = pageSize,
                ["hasMore"] = Math.Max(added.Count, Math.Max(modified.Count, deleted.Count)) > page * pageSize
            };
            report["added"] = PageOf(added, page, pageSize).Select(Element).ToList();
            report["modified"] = PageOf(modified, page, pageSize).Select(Modified).ToList();
            report["deleted"] = PageOf(deleted, page, pageSize).Select(Element).ToList();
        }

        if (warnings != null)
        {
            report["warnings"] = new Dictionary<string, object?>
            {
                ["new"] = warnings.New.Count,
                ["newErrors"] = warnings.New.Count(WarningDiff.IsErrorSeverity),
                ["resolved"] = warnings.Resolved.Count,
                ["unchanged"] = warnings.Unchanged,
                ["newGroups"] = WarningDiff.Group(warnings.New).Take(MaxWarningGroups).Select(GroupRow).ToList(),
                ["resolvedGroups"] = WarningDiff.Group(warnings.Resolved).Take(MaxWarningGroups).Select(GroupRow).ToList()
            };
        }

        var plan = RollbackPlanner.Plan(diff, checkpoint, new RollbackOptions());
        report["undo"] = new Dictionary<string, object?>
        {
            ["transactionNames"] = checkpoint.Batches
                .Where(b => b.Operation == "TransactionCommitted")
                .SelectMany(b => b.TransactionNames)
                .Reverse().Take(MaxTransactions).Reverse().ToList(),
            ["rollbackCanRestore"] = new Dictionary<string, object?>
            {
                ["deleteAdded"] = plan.Delete.Count,
                ["restoreValues"] = plan.Values.Count,
                ["restoreValueElements"] = plan.ValueElementCount,
                ["restoreLocations"] = plan.Locations.Count
            },
            ["rollbackCannotRestore"] = new Dictionary<string, object?>
            {
                ["deleted"] = plan.CannotRestoreDeleted,
                ["notSnapshotted"] = plan.CannotRestoreNotSnapshotted,
                ["skipped"] = plan.Skipped.Count
            }
        };
        return report;
    }

    public static Dictionary<string, object?> Describe(Checkpoint checkpoint, DateTimeOffset now) => new()
    {
        ["id"] = checkpoint.Id,
        ["title"] = checkpoint.Title,
        ["document"] = checkpoint.DocumentTitle,
        ["createdAt"] = checkpoint.CreatedAt,
        ["ageMinutes"] = Math.Round((now - checkpoint.CreatedAt).TotalMinutes, 1),
        ["createdBy"] = checkpoint.CreatedBy,
        ["planId"] = checkpoint.PlanId,
        ["snapshotElements"] = checkpoint.Snapshot.Count,
        ["trackingComplete"] = checkpoint.TrackingComplete,
        ["trackingNote"] = checkpoint.TrackingNote
    };

    public static bool IsTouchedUnchanged(DiffEntry e)
        => e.Kind == NetChangeKind.Modified && e.Snapshotted && (e.Delta == null || !e.Delta.HasChanges);

    public static object GroupRow(WarningGroup g) => new Dictionary<string, object?>
    {
        ["description"] = g.Description,
        ["severity"] = g.Severity,
        ["count"] = g.Count,
        ["elementIds"] = g.ElementIds,
        ["elementIdCount"] = g.ElementIdCount
    };

    private static List<object> Transactions(Checkpoint checkpoint, DiffReportOptions options)
    {
        var batches = checkpoint.Batches.Where(b => ChangeNetting.MatchesAttribution(b.Attribution, options.Attribution)).ToList();
        return batches.Skip(Math.Max(0, batches.Count - MaxTransactions)).Select(b =>
        {
            var row = new Dictionary<string, object?>
            {
                ["at"] = b.At,
                ["names"] = b.TransactionNames,
                ["operation"] = b.Operation,
                ["by"] = b.Attribution,
                ["added"] = b.Added.Length,
                ["modified"] = b.Modified.Length,
                ["deleted"] = b.Deleted.Length
            };
            if (options.WithAll)
            {
                row["addedIds"] = b.Added.Take(200).ToArray();
                row["modifiedIds"] = b.Modified.Take(200).ToArray();
                row["deletedIds"] = b.Deleted.Take(200).ToArray();
            }
            return (object)row;
        }).ToList();
    }

    private static IEnumerable<DiffEntry> PageOf(List<DiffEntry> list, int page, int pageSize)
        => list.Skip((page - 1) * pageSize).Take(pageSize);

    private static Dictionary<string, object?> Element(DiffEntry e)
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["category"] = e.Identity?.Category,
            ["name"] = e.Identity?.Name,
            ["type"] = e.Identity?.TypeName,
            ["level"] = e.Identity?.Level,
            ["by"] = e.By
        };
        return row;
    }

    private static Dictionary<string, object?> Modified(DiffEntry e)
    {
        var row = Element(e);
        row["snapshotted"] = e.Snapshotted;
        if (e.Delta == null) return row;
        if (e.Delta.Parameters.Count > 0)
            row["parameters"] = e.Delta.Parameters.Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name,
                ["before"] = p.Before,
                ["now"] = p.Now
            }).ToList();
        if (e.Delta.Moved != null)
            row["moved"] = new Dictionary<string, object?>
            {
                ["fromMm"] = e.Delta.Moved.FromMm,
                ["toMm"] = e.Delta.Moved.ToMm,
                ["distanceMm"] = e.Delta.Moved.DistanceMm
            };
        if (e.Delta.Rotated)
            row["rotated"] = new Dictionary<string, object?>
            {
                ["beforeDeg"] = Math.Round(e.Delta.RotationBeforeRad!.Value * 180 / Math.PI, 2),
                ["nowDeg"] = Math.Round(e.Delta.RotationNowRad!.Value * 180 / Math.PI, 2)
            };
        return row;
    }
}
