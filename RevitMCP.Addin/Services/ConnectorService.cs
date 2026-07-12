using RevitMCP.Addin.Approval;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Manages the lifecycle of the pipe server. Start/Stop/PanicStop are called from the WPF UI thread.
/// </summary>
public class ConnectorService
{
    private readonly PipeServer _pipeServer;
    private readonly ExternalEventService _eventService;
    private readonly ApprovalService? _approvalService;
    private readonly InstanceRegistrationService? _registration;

    public event Action<bool>? StatusChanged;
    public bool IsRunning { get; private set; }

    public ConnectorService(
        PipeServer pipeServer,
        ExternalEventService eventService,
        ApprovalService? approvalService = null,
        InstanceRegistrationService? registration = null)
    {
        _pipeServer = pipeServer;
        _eventService = eventService;
        _approvalService = approvalService;
        _registration = registration;
    }

    /// <summary>Pipe name this connector listens on (unique per Revit process).</summary>
    public string? PipeName => _registration?.PipeName;

    /// <summary>True when this Revit instance is the user-selected active instance for the bridge.</summary>
    public bool IsActiveInstance => _registration?.IsActive ?? false;

    /// <summary>Marks this Revit instance as the one the bridge should route requests to.</summary>
    public void MakeActive() => _registration?.MakeActive();

    /// <summary>Refreshes the registered document title so the bridge can show which project this is.</summary>
    public void UpdateDocumentTitle(string? documentTitle)
    {
        if (IsRunning) _registration?.UpdateDocumentTitle(documentTitle);
    }

    public void Start()
    {
        if (IsRunning) return;
        _pipeServer.Start();
        _registration?.Register();
        IsRunning = true;
        StatusChanged?.Invoke(true);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _pipeServer.Stop();
        _registration?.Unregister();
        IsRunning = false;
        StatusChanged?.Invoke(false);
    }

    public void PanicStop()
    {
        _approvalService?.RejectAll();
        _eventService.CancelAllPending("Request cancelled by Panic Stop.");
        _pipeServer.Stop();
        _registration?.Unregister();
        IsRunning = false;
        StatusChanged?.Invoke(false);
    }
}
