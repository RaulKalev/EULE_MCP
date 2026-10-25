namespace RevitMCP.Addin.Checkpoints;

public sealed class RollbackOptions
{
    public bool DeleteAdded { get; set; } = true;
    public bool RestoreValues { get; set; } = true;
    public bool RestoreLocations { get; set; } = true;

    /// <summary>Limit the rollback to these ids; null or empty means every changed element.</summary>
    public HashSet<long>? ElementIds { get; set; }

    /// <summary>Also roll back elements whose latest change was not made by an agent.</summary>
    public bool Force { get; set; }

    /// <summary>Parses the tool's <c>what[]</c>; empty means all three. Returns false with the unknown value.</summary>
    public static bool TryParseWhat(IEnumerable<string>? what, RollbackOptions options, out string? unknown)
    {
        unknown = null;
        var values = (what ?? Array.Empty<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        if (values.Count == 0) return true;
        options.DeleteAdded = options.RestoreValues = options.RestoreLocations = false;
        foreach (var value in values)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "deleteadded": options.DeleteAdded = true; break;
                case "restorevalues": options.RestoreValues = true; break;
                case "restorelocations": options.RestoreLocations = true; break;
                default: unknown = value; return false;
            }
        }
        return true;
    }
}

public sealed class RollbackValue
{
    public long Id { get; set; }
    public string Parameter { get; set; } = string.Empty;

    /// <summary>The value at checkpoint time; empty when the parameter had none.</summary>
    public string Value { get; set; } = string.Empty;
    public string? Current { get; set; }
}

public sealed class RollbackLocation
{
    public long Id { get; set; }

    /// <summary>Translation that brings the element back, in millimetres; null when only the rotation changed.</summary>
    public double[]? TranslateMm { get; set; }

    /// <summary>The point to rotate about after the translation (the checkpoint-time location), in millimetres.</summary>
    public double[]? RotationOriginMm { get; set; }

    /// <summary>Rotation about the vertical axis that restores the checkpoint angle, in radians.</summary>
    public double? RotateRad { get; set; }
}

public sealed class RollbackSkip
{
    public long Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>What a rollback will do; computed from a diff without touching Revit.</summary>
public sealed class RollbackPlan
{
    public List<long> Delete { get; set; } = new();
    public List<RollbackValue> Values { get; set; } = new();
    public List<RollbackLocation> Locations { get; set; } = new();
    public List<RollbackSkip> Skipped { get; set; } = new();

    /// <summary>Deleted since the checkpoint: never recreated.</summary>
    public int CannotRestoreDeleted { get; set; }

    /// <summary>Modified but not in the snapshot: their previous values are unknown.</summary>
    public int CannotRestoreNotSnapshotted { get; set; }

    public int ValueElementCount => Values.Select(v => v.Id).Distinct().Count();

    public bool IsEmpty => Delete.Count == 0 && Values.Count == 0 && Locations.Count == 0;
}

/// <summary>
/// Plans a compensating rollback to a checkpoint (#98): delete what was added, restore snapshotted values and
/// locations. Deleted elements are never recreated, and an element whose latest change was not an agent's is
/// left alone unless <see cref="RollbackOptions.Force"/>. Pure — no Revit API.
/// </summary>
public static class RollbackPlanner
{
    public const double LineShapeToleranceMm = 1.0;

    public static RollbackPlan Plan(CheckpointDiff diff, Checkpoint checkpoint, RollbackOptions options)
    {
        var plan = new RollbackPlan();
        foreach (var entry in diff.Entries.Values.OrderBy(e => e.Id))
        {
            if (options.ElementIds is { Count: > 0 } only && !only.Contains(entry.Id)) continue;

            switch (entry.Kind)
            {
                case NetChangeKind.Deleted:
                    plan.CannotRestoreDeleted++;
                    plan.Skipped.Add(Skip(entry.Id, "restore", "deleted elements are not recreated"));
                    break;

                case NetChangeKind.Added:
                    if (!options.DeleteAdded) break;
                    if (Blocked(entry, options, "deleteAdded", plan)) break;
                    plan.Delete.Add(entry.Id);
                    break;

                case NetChangeKind.Modified:
                    PlanModified(entry, checkpoint, options, plan);
                    break;
            }
        }
        return plan;
    }

    private static void PlanModified(DiffEntry entry, Checkpoint checkpoint, RollbackOptions options, RollbackPlan plan)
    {
        if (!checkpoint.Snapshot.TryGetValue(entry.Id, out var before))
        {
            plan.CannotRestoreNotSnapshotted++;
            if (options.RestoreValues || options.RestoreLocations)
                plan.Skipped.Add(Skip(entry.Id, "restore", "not in the checkpoint snapshot: previous values are unknown"));
            return;
        }

        var delta = entry.Delta;
        if (delta == null || !delta.HasChanges) return;   // touched, but every snapshotted value is as it was

        var wantsValues = options.RestoreValues && delta.Parameters.Count > 0;
        var wantsLocation = options.RestoreLocations && (delta.Moved != null || delta.Rotated);
        if (!wantsValues && !wantsLocation) return;
        if (Blocked(entry, options, "restore", plan)) return;

        if (wantsValues)
        {
            foreach (var p in delta.Parameters)
                plan.Values.Add(new RollbackValue { Id = entry.Id, Parameter = p.Name, Value = p.Before ?? string.Empty, Current = p.Now });
        }

        if (wantsLocation)
        {
            var location = new RollbackLocation { Id = entry.Id };
            if (delta.Moved != null)
            {
                var translate = Translation(delta.Moved.FromMm, delta.Moved.ToMm, out var reason);
                if (translate == null)
                {
                    plan.Skipped.Add(Skip(entry.Id, "restoreLocations", reason!));
                }
                else
                {
                    location.TranslateMm = translate;
                }
            }
            if (delta.Rotated && before.LocationMm != null)
            {
                location.RotationOriginMm = before.LocationMm.ToArray();
                location.RotateRad = Math.IEEERemainder(delta.RotationBeforeRad!.Value - delta.RotationNowRad!.Value, 2 * Math.PI);
            }
            if (location.TranslateMm != null || location.RotateRad != null) plan.Locations.Add(location);
        }
    }

    /// <summary>
    /// The vector from now back to before. For a line both endpoints must have moved alike: a stretched or
    /// re-shaped line cannot be undone by a move.
    /// </summary>
    public static double[]? Translation(double[] beforeMm, double[] nowMm, out string? reason)
    {
        reason = null;
        if (beforeMm.Length != nowMm.Length || beforeMm.Length == 0 || beforeMm.Length % 3 != 0)
        {
            reason = "the location changed kind and cannot be restored by a move";
            return null;
        }
        var t = new[] { beforeMm[0] - nowMm[0], beforeMm[1] - nowMm[1], beforeMm[2] - nowMm[2] };
        for (var i = 3; i < beforeMm.Length; i += 3)
        {
            var dx = beforeMm[i] - nowMm[i] - t[0];
            var dy = beforeMm[i + 1] - nowMm[i + 1] - t[1];
            var dz = beforeMm[i + 2] - nowMm[i + 2] - t[2];
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > LineShapeToleranceMm)
            {
                reason = "the line changed length or direction; only a move can be undone";
                return null;
            }
        }
        return t;
    }

    private static bool Blocked(DiffEntry entry, RollbackOptions options, string action, RollbackPlan plan)
    {
        if (options.Force || ChangeAttribution.IsAgent(entry.By)) return false;
        var reason = entry.By == ChangeAttribution.Unknown
            ? "changed while the connector was not tracking it; pass force=true to roll it back"
            : $"last changed by {entry.By}; pass force=true to roll it back";
        plan.Skipped.Add(Skip(entry.Id, action, reason));
        return true;
    }

    private static RollbackSkip Skip(long id, string action, string reason)
        => new() { Id = id, Action = action, Reason = reason };
}
