using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.Tools.Bcf;

/// <summary>Registers the BCF clash review and clash fix tools (docs/bcf-clashes.md).</summary>
public static class BcfToolRegistration
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new BcfReadTool());
        handler.RegisterTool(new BcfCheckTool());
        handler.RegisterTool(new ProposeClashFixesTool());
        handler.RegisterTool(new ApplyClashFixesTool());
    }
}
