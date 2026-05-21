namespace RevitMCP.Addin.Services;

/// <summary>
/// Manages the lifecycle of the pipe server. Start/Stop/PanicStop are called from the WPF UI thread.
/// </summary>
public class ConnectorService
{
    private readonly PipeServer _pipeServer;
    private readonly ExternalEventService _eventService;

    public event Action<bool>? StatusChanged;
    public bool IsRunning { get; private set; }

    public ConnectorService(PipeServer pipeServer, ExternalEventService eventService)
    {
        _pipeServer = pipeServer;
        _eventService = eventService;
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
        _eventService.CancelAllPending("Request cancelled by Panic Stop.");
        _pipeServer.Stop();
        IsRunning = false;
        StatusChanged?.Invoke(false);
    }
}
