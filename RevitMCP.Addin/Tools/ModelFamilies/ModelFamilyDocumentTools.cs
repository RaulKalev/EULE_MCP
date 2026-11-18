using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Create, inspect and edit loadable model-family documents (#169). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyDocumentTools
{
    public static void Register(ExternalEventHandler handler)
    {
        // #169: tools are registered here.
    }
}
