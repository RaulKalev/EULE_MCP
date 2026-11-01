namespace RevitMCP.Addin.Annotation;

/// <summary>An annotation's box in the view's plane, in mm.</summary>
public sealed class OverlapItem
{
    public long Id { get; set; }

    /// <summary>textNote, tag or viewport.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>What to show for it: a note's text, a viewport's view name.</summary>
    public string Label { get; set; } = string.Empty;

    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}

/// <summary>One straight piece of a detail line, in mm.</summary>
public sealed class OverlapSegment
{
    public long Id { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
}

/// <summary>Text overlapping something, with both ids. A line reports its length inside the note in OverlapXmm.</summary>
public sealed class OverlapFinding
{
    public long TextNoteId { get; set; }
    public string Text { get; set; } = string.Empty;
    public long OtherId { get; set; }

    /// <summary>textNote, tag, detailLine or viewportEdge.</summary>
    public string OtherKind { get; set; } = string.Empty;

    public string OtherLabel { get; set; } = string.Empty;
    public double OverlapXmm { get; set; }
    public double OverlapYmm { get; set; }
}

/// <summary>
/// Finds text notes overlapping other annotation in one view or sheet (#141). Works on boxes in the view's plane, so a
/// wrapped note is seen at its real size. Pure, unit tested in RevitMCP.Tests.
/// </summary>
public static class AnnotationOverlapMath
{
    /// <param name="minOverlapMm">Overlaps smaller than this in either direction are touching, not overlapping.</param>
    public static List<OverlapFinding> Find(
        IReadOnlyList<OverlapItem> notes,
        IReadOnlyList<OverlapItem> tags,
        IReadOnlyList<OverlapSegment> lines,
        IReadOnlyList<OverlapItem> viewports,
        double minOverlapMm)
    {
        var findings = new List<OverlapFinding>();

        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            for (var j = i + 1; j < notes.Count; j++)
                AddBoxOverlap(findings, note, notes[j], "textNote", minOverlapMm);

            foreach (var tag in tags)
                AddBoxOverlap(findings, note, tag, "tag", minOverlapMm);

            // One line of several pieces is one finding: the longest piece inside the note.
            foreach (var group in lines.GroupBy(l => l.Id))
            {
                var inside = group.Max(l => LengthInside(note, l, minOverlapMm));
                if (inside >= minOverlapMm)
                    findings.Add(new OverlapFinding
                    {
                        TextNoteId = note.Id,
                        Text = note.Label,
                        OtherId = group.Key,
                        OtherKind = "detailLine",
                        OverlapXmm = inside
                    });
            }

            // A note inside a viewport is a choice; one crossing its edge is the problem.
            foreach (var viewport in viewports)
                if (!Contains(viewport, note))
                    AddBoxOverlap(findings, note, viewport, "viewportEdge", minOverlapMm);
        }

        return findings
            .OrderByDescending(f => Math.Max(f.OverlapXmm, f.OverlapYmm))
            .ThenBy(f => f.TextNoteId)
            .ToList();
    }

    /// <summary>
    /// The box of a note's text from its anchor. Revit's bounding box of a note includes its leaders, which would report
    /// everything a leader passes over. <paramref name="horizontal"/> is Left, Center or Right; <paramref name="vertical"/>
    /// Top, Middle or Bottom.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) TextBox(
        double anchorX, double anchorY, double widthMm, double heightMm, string horizontal, string vertical)
    {
        var minX = horizontal switch
        {
            "Center" => anchorX - widthMm / 2,
            "Right" => anchorX - widthMm,
            _ => anchorX
        };
        var maxY = vertical switch
        {
            "Middle" => anchorY + heightMm / 2,
            "Bottom" => anchorY + heightMm,
            _ => anchorY
        };
        return (minX, maxY - heightMm, minX + widthMm, maxY);
    }

    /// <summary>Length of a segment inside the note's box shrunk by the tolerance (Liang-Barsky clipping).</summary>
    public static double LengthInside(OverlapItem box, OverlapSegment line, double toleranceMm = 0)
    {
        var dx = line.X2 - line.X1;
        var dy = line.Y2 - line.Y1;
        var t0 = 0.0;
        var t1 = 1.0;

        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            var t = q / p;
            if (p < 0)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
            return true;
        }

        var inner = toleranceMm / 2;
        if (!Clip(-dx, line.X1 - (box.MinX + inner)) || !Clip(dx, box.MaxX - inner - line.X1) ||
            !Clip(-dy, line.Y1 - (box.MinY + inner)) || !Clip(dy, box.MaxY - inner - line.Y1))
            return 0;
        return t1 <= t0 ? 0 : (t1 - t0) * Math.Sqrt(dx * dx + dy * dy);
    }

    private static void AddBoxOverlap(List<OverlapFinding> findings, OverlapItem note, OverlapItem other, string kind, double minOverlapMm)
    {
        var x = Math.Min(note.MaxX, other.MaxX) - Math.Max(note.MinX, other.MinX);
        var y = Math.Min(note.MaxY, other.MaxY) - Math.Max(note.MinY, other.MinY);
        if (x < minOverlapMm || y < minOverlapMm) return;
        findings.Add(new OverlapFinding
        {
            TextNoteId = note.Id,
            Text = note.Label,
            OtherId = other.Id,
            OtherKind = kind,
            OtherLabel = other.Label,
            OverlapXmm = x,
            OverlapYmm = y
        });
    }

    private static bool Contains(OverlapItem outer, OverlapItem inner) =>
        inner.MinX >= outer.MinX && inner.MaxX <= outer.MaxX && inner.MinY >= outer.MinY && inner.MaxY <= outer.MaxY;
}
