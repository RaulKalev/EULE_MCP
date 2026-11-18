using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The model-family authoring tools (#168): each feature registers its own tools and inspection sections.
/// Called once from App.OnStartup.
/// </summary>
public static class ModelFamilyToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        ModelFamilyDocumentTools.Register(handler);   // #169
        ModelFamilyReferenceTools.Register(handler);  // #170
        ModelFamilyFormTools.Register(handler);       // #171
        ModelFamilyParameterTools.Register(handler);  // #172
        ModelFamilyMaterialTools.Register(handler);   // #173
        ModelFamilyConnectorTools.Register(handler);  // #174
        ModelFamilyComponentTools.Register(handler);  // #175
        ModelFamilyFlexTools.Register(handler);       // #176
        ModelFamilyProfileTools.Register(handler);    // #177
    }
}
