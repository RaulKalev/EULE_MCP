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

    public event Action<bool>? StatusChanged;
    public bool IsRunning { get; private set; }

    public ConnectorService(PipeServer pipeServer, ExternalEventService eventService, ApprovalService? approvalService = null)
    {
        _pipeServer = pipeServer;
        _eventService = eventService;
        _approvalService = approvalService;
    }

    public void Start()
    {
        if (IsRunning) return;
        _pipeServer.Start();
        IsRunning = true;
        StatusChanged?.Invoke(true);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _pipeServer.Stop();
        IsRunning = false;
        StatusChanged?.Invoke(false);
    }

    public void PanicStop()
    {
        _approvalService?.RejectAll();
        _eventService.CancelAllPending("Request cancelled by Panic Stop.");
        _pipeServer.Stop();
        IsRunning = false;
        StatusChanged?.Invoke(false);
    }
}
