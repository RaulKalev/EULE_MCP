using System;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Shared parameter-lookup helper used by the bulk parameter-setting tools.
/// Resolves a parameter by exact name first (<see cref="Element.LookupParameter"/>), then falls
/// back to a single case-insensitive "contains" match. When more than one parameter contains the
/// requested name the match is ambiguous and <c>null</c> is returned, forcing callers to use an
/// exact name.
/// </summary>
public static class ParameterFinder
{
    public static Parameter? Find(Element element, string name)
    {
        var exact = element.LookupParameter(name);
        if (exact != null) return exact;

        Parameter? match = null;
        foreach (Parameter p in element.Parameters)
        {
            // string.Contains(string, StringComparison) is unavailable on net48; IndexOf is portable.
            if (p.Definition.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (match != null) return null; // ambiguous — require exact name
                match = p;
            }
        }
        return match;
    }
}
