using Autodesk.Revit.DB;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Shared parameter-lookup helper used by the bulk parameter-setting tools.
/// Resolves through <see cref="ParameterResolution"/>: an exact name first (a user/shared parameter
/// before a built-in one with the same name), then a single partial match. When more than one
/// parameter matches equally well the match is ambiguous and <c>null</c> is returned, forcing
/// callers to use an exact name.
/// </summary>
public static class ParameterFinder
{
    public static Parameter? Find(Element element, string name) => Find(element, name, out _);

    public static Parameter? Find(Element element, string name, out string? problem)
    {
        var resolution = ParameterResolver.Resolve(element.Document, element, new ParameterTarget { Name = name });
        problem = resolution.Problem;
        return ParameterResolver.Selected(resolution);
    }
}
