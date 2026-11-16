namespace RevitMCP.Addin.Placement;

/// <summary>Per-element outcome of a pin or flip plan (#162).</summary>
public static class OrientationStatus
{
    public const string Ready = "ready";
    public const string NoOp = "noOp";
    public const string NotFound = "notFound";
    public const string NotAnInstance = "notAnInstance";
    public const string Grouped = "grouped";
    public const string OwnedByOther = "ownedByOther";
    public const string Pinned = "pinned";
    public const string NotFamilyInstance = "notFamilyInstance";
    public const string CannotFlip = "cannotFlip";
    public const string Failed = "failed";
}

/// <summary>What the Revit-facing code read about one element. No Revit types, so the decisions below are unit tested.</summary>
public sealed class OrientationFacts
{
    public long ElementId { get; set; }
    public bool Found { get; set; } = true;
    public bool IsType { get; set; }
    public bool Pinned { get; set; }
    /// <summary>Id of the group the element belongs to, or null.</summary>
    public long? GroupId { get; set; }
    public string? OwnedByOther { get; set; }
    public bool IsFamilyInstance { get; set; }
    public bool CanFlipFacing { get; set; }
    public bool CanFlipHand { get; set; }
}

public sealed class OrientationDecision
{
    public string Status { get; set; } = OrientationStatus.Ready;
    public string? Reason { get; set; }
    public bool NeedsUnpin { get; set; }
    public bool Ready => Status == OrientationStatus.Ready;
}

/// <summary>Pure decisions for revit_set_pin_state and revit_flip_family_instances (#162).</summary>
public static class ElementOrientationMath
{
    public const int DefaultSampleSize = 20;
    public const int MaxSampleSize = 200;

    public static int ClampSample(int requested) =>
        requested <= 0 ? DefaultSampleSize : Math.Min(requested, MaxSampleSize);

    /// <summary>Null when the request is usable; otherwise the message.</summary>
    public static string? ValidateFlip(bool facing, bool hand) =>
        facing || hand ? null : "Pass flipFacing=true, flipHand=true, or both.";

    public static string DescribeFlip(bool facing, bool hand) =>
        facing && hand ? "facing and hand" : facing ? "facing" : "hand";

    private static OrientationDecision Blocked(string status, string reason) => new() { Status = status, Reason = reason };

    /// <summary>Shared checks: missing, type, grouped, checked out by someone else.</summary>
    private static OrientationDecision? CommonBlock(OrientationFacts f, string verb)
    {
        if (!f.Found) return Blocked(OrientationStatus.NotFound, "No element with this id in the target document (linked-model elements cannot be edited from the host).");
        if (f.IsType) return Blocked(OrientationStatus.NotAnInstance, "A type, not a placed instance.");
        if (f.GroupId != null)
            return Blocked(OrientationStatus.Grouped, $"Member of group {f.GroupId}; Revit does not {verb} a member on its own. Pass the group id, or edit the group.");
        if (!string.IsNullOrEmpty(f.OwnedByOther))
            return Blocked(OrientationStatus.OwnedByOther, $"Checked out by '{f.OwnedByOther}'. Ask them to relinquish it, then retry.");
        return null;
    }

    public static OrientationDecision DecidePin(OrientationFacts f, bool wantPinned)
    {
        var block = CommonBlock(f, wantPinned ? "pin" : "unpin");
        if (block != null) return block;
        if (f.Pinned == wantPinned)
            return Blocked(OrientationStatus.NoOp, wantPinned ? "Already pinned." : "Already unpinned.");
        return new OrientationDecision();
    }

    public static OrientationDecision DecideFlip(OrientationFacts f, bool facing, bool hand, bool unpinToFlip)
    {
        var block = CommonBlock(f, "flip");
        if (block != null) return block;
        if (!f.IsFamilyInstance)
            return Blocked(OrientationStatus.NotFamilyInstance, "Not a family instance; only family instances have a facing and hand orientation.");

        var missing = new List<string>();
        if (facing && !f.CanFlipFacing) missing.Add("facing");
        if (hand && !f.CanFlipHand) missing.Add("hand");
        if (missing.Count > 0)
            return Blocked(OrientationStatus.CannotFlip,
                $"The family does not support flipping {string.Join(" and ", missing)} for this placement.");

        if (f.Pinned)
        {
            if (!unpinToFlip)
                return Blocked(OrientationStatus.Pinned, "Pinned. Revit refuses to flip a pinned element; pass unpinToFlip=true to unpin, flip and re-pin it, or unpin it first.");
            return new OrientationDecision { NeedsUnpin = true, Reason = "Pinned: will be unpinned, flipped and pinned again." };
        }
        return new OrientationDecision();
    }

    /// <summary>Counts per status, in a stable order.</summary>
    public static Dictionary<string, int> CountByStatus(IEnumerable<string> statuses)
    {
        var counts = new Dictionary<string, int>();
        foreach (var s in statuses)
            counts[s] = counts.TryGetValue(s, out var n) ? n + 1 : 1;
        return counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>Counts as a compact line, e.g. "3 of 5 element(s) would be pinned; 1 noOp, 1 grouped."</summary>
    public static string SummaryLine(string verb, int total, IReadOnlyDictionary<string, int> counts)
    {
        counts.TryGetValue(OrientationStatus.Ready, out var ready);
        var others = counts.Where(kv => kv.Key != OrientationStatus.Ready && kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}").ToList();
        return $"{ready} of {total} element(s) {verb}" + (others.Count > 0 ? $"; {string.Join(", ", others)}." : ".");
    }

    /// <summary>True when an element moved by more than the tolerance (millimetres) during a flip.</summary>
    public static bool MovedBeyond(double distanceMm, double toleranceMm = 0.5) => distanceMm > toleranceMm;
}
