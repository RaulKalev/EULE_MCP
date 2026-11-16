using System.Globalization;

namespace RevitMCP.Addin.Editing;

/// <summary>Switchable: the type can be replaced. Unchanged: already that type. Blocked: cannot be done (see reason).</summary>
public enum TypeChangeStatus
{
    Switchable,
    Unchanged,
    Blocked
}

/// <summary>Stable reason codes reported per element.</summary>
public static class TypeChangeReason
{
    public const string NotFound = "notFound";
    public const string IsElementType = "isElementType";
    public const string LinkedDocument = "linkedDocument";
    public const string NoType = "noType";
    public const string InGroup = "inGroup";
    public const string Pinned = "pinned";
    public const string NotEditable = "notEditable";
    public const string CategoryMismatch = "categoryMismatch";
    public const string InvalidForElement = "invalidForElement";
    public const string SameType = "sameType";
}

/// <summary>What the live model says about one candidate element. No Revit types, so it is unit tested.</summary>
public sealed class TypeChangeElementFacts
{
    public long Id { get; set; }
    public bool Exists { get; set; } = true;
    public bool IsElementType { get; set; }
    public bool InLinkedDocument { get; set; }
    public bool HasType { get; set; } = true;
    public long CurrentTypeId { get; set; }
    public long? CategoryId { get; set; }
    /// <summary>Id of the group the element belongs to, 0 when none.</summary>
    public long GroupId { get; set; }
    public bool Pinned { get; set; }
    /// <summary>Worksharing problem (owned by someone else, ...), null when editable.</summary>
    public string? EditProblem { get; set; }
    /// <summary>Element.IsValidType(target); null when it was not asked.</summary>
    public bool? TargetIsValid { get; set; }
}

public sealed class TypeChangeTargetFacts
{
    public long Id { get; set; }
    public long? CategoryId { get; set; }
    public string FamilyName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
}

public sealed class TypeChangeDecision
{
    public long ElementId { get; set; }
    public TypeChangeStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? Detail { get; set; }
}

/// <summary>An element type that could be the target of a switch.</summary>
public sealed class TypeCandidate
{
    public long Id { get; set; }
    public string FamilyName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;

    public string Display => string.IsNullOrEmpty(FamilyName) ? TypeName : $"{FamilyName}: {TypeName}";
}

public sealed class TypeResolution
{
    public TypeCandidate? Type { get; set; }
    public string? Error { get; set; }
    /// <summary>"unknownType" | "ambiguousType" | "missingTarget" | null.</summary>
    public string? Code { get; set; }
    public List<TypeCandidate> Matches { get; set; } = new();
}

/// <summary>One element's result of the apply.</summary>
public sealed class TypeChangeOutcome
{
    public long OldId { get; set; }
    public long NewId { get; set; }
    public long OldTypeId { get; set; }
    public long NewTypeId { get; set; }
    /// <summary>changed | replaced | failed</summary>
    public string Status { get; set; } = "changed";
    public string? Reason { get; set; }
}

/// <summary>Pure planning for revit_change_element_type (#159): target resolution, per-element decisions, summaries.</summary>
public static class ChangeElementTypePlanning
{
    public const int DefaultSampleSize = 20;
    public const int MaxSampleSize = 200;
    public const int MaxElements = 5000;

    public static int ClampSample(int requested) =>
        requested <= 0 ? DefaultSampleSize : Math.Min(requested, MaxSampleSize);

    /// <summary>Validates which elements and which target the call names. Null when usable.</summary>
    public static string? ValidateArguments(int elementIdCount, bool useSelection, long typeId, string? familyName, string? typeName)
    {
        if (elementIdCount == 0 && !useSelection)
            return "Provide elementIds or useSelection=true.";
        if (elementIdCount > MaxElements)
            return $"At most {MaxElements} elements per call (got {elementIdCount}); split the list.";
        if (typeId <= 0 && string.IsNullOrWhiteSpace(typeName))
            return "Name the target type: typeId, or typeName (with familyName when more than one family has that type name).";
        return null;
    }

    /// <summary>
    /// Finds the one target type. typeId wins; otherwise typeName (and familyName / categoryName when given) must match
    /// exactly one candidate, case-insensitively. Never guesses between several.
    /// </summary>
    public static TypeResolution ResolveTarget(IReadOnlyList<TypeCandidate> candidates, long typeId, string? familyName, string? typeName, string? categoryName = null)
    {
        if (typeId > 0)
        {
            var byId = candidates.FirstOrDefault(c => c.Id == typeId);
            return byId != null
                ? new TypeResolution { Type = byId }
                : new TypeResolution { Code = "unknownType", Error = $"typeId {typeId} is not an element type in this document." };
        }
        if (string.IsNullOrWhiteSpace(typeName))
            return new TypeResolution { Code = "missingTarget", Error = "typeId or typeName is required." };

        var matches = candidates.Where(c =>
                string.Equals(c.TypeName, typeName!.Trim(), StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(familyName) || string.Equals(c.FamilyName, familyName!.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(categoryName) || string.Equals(c.CategoryName, categoryName!.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 1) return new TypeResolution { Type = matches[0], Matches = matches };
        if (matches.Count == 0)
            return new TypeResolution
            {
                Code = "unknownType",
                Error = $"No type named '{typeName}'" + (string.IsNullOrWhiteSpace(familyName) ? string.Empty : $" in family '{familyName}'") + " in this document."
            };
        var shown = string.Join("; ", matches.Take(10).Select(m => $"{m.Display} [{m.CategoryName}] id {m.Id.ToString(CultureInfo.InvariantCulture)}"));
        return new TypeResolution
        {
            Code = "ambiguousType",
            Matches = matches,
            Error = $"'{typeName}' matches {matches.Count} types ({shown}{(matches.Count > 10 ? "; …" : string.Empty)}). Pass typeId, or add familyName / categoryName."
        };
    }

    /// <summary>One element's decision. The order matters: a same-type element is unchanged even when pinned or grouped.</summary>
    public static TypeChangeDecision Classify(TypeChangeElementFacts el, TypeChangeTargetFacts target)
    {
        TypeChangeDecision Make(TypeChangeStatus status, string? reason = null, string? detail = null) =>
            new() { ElementId = el.Id, Status = status, Reason = reason, Detail = detail };

        if (!el.Exists) return Make(TypeChangeStatus.Blocked, TypeChangeReason.NotFound, "No element with this id in the document.");
        if (el.InLinkedDocument) return Make(TypeChangeStatus.Blocked, TypeChangeReason.LinkedDocument, "Elements of a linked model cannot be changed from the host.");
        if (el.IsElementType) return Make(TypeChangeStatus.Blocked, TypeChangeReason.IsElementType, "This id is a type, not an instance; pass instance ids.");
        if (!el.HasType) return Make(TypeChangeStatus.Blocked, TypeChangeReason.NoType, "The element has no type to change.");
        if (el.CurrentTypeId == target.Id) return Make(TypeChangeStatus.Unchanged, TypeChangeReason.SameType, "Already this type.");
        if (el.GroupId > 0)
            return Make(TypeChangeStatus.Blocked, TypeChangeReason.InGroup, $"Member of group {el.GroupId.ToString(CultureInfo.InvariantCulture)}; its type is changed through the group.");
        if (el.Pinned) return Make(TypeChangeStatus.Blocked, TypeChangeReason.Pinned, "The element is pinned; unpin it first.");
        if (!string.IsNullOrEmpty(el.EditProblem)) return Make(TypeChangeStatus.Blocked, TypeChangeReason.NotEditable, el.EditProblem);
        if (el.CategoryId != target.CategoryId)
            return Make(TypeChangeStatus.Blocked, TypeChangeReason.CategoryMismatch, "The target type belongs to a different category.");
        if (el.TargetIsValid == false)
            return Make(TypeChangeStatus.Blocked, TypeChangeReason.InvalidForElement, "Revit does not accept this type for the element (family, host or kind does not fit).");
        return Make(TypeChangeStatus.Switchable);
    }

    /// <summary>requireAll: nothing may be written when any element is blocked (unchanged ones are fine).</summary>
    public static bool ShouldRefuse(bool requireAll, IReadOnlyCollection<TypeChangeDecision> decisions) =>
        requireAll && decisions.Any(d => d.Status == TypeChangeStatus.Blocked);

    public static Dictionary<string, int> CountByReason(IEnumerable<TypeChangeDecision> decisions) =>
        decisions.Where(d => d.Status == TypeChangeStatus.Blocked && d.Reason != null)
            .GroupBy(d => d.Reason!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

    public static (int Switchable, int Unchanged, int Blocked) Count(IEnumerable<TypeChangeDecision> decisions)
    {
        int s = 0, u = 0, b = 0;
        foreach (var d in decisions)
            switch (d.Status)
            {
                case TypeChangeStatus.Switchable: s++; break;
                case TypeChangeStatus.Unchanged: u++; break;
                default: b++; break;
            }
        return (s, u, b);
    }

    /// <summary>The outcome of one Element.ChangeTypeId call: a different id back means Revit replaced the element.</summary>
    public static TypeChangeOutcome Outcome(long oldId, long oldTypeId, long newTypeId, long returnedId) =>
        new()
        {
            OldId = oldId,
            NewId = returnedId,
            OldTypeId = oldTypeId,
            NewTypeId = newTypeId,
            Status = returnedId != oldId && returnedId > 0 ? "replaced" : "changed"
        };

    public static TypeChangeOutcome Failure(long oldId, long oldTypeId, long newTypeId, string reason) =>
        new() { OldId = oldId, NewId = oldId, OldTypeId = oldTypeId, NewTypeId = newTypeId, Status = "failed", Reason = reason };

    /// <summary>Old id to new id for the elements Revit replaced (ids that are not the same any more).</summary>
    public static Dictionary<long, long> IdMappings(IEnumerable<TypeChangeOutcome> outcomes) =>
        outcomes.Where(o => o.Status == "replaced").ToDictionary(o => o.OldId, o => o.NewId);
}
