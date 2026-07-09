using RevitMCP.Core.Instances;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Registers this Revit process in the shared instance registry so the MCP bridge can
/// discover it (and distinguish it from other running Revit instances, e.g. when
/// Revit 2024 and 2026 are open at the same time).
/// </summary>
public class InstanceRegistrationService
{
    private readonly RevitInstanceRegistry _registry;

    public string RevitVersion { get; }
    public int ProcessId { get; }
    public string PipeName { get; }

    private string? _documentTitle;

    public InstanceRegistrationService(string revitVersion, int processId, string pipeName)
    {
        _registry = new RevitInstanceRegistry();
        RevitVersion = revitVersion;
        ProcessId = processId;
        PipeName = pipeName;
    }

    public void Register()
    {
        _registry.Register(new RevitInstanceInfo
        {
            ProcessId = ProcessId,
            RevitVersion = RevitVersion,
            PipeName = PipeName,
            DocumentTitle = _documentTitle
        });
    }

    public void Unregister() => _registry.Unregister(ProcessId);

    /// <summary>Marks this Revit instance as the one the bridge should talk to.</summary>
    public void MakeActive() => _registry.SetActive(ProcessId);

    public bool IsActive => _registry.GetActiveProcessId() == ProcessId;

    /// <summary>Refreshes the registered document title when it changes.</summary>
    public void UpdateDocumentTitle(string? documentTitle)
    {
        if (string.Equals(_documentTitle, documentTitle, StringComparison.Ordinal)) return;
        _documentTitle = documentTitle;
        Register();
    }
}
