using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Parameters;

/// <summary>
/// The Revit side of <see cref="ParameterSpecMap"/> (#102): SpecTypeId / GroupTypeId members of the running Revit for
/// the map's keys, labels through LabelUtils, and label matching over every spec and built-in group as a fallback.
/// The same members exist in the Revit 2024 and 2026 APIs.
/// </summary>
internal static class ParameterRevitTypes
{
    public static ForgeTypeId? SpecOfKey(string key) => key switch
    {
        "String.Text" => SpecTypeId.String.Text,
        "String.MultilineText" => SpecTypeId.String.MultilineText,
        "String.Url" => SpecTypeId.String.Url,
        "Int.Integer" => SpecTypeId.Int.Integer,
        "Int.NumberOfPoles" => SpecTypeId.Int.NumberOfPoles,
        "Number" => SpecTypeId.Number,
        "Length" => SpecTypeId.Length,
        "Area" => SpecTypeId.Area,
        "Volume" => SpecTypeId.Volume,
        "Angle" => SpecTypeId.Angle,
        "Slope" => SpecTypeId.Slope,
        "Currency" => SpecTypeId.Currency,
        "Boolean.YesNo" => SpecTypeId.Boolean.YesNo,
        "Reference.Material" => SpecTypeId.Reference.Material,
        "Reference.Image" => SpecTypeId.Reference.Image,
        "Current" => SpecTypeId.Current,
        "ElectricalPotential" => SpecTypeId.ElectricalPotential,
        "ElectricalPower" => SpecTypeId.ElectricalPower,
        "ApparentPower" => SpecTypeId.ApparentPower,
        "Wattage" => SpecTypeId.Wattage,
        _ => null
    };

    public static ForgeTypeId? GroupOfKey(string key) => key switch
    {
        "Data" => GroupTypeId.Data,
        "IdentityData" => GroupTypeId.IdentityData,
        "Text" => GroupTypeId.Text,
        "General" => GroupTypeId.General,
        "Constraints" => GroupTypeId.Constraints,
        "Geometry" => GroupTypeId.Geometry,
        "Construction" => GroupTypeId.Construction,
        "Graphics" => GroupTypeId.Graphics,
        "Materials" => GroupTypeId.Materials,
        "Phasing" => GroupTypeId.Phasing,
        "Electrical" => GroupTypeId.Electrical,
        "ElectricalCircuiting" => GroupTypeId.ElectricalCircuiting,
        "ElectricalLighting" => GroupTypeId.ElectricalLighting,
        "ElectricalLoads" => GroupTypeId.ElectricalLoads,
        "ElectricalEngineering" => GroupTypeId.ElectricalEngineering,
        "Mechanical" => GroupTypeId.Mechanical,
        "Plumbing" => GroupTypeId.Plumbing,
        "FireProtection" => GroupTypeId.FireProtection,
        "Structural" => GroupTypeId.Structural,
        "Ifc" => GroupTypeId.Ifc,
        "GreenBuilding" => GroupTypeId.GreenBuilding,
        "Visibility" => GroupTypeId.Visibility,
        _ => null
    };

    /// <summary>
    /// A dataType argument → the forge id of the running Revit: a friendly name or SpecTypeId member path, a raw forge
    /// id (matched version-free against every spec), or Revit's own label of a spec ("Electrical Potential").
    /// </summary>
    public static string? ResolveSpec(string value)
    {
        var resolved = ParameterSpecMap.ResolveSpec(value);
        if (resolved?.Key != null)
            return SpecOfKey(resolved.Key)?.TypeId;

        var specs = AllSpecs();
        if (resolved?.ForgeId != null)
        {
            var match = specs.FirstOrDefault(s => ParameterSpecMap.SameType(s.TypeId, resolved.ForgeId));
            if (match != null) return match.TypeId;
            try
            {
                var id = new ForgeTypeId(resolved.ForgeId);
                return SpecUtils.IsValidDataType(id) ? id.TypeId : null;
            }
            catch { return null; }
        }

        var norm = ParameterSpecMap.Normalize(value);
        return specs.FirstOrDefault(s => ParameterSpecMap.Normalize(SpecLabel(s)) == norm)?.TypeId;
    }

    /// <summary>A parameterGroup argument → map key ("" = Other) or the forge id of a built-in group matched by its label.</summary>
    public static string? ResolveGroup(string value)
    {
        var key = ParameterSpecMap.ResolveGroupKey(value);
        if (key != null) return key;
        if (ParameterSpecMap.IsForgeId(value))
        {
            try { return ParameterUtils.IsBuiltInGroup(new ForgeTypeId(value.Trim())) ? value.Trim() : null; }
            catch { return null; }
        }
        var norm = ParameterSpecMap.Normalize(value);
        try
        {
            return ParameterUtils.GetAllBuiltInGroups().FirstOrDefault(g => ParameterSpecMap.Normalize(GroupLabel(g)) == norm)?.TypeId;
        }
        catch { return null; }
    }

    /// <summary>The GroupTypeId for a resolved group: null means Other (no group).</summary>
    public static ForgeTypeId? GroupOf(string? keyOrForgeId)
    {
        if (string.IsNullOrEmpty(keyOrForgeId)) return null;
        return GroupOfKey(keyOrForgeId!) ?? (ParameterSpecMap.IsForgeId(keyOrForgeId!) ? new ForgeTypeId(keyOrForgeId) : null);
    }

    public static ForgeTypeId? SpecOf(string? forgeId) => string.IsNullOrEmpty(forgeId) ? null : new ForgeTypeId(forgeId);

    public static string SpecLabel(ForgeTypeId? spec)
    {
        if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return string.Empty;
        try { return LabelUtils.GetLabelForSpec(spec); }
        catch { return ParameterSpecMap.FriendlyOfForgeId(spec.TypeId) ?? spec.TypeId; }
    }

    public static string GroupLabel(ForgeTypeId? group)
    {
        if (group == null || string.IsNullOrEmpty(group.TypeId)) return "Other";
        try { return LabelUtils.GetLabelForGroup(group); }
        catch { return group.TypeId; }
    }

    /// <summary>The friendly name of a spec when the map knows it (text, length, …), else null.</summary>
    public static string? FriendlyOf(ForgeTypeId? spec)
    {
        if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return null;
        foreach (var key in ParameterSpecMap.SpecKeys)
        {
            var known = SpecOfKey(key);
            if (known != null && ParameterSpecMap.SameType(known.TypeId, spec.TypeId)) return ParameterSpecMap.FriendlyOfKey(key);
        }
        return ParameterSpecMap.FriendlyOfForgeId(spec.TypeId);
    }

    private static List<ForgeTypeId> AllSpecs()
    {
        try { return SpecUtils.GetAllSpecs().ToList(); }
        catch { return new List<ForgeTypeId>(); }
    }
}
