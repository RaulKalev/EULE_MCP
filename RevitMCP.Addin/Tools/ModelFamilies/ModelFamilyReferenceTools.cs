using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Reference planes, reference lines, dimensions and constraints of a model family (#170). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyReferenceTools
{
    public static void Register(ExternalEventHandler handler)
    {
        // #170: tools are registered here.
    }
}
