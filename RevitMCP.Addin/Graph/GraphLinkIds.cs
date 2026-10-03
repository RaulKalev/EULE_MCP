using System.Globalization;

namespace RevitMCP.Addin.Graph;

/// <summary>
/// Namespaced ids for linked-model nodes (#62): <c>link:&lt;linkInstanceId&gt;:&lt;linkedElementId&gt;</c>.
/// Host nodes keep plain element ids. Pure — unit tested in RevitMCP.Tests.
/// </summary>
public static class GraphLinkIds
{
    public static string Make(long linkInstanceId, long linkedElementId) =>
        GraphSchema.LinkIdPrefix +
        linkInstanceId.ToString(CultureInfo.InvariantCulture) + ":" +
        linkedElementId.ToString(CultureInfo.InvariantCulture);

    public static bool IsLinked(string? id) =>
        id != null && id.StartsWith(GraphSchema.LinkIdPrefix, StringComparison.Ordinal);

    /// <summary>Splits a linked id; false for host ids and malformed values.</summary>
    public static bool TryParse(string? id, out long linkInstanceId, out long linkedElementId)
    {
        linkInstanceId = 0;
        linkedElementId = 0;
        if (!IsLinked(id)) return false;
        var rest = id!.Substring(GraphSchema.LinkIdPrefix.Length);
        var colon = rest.IndexOf(':');
        if (colon <= 0 || colon == rest.Length - 1) return false;
        return long.TryParse(rest.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out linkInstanceId) &&
               long.TryParse(rest.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out linkedElementId);
    }

    /// <summary>The link instance id a node belongs to, or null for host nodes.</summary>
    public static string? LinkInstanceOf(string? id) =>
        TryParse(id, out var inst, out _) ? inst.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Half-open id range [from, to) covering every node of one link instance, or of all links when
    /// <paramref name="linkInstanceId"/> is null. Ranges use the primary-key index (';' sorts right after ':').
    /// </summary>
    public static (string From, string To) Range(string? linkInstanceId) =>
        linkInstanceId == null
            ? (GraphSchema.LinkIdPrefix, "link;")
            : (GraphSchema.LinkIdPrefix + linkInstanceId + ":", GraphSchema.LinkIdPrefix + linkInstanceId + ";");
}
