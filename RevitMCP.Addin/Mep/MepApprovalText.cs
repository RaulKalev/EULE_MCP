using System.Globalization;

namespace RevitMCP.Addin.Mep;

/// <summary>Approval wording of the MEP routing tools (#106), shared by the live summary and MepApprovalSummaries. Pure.</summary>
public static class MepApprovalText
{
    /// <summary>"Create a cable tray run 'KL-300x60' of 6 segments, 38.4 m, 5 elbows, connect start to tray 123456."</summary>
    public static string CreateRun(string kind, string typeName, int segments, double totalLengthMm, int elbows,
        MepEndConnection start, MepEndConnection end, double snapMm, string? levelName = null)
    {
        var text = $"Create a {MepKind.Label(kind)} run '{typeName}' of {segments} segment{(segments == 1 ? string.Empty : "s")}, " +
                   $"{(totalLengthMm / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} m, {elbows} elbow{(elbows == 1 ? string.Empty : "s")}" +
                   (string.IsNullOrEmpty(levelName) ? string.Empty : $" on '{levelName}'");
        var joins = new List<string>();
        if (start.Mode != MepEndMode.None) joins.Add(Join("start", start, kind, snapMm));
        if (end.Mode != MepEndMode.None) joins.Add(Join("end", end, kind, snapMm));
        return text + (joins.Count > 0 ? ", " + string.Join(", ", joins) : string.Empty) + ".";
    }

    /// <summary>"Connect cable tray 1 to cable tray 2 (mode auto)."</summary>
    public static string Connect(long elementIdA, long elementIdB, string mode) =>
        $"Join the open ends of tray/conduit {elementIdA} and {elementIdB} (mode {mode}): a fitting or a direct connection, " +
        $"{(mode == "extend" || mode == "auto" ? "extending " + elementIdA.ToString(CultureInfo.InvariantCulture) + " where the two are in line" : "no extension")}.";

    private static string Join(string which, MepEndConnection connection, string kind, double snapMm)
    {
        var noun = kind == MepKind.CableTray ? "tray" : "conduit";
        return connection.Mode == MepEndMode.Auto
            ? $"connect {which} to the nearest open {noun} end within {MepMath.FormatMm(snapMm)} mm"
            : $"connect {which} to {noun} {connection.ElementId.ToString(CultureInfo.InvariantCulture)}";
    }
}
