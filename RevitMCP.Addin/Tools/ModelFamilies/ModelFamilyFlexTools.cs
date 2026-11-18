using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Flex tests of family types and checks of placed instances (#176). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyFlexTools
{
    public static void Register(ExternalEventHandler handler)
    {
        // #176: tools are registered here.
    }
}
