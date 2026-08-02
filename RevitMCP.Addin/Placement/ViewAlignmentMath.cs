namespace RevitMCP.Addin.Placement;

/// <summary>
/// How far an element reaches along one of a view's two axes, measured in feet in view space.
/// A degenerate extent (Min == Max) is a point anchor — a tag head or a text insertion point —
/// for which every edge mode collapses to the same coordinate.
/// </summary>
public readonly struct Extent
{
    public Extent(double min, double max)
    {
        Min = Math.Min(min, max);
        Max = Math.Max(min, max);
    }

    public static Extent Point(double value) => new(value, value);

    public double Min { get; }
    public double Max { get; }
    public double Center => (Min + Max) * 0.5;
    public double Size => Max - Min;

    public override string ToString() => $"[{Min:F4}, {Max:F4}]";
}

/// <summary>
/// The one-dimensional maths behind "line these up in the view": which axis a mode works on,
/// which coordinate it aligns, where the common line ends up, and how much each element has to
/// slide to reach it. Deliberately free of the Revit API — the tools project view geometry onto
/// the view's right and up axes and hand the results here as plain numbers.
/// </summary>
public static class ViewAlignmentMath
{
    public const string ModeLeft = "left";
    public const string ModeRight = "right";
    public const string ModeTop = "top";
    public const string ModeBottom = "bottom";

    /// <summary>Centres share a vertical line — the horizontal coordinate is equalised.</summary>
    public const string ModeCenterVertical = "centerVertical";

    /// <summary>Centres share a horizontal line — the vertical coordinate is equalised.</summary>
    public const string ModeCenterHorizontal = "centerHorizontal";

    public const string ModeDistributeHorizontal = "distributeHorizontal";
    public const string ModeDistributeVertical = "distributeVertical";

    public const string ReferenceExtreme = "extreme";
    public const string ReferenceMin = "min";
    public const string ReferenceMax = "max";
    public const string ReferenceAverage = "average";

    /// <summary>Align to one nominated element, identified by its index in the input order.</summary>
    public const string ReferenceElement = "element";

    public const string SpreadCenters = "centers";
    public const string SpreadGaps = "gaps";

    private static readonly Dictionary<string, string> ModeAliases = new(StringComparer.Ordinal)
    {
        ["left"] = ModeLeft,
        ["alignleft"] = ModeLeft,
        ["right"] = ModeRight,
        ["alignright"] = ModeRight,
        ["top"] = ModeTop,
        ["aligntop"] = ModeTop,
        ["bottom"] = ModeBottom,
        ["alignbottom"] = ModeBottom,
        ["centervertical"] = ModeCenterVertical,
        ["centervertically"] = ModeCenterVertical,
        ["verticalcenter"] = ModeCenterVertical,
        ["centerx"] = ModeCenterVertical,
        ["centerhorizontal"] = ModeCenterHorizontal,
        ["centerhorizontally"] = ModeCenterHorizontal,
        ["horizontalcenter"] = ModeCenterHorizontal,
        ["middle"] = ModeCenterHorizontal,
        ["centery"] = ModeCenterHorizontal,
        ["distributehorizontal"] = ModeDistributeHorizontal,
        ["distributehorizontally"] = ModeDistributeHorizontal,
        ["spacehorizontal"] = ModeDistributeHorizontal,
        ["distributevertical"] = ModeDistributeVertical,
        ["distributevertically"] = ModeDistributeVertical,
        ["spacevertical"] = ModeDistributeVertical
    };

    private static readonly Dictionary<string, string> ReferenceAliases = new(StringComparer.Ordinal)
    {
        ["extreme"] = ReferenceExtreme,
        ["outermost"] = ReferenceExtreme,
        ["min"] = ReferenceMin,
        ["lowest"] = ReferenceMin,
        ["max"] = ReferenceMax,
        ["highest"] = ReferenceMax,
        ["average"] = ReferenceAverage,
        ["mean"] = ReferenceAverage,
        ["first"] = "first",
        ["last"] = "last"
    };

    public static IReadOnlyList<string> AllModes { get; } = new[]
    {
        ModeLeft, ModeRight, ModeTop, ModeBottom,
        ModeCenterVertical, ModeCenterHorizontal,
        ModeDistributeHorizontal, ModeDistributeVertical
    };

    /// <summary>
    /// Canonicalises a user-supplied mode. Case, spaces, hyphens and the common spellings
    /// ("align left", "distribute-horizontally", "verticalCenter") all resolve; anything else
    /// returns null so the caller can list the valid values.
    /// </summary>
    public static string? NormalizeMode(string? raw)
    {
        var key = Simplify(raw);
        return key.Length > 0 && ModeAliases.TryGetValue(key, out var mode) ? mode : null;
    }

    /// <summary>
    /// Canonicalises the reference. "first" and "last" stay as-is — they name a position in the
    /// input order, which only the caller can turn into an index.
    /// </summary>
    public static string? NormalizeReference(string? raw)
    {
        var key = Simplify(raw);
        return key.Length > 0 && ReferenceAliases.TryGetValue(key, out var reference) ? reference : null;
    }

    public static string? NormalizeSpread(string? raw)
    {
        var key = Simplify(raw);
        return key switch
        {
            "centers" or "center" => SpreadCenters,
            "gaps" or "gap" or "edges" or "spacing" => SpreadGaps,
            _ => null
        };
    }

    public static bool IsDistribute(string mode) =>
        mode is ModeDistributeHorizontal or ModeDistributeVertical;

    /// <summary>
    /// True when the mode slides elements along the view's right direction. The remaining modes
    /// slide along its up direction.
    /// </summary>
    public static bool UsesHorizontalAxis(string mode) =>
        mode is ModeLeft or ModeRight or ModeCenterVertical or ModeDistributeHorizontal;

    /// <summary>The coordinate a mode lines up: an edge for the edge modes, the centre otherwise.</summary>
    public static double AlignedValue(Extent extent, string mode) => mode switch
    {
        ModeLeft or ModeBottom => extent.Min,
        ModeRight or ModeTop => extent.Max,
        _ => extent.Center
    };

    /// <summary>
    /// The coordinate every element ends up sharing.
    /// <paramref name="referenceIndex"/> is only read for <see cref="ReferenceElement"/>.
    /// <see cref="ReferenceExtreme"/> means the outermost element already in that direction —
    /// the leftmost for <c>left</c>, the topmost for <c>top</c> — which is what "align left"
    /// means in every drafting tool. Centre modes have no outermost element, so they average.
    /// </summary>
    public static double ResolveTarget(
        IReadOnlyList<Extent> extents,
        string mode,
        string reference,
        int referenceIndex = 0)
    {
        if (extents == null || extents.Count == 0)
            throw new ArgumentException("At least one extent is required.", nameof(extents));

        if (reference == ReferenceElement)
        {
            var index = Math.Min(extents.Count - 1, Math.Max(0, referenceIndex));
            return AlignedValue(extents[index], mode);
        }

        var values = new double[extents.Count];
        for (var i = 0; i < extents.Count; i++)
            values[i] = AlignedValue(extents[i], mode);

        switch (reference)
        {
            case ReferenceMin:
                return values.Min();
            case ReferenceMax:
                return values.Max();
            case ReferenceAverage:
                return values.Average();
            default:
                return mode switch
                {
                    ModeLeft or ModeBottom => values.Min(),
                    ModeRight or ModeTop => values.Max(),
                    _ => values.Average()
                };
        }
    }

    /// <summary>How far this element has to slide along the mode's axis to reach the common line.</summary>
    public static double OffsetToTarget(Extent extent, string mode, double target) =>
        target - AlignedValue(extent, mode);

    /// <summary>
    /// Evenly spreads the extents along their axis and returns one offset per input, in input order.
    ///
    /// The two outermost elements never move — they define the span being filled — unless
    /// <paramref name="fixedSpacingFt"/> is given, in which case the lowest element stays put and
    /// the rest are laid out from it at that spacing.
    ///
    /// <paramref name="spread"/> chooses what is made equal: <see cref="SpreadCenters"/> equalises
    /// the distance between centres, <see cref="SpreadGaps"/> equalises the clear space between
    /// bounding boxes, which is what the eye reads as evenly spaced when the elements differ in size.
    ///
    /// <paramref name="step"/> reports the resulting centre-to-centre distance or gap. A negative
    /// gap means the extents overlap — they do not fit in the span they currently occupy.
    /// </summary>
    public static double[] DistributeOffsets(
        IReadOnlyList<Extent> extents,
        string spread,
        double? fixedSpacingFt,
        out double step)
    {
        step = 0.0;
        if (extents == null || extents.Count == 0)
            return Array.Empty<double>();

        var offsets = new double[extents.Count];
        if (extents.Count == 1)
            return offsets;

        // Laid out in the order they currently appear along the axis, not the order they were
        // passed in — distributing a selection must not depend on how it was selected.
        var order = Enumerable.Range(0, extents.Count)
            .OrderBy(i => extents[i].Center)
            .ThenBy(i => i)
            .ToArray();

        var first = extents[order[0]];
        var last = extents[order[order.Length - 1]];
        var count = order.Length;

        if (spread == SpreadGaps)
        {
            double gap;
            if (fixedSpacingFt.HasValue)
            {
                gap = fixedSpacingFt.Value;
            }
            else
            {
                var occupied = 0.0;
                foreach (var index in order)
                    occupied += extents[index].Size;
                gap = (last.Max - first.Min - occupied) / (count - 1);
            }

            step = gap;
            var cursor = first.Min;
            foreach (var index in order)
            {
                offsets[index] = cursor - extents[index].Min;
                cursor += extents[index].Size + gap;
            }
            return offsets;
        }

        var spacing = fixedSpacingFt ?? (last.Center - first.Center) / (count - 1);
        step = spacing;
        for (var position = 0; position < count; position++)
        {
            var index = order[position];
            var target = first.Center + spacing * position;
            offsets[index] = target - extents[index].Center;
        }
        return offsets;
    }

    /// <summary>
    /// Lowercases, drops spaces, hyphens and underscores, and folds "centre" onto "center" so the
    /// alias tables only have to carry one spelling of each.
    /// </summary>
    private static string Simplify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var builder = new System.Text.StringBuilder(raw!.Length);
        foreach (var character in raw)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.Replace("centre", "center").ToString();
    }
}
