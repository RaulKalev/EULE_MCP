using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.CadManagement;

/// <summary>
/// Argument parsing shared by the CAD point reader and the two place-from-CAD tools, so all three
/// resolve the same import, the same layers, and the same points.
/// </summary>
internal static class CadPlacementRequest
{
    public const double DefaultMergeToleranceMm = 1.0;
    public const double DefaultDuplicateToleranceMm = 50.0;
    public const int DefaultMaxInstances = 500;
    public const int HardMaxInstances = 2000;

    public static double MmToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    public static double FtToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

    /// <summary>
    /// Resolves the CAD import to read. With no id and exactly one import in the model that one is
    /// used; with several the caller is told to pick, because guessing which drawing holds the
    /// locations is not something to be silent about.
    /// </summary>
    public static ImportInstance? ResolveImport(
        Document doc,
        Dictionary<string, object?> arguments,
        out string? error)
    {
        error = null;
        var importInstanceId = ToolArguments.GetLong(arguments, "importInstanceId");

        if (importInstanceId > 0)
        {
            if (doc.GetElement(new ElementId(importInstanceId)) is ImportInstance byId)
                return byId;
            error = $"Element {importInstanceId} is not a CAD import or link. " +
                    "Use revit_list_cad_imports to find the importInstanceId.";
            return null;
        }

        var imports = new FilteredElementCollector(doc)
            .OfClass(typeof(ImportInstance))
            .WhereElementIsNotElementType()
            .Cast<ImportInstance>()
            .ToList();

        if (imports.Count == 0)
        {
            error = "This model has no imported or linked CAD files.";
            return null;
        }

        if (imports.Count > 1)
        {
            var sample = string.Join("; ", imports.Take(10)
                .Select(i => $"{CadPointExtractor.SafeName(i)} (importInstanceId {i.Id.Value})"));
            error = $"{imports.Count} CAD imports are present — pass importInstanceId. Found: {sample}";
            return null;
        }

        return imports[0];
    }

    /// <summary>The point sources to accept. Defaults to all three.</summary>
    public static HashSet<string>? ParseSources(Dictionary<string, object?> arguments, out string? error)
    {
        error = null;
        var requested = ToolArguments.GetStringArray(arguments, "pointSources");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (requested.Length == 0)
        {
            sources.Add(CadPointMath.SourceBlock);
            sources.Add(CadPointMath.SourcePoint);
            sources.Add(CadPointMath.SourceCircle);
            return sources;
        }

        foreach (var raw in requested)
        {
            var source = raw.Trim().ToLowerInvariant();
            if (!CadPointMath.IsKnownSource(source))
            {
                error = $"Unknown pointSource '{raw}'. Valid: block, point, circle.";
                return null;
            }
            sources.Add(source);
        }

        return sources;
    }

    public static double ParseMergeTolerance(Dictionary<string, object?> arguments, List<string> warnings)
    {
        var tolerance = ToolArguments.GetDouble(arguments, "mergeToleranceMm", DefaultMergeToleranceMm);
        if (tolerance < 0)
        {
            warnings.Add($"mergeToleranceMm {tolerance} is negative; duplicate marks were not merged.");
            return 0;
        }
        return tolerance;
    }

    /// <summary>
    /// Reads the layers to take points from. The place tools require this: which layer holds the
    /// locations differs per project, and it is the caller's job to ask rather than the tool's to
    /// guess.
    /// </summary>
    public static HashSet<string>? ParseLayers(Dictionary<string, object?> arguments)
    {
        var layers = ToolArguments.GetStringArray(arguments, "layers")
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .ToList();

        return layers.Count == 0
            ? null
            : new HashSet<string>(layers, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns the elevation arguments into one height for every point. There is deliberately no
    /// default mode: a 2D drawing carries no mounting height, and silently placing everything on
    /// the floor is the failure this guards against.
    /// </summary>
    public static ElevationPlan? ParseElevation(
        Document doc,
        Dictionary<string, object?> arguments,
        out string? error)
    {
        error = null;
        var mode = ToolArguments.GetString(arguments, "elevationMode").Trim().ToLowerInvariant();

        if (mode.Length == 0)
        {
            error = "elevationMode is required: 'dwg' to keep the height from the drawing, " +
                    "'level' for a levelName plus offsetMm, or 'explicit' for an absolute elevationMm. " +
                    "A 2D drawing has no mounting height — ask which one applies before placing.";
            return null;
        }

        if (!CadPointMath.IsKnownElevationMode(mode))
        {
            error = $"Unknown elevationMode '{mode}'. Valid: dwg, level, explicit.";
            return null;
        }

        var plan = new ElevationPlan
        {
            Mode = mode,
            OffsetMm = ToolArguments.GetDouble(arguments, "offsetMm"),
            ExplicitElevationMm = ToolArguments.GetDouble(arguments, "elevationMm")
        };

        var levelName = ToolArguments.GetString(arguments, "levelName").Trim();
        if (levelName.Length > 0)
        {
            plan.Level = FamilyInstancePlacer.FindLevel(doc, levelName);
            if (plan.Level == null)
            {
                error = $"Level '{levelName}' was not found in this model.";
                return null;
            }
            plan.LevelElevationMm = FtToMm(plan.Level.Elevation);
        }
        else if (mode == CadPointMath.ElevationFromLevel)
        {
            error = "elevationMode=level needs levelName — the level the offsetMm is measured from.";
            return null;
        }

        return plan;
    }
}

internal sealed class ElevationPlan
{
    public string Mode { get; set; } = string.Empty;
    public Level? Level { get; set; }
    public double? LevelElevationMm { get; set; }
    public double OffsetMm { get; set; }
    public double ExplicitElevationMm { get; set; }

    public bool TryResolve(double dwgZmm, out double elevationMm, out string? error) =>
        CadPointMath.TryResolveElevation(
            Mode, dwgZmm, LevelElevationMm, OffsetMm, ExplicitElevationMm, out elevationMm, out error);

    public string Describe() => Mode switch
    {
        CadPointMath.ElevationFromDwg => "height taken from the drawing",
        CadPointMath.ElevationFromLevel =>
            $"level '{Level?.Name}' ({LevelElevationMm:F0} mm) + {OffsetMm:F0} mm",
        CadPointMath.ElevationExplicit => $"{ExplicitElevationMm:F0} mm absolute",
        _ => Mode
    };
}
