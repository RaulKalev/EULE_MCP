using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documents;

/// <summary>Who owns an element in a workshared model and whether central has a newer version of it (#101).</summary>
public sealed class OwnershipInfo
{
    public long Id { get; set; }
    public bool Exists { get; set; } = true;

    /// <summary>mine | other | none | unknown.</summary>
    public string Checkout { get; set; } = "unknown";
    public string? Owner { get; set; }
    public string? Creator { get; set; }
    public string? LastChangedBy { get; set; }

    /// <summary>current | updatedInCentral | deletedInCentral | notYetInCentral | unknown.</summary>
    public string Central { get; set; } = "unknown";

    public bool OwnedByOther => Checkout == "other";
    public bool UpdatedInCentral => Central is "updatedInCentral" or "deletedInCentral";
}

/// <summary>Reads checkout and central status of elements. API thread only; never throws.</summary>
public static class OwnershipReader
{
    public static OwnershipInfo Read(Document doc, ElementId id, bool withTooltip = false)
    {
        var info = new OwnershipInfo { Id = id.Value };
        try
        {
            if (doc.GetElement(id) == null)
            {
                info.Exists = false;
                return info;
            }
            var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            info.Checkout = status switch
            {
                CheckoutStatus.OwnedByCurrentUser => "mine",
                CheckoutStatus.OwnedByOtherUser => "other",
                CheckoutStatus.NotOwned => "none",
                _ => "unknown"
            };
            info.Owner = string.IsNullOrEmpty(owner) ? null : owner;
            info.Central = WorksharingUtils.GetModelUpdatesStatus(doc, id) switch
            {
                ModelUpdatesStatus.CurrentWithCentral => "current",
                ModelUpdatesStatus.UpdatedInCentral => "updatedInCentral",
                ModelUpdatesStatus.DeletedInCentral => "deletedInCentral",
                ModelUpdatesStatus.NotYetInCentral => "notYetInCentral",
                _ => "unknown"
            };
            if (withTooltip)
            {
                var tooltip = WorksharingUtils.GetWorksharingTooltipInfo(doc, id);
                info.Creator = NullIfEmpty(tooltip.Creator);
                info.LastChangedBy = NullIfEmpty(tooltip.LastChangedBy);
                info.Owner ??= NullIfEmpty(tooltip.Owner);
            }
        }
        catch
        {
            // Unknown stays unknown: never let an ownership read fail the caller.
        }
        return info;
    }

    /// <summary>
    /// Why an element cannot be edited from this local now (owned by someone else, newer or deleted in central),
    /// or null when it can. Non-workshared documents are always editable.
    /// </summary>
    public static string? EditProblem(Document doc, ElementId id, out OwnershipInfo? info)
    {
        info = null;
        try
        {
            if (!doc.IsWorkshared) return null;
        }
        catch
        {
            return null;
        }
        info = Read(doc, id);
        if (info.OwnedByOther) return $"owned by {info.Owner ?? "another user"}";
        if (info.Central == "updatedInCentral") return "updated in central by another user; reload latest first";
        if (info.Central == "deletedInCentral") return "deleted in central";
        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>Moves elements to a workset through ELEM_PARTITION_PARAM (#101). Inside a transaction, API thread.</summary>
public static class WorksetAssigner
{
    /// <summary>True when the element now is on <paramref name="worksetId"/>; otherwise false with the reason.</summary>
    public static bool TryAssign(Document doc, Element element, WorksetId worksetId, out string? reason)
    {
        reason = null;
        try
        {
            if (element.WorksetId == worksetId) return true;
            var problem = OwnershipReader.EditProblem(doc, element.Id, out _);
            if (problem != null)
            {
                reason = problem;
                return false;
            }
            var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (parameter == null || parameter.IsReadOnly)
            {
                reason = "its workset cannot be changed (view-specific, a type, or a system element)";
                return false;
            }
            if (!parameter.Set(worksetId.IntegerValue))
            {
                reason = "Revit refused the workset";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
