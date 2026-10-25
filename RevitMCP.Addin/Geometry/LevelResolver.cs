namespace RevitMCP.Addin.Geometry;

/// <summary>What <see cref="LevelResolver"/> needs to know about one element, so the rule is testable without Revit.</summary>
public interface ILevelReader
{
    /// <summary><c>Element.LevelId</c>, or null when it is invalid or cannot be read.</summary>
    long? LevelId();

    /// <summary>The level id stored in a level parameter (one of <see cref="LevelResolver.FallbackParameters"/>), or null.</summary>
    long? ParameterLevelId(string builtInParameter);

    /// <summary>The name of the level with this id, or null when the id is not a level.</summary>
    string? LevelName(long levelId);
}

/// <summary>An element's level: id, name and where it was found (<c>LevelId</c> or the parameter's name).</summary>
public sealed class LevelResolution
{
    public static readonly LevelResolution None = new(null, string.Empty, null);

    public LevelResolution(long? id, string name, string? source)
    {
        Id = id;
        Name = name;
        Source = source;
    }

    public long? Id { get; }
    public string Name { get; }
    public string? Source { get; }
    public bool Found => Id.HasValue;
}

/// <summary>
/// The level of an element (#107), with the fallback order the model graph uses
/// (<c>RevitGraphExtractor.LevelOf</c>, copied here so the geometry tools and the graph can share it): the element's
/// own <c>LevelId</c>, then the level parameters of family instances, MEP curves, schedules and generic elements.
/// A level id whose element is not a level (no name) counts as no level. Pure; unit tested with a fake reader.
/// </summary>
public static class LevelResolver
{
    /// <summary>The level parameters read, in order, when <c>LevelId</c> is empty (BuiltInParameter names).</summary>
    public static readonly string[] FallbackParameters =
    {
        "FAMILY_LEVEL_PARAM", "RBS_START_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "LEVEL_PARAM"
    };

    public const string SourceLevelId = "LevelId";

    public static LevelResolution Resolve(ILevelReader reader)
    {
        long? id = null;
        string? source = null;
        try { id = Valid(reader.LevelId()); } catch { id = null; }
        if (id.HasValue) source = SourceLevelId;

        if (!id.HasValue)
        {
            foreach (var parameter in FallbackParameters)
            {
                long? candidate;
                try { candidate = Valid(reader.ParameterLevelId(parameter)); }
                catch { candidate = null; }
                if (!candidate.HasValue) continue;
                id = candidate;
                source = parameter;
                break;
            }
        }
        if (!id.HasValue) return LevelResolution.None;

        string? name;
        try { name = reader.LevelName(id.Value); }
        catch { name = null; }
        return string.IsNullOrEmpty(name) ? LevelResolution.None : new LevelResolution(id, name!, source);
    }

    /// <summary>Revit's invalid id is -1; anything not positive is no level.</summary>
    private static long? Valid(long? id) => id.HasValue && id.Value > 0 ? id : null;
}
