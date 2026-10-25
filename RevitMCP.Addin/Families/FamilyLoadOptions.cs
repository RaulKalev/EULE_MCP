using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Families;

/// <summary>
/// The answers Revit gets when a family being loaded already exists in the project (#102). Maps the ifExists policy
/// (<see cref="FamilyLoadPolicy.Decide"/>) to IFamilyLoadOptions and records what Revit asked, for the result.
/// </summary>
internal sealed class FamilyLoadOptions : IFamilyLoadOptions
{
    private readonly FamilyIfExists _policy;

    public FamilyLoadOptions(FamilyIfExists policy) => _policy = policy;

    /// <summary>Revit found the family already loaded.</summary>
    public bool FamilyFound { get; private set; }
    public bool FamilyInUse { get; private set; }
    /// <summary>Shared nested families Revit found already loaded, by name.</summary>
    public List<string> SharedFamiliesFound { get; } = new();

    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        FamilyFound = true;
        FamilyInUse = familyInUse;
        var decision = FamilyLoadPolicy.Decide(_policy, shared: false);
        overwriteParameterValues = decision.OverwriteParameterValues;
        return decision.Load;
    }

    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
    {
        try { SharedFamiliesFound.Add(sharedFamily?.Name ?? string.Empty); }
        catch { /* the name is diagnostics only */ }
        var decision = FamilyLoadPolicy.Decide(_policy, shared: true);
        source = decision.SharedFromFile ? FamilySource.Family : FamilySource.Project;
        overwriteParameterValues = decision.OverwriteParameterValues;
        return decision.Load;
    }
}
