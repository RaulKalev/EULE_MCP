using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Forms from CAD profiles or family curves (#177). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyProfileTools
{
    public static void Register(ExternalEventHandler handler)
    {
        // #177: tools are registered here.
    }
}
