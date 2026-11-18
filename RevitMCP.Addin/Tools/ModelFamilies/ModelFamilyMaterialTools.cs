using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Materials of a model family and the forms they are assigned to (#173). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyMaterialTools
{
    public static void Register(ExternalEventHandler handler)
    {
        // #173: tools are registered here.
    }
}
