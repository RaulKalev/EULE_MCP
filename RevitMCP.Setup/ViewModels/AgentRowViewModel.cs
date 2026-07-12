using System.Windows.Input;
using RevitMCP.Setup.Services;

namespace RevitMCP.Setup.ViewModels;

/// <summary>One row in the agents list: detection status, register checkbox, install buttons.</summary>
public class AgentRowViewModel : BaseViewModel
{
    private readonly IAgent _agent;
    private readonly Action<string> _log;
    private readonly Func<PackageLayout?> _layout;
    private readonly Action _refreshAll;

    private bool _registerChecked;
    private string _cliStatus = "…";
    private string _registrationStatus = "";
    private string _desktopStatus = "";
    private bool _cliInstalled;
    private bool _desktopInstalled;

    public AgentRowViewModel(IAgent agent, Action<string> log, Func<PackageLayout?> layout, Action refreshAll)
    {
        _agent = agent;
        _log = log;
        _layout = layout;
        _refreshAll = refreshAll;

        InstallCliCommand = new AsyncRelayCommand(InstallCliAsync, () => !_cliInstalled);
        InstallDesktopCommand = new AsyncRelayCommand(InstallDesktopAsync, () => HasDesktop && !_desktopInstalled);
    }

    public IAgent Agent => _agent;
    public string DisplayName => _agent.DisplayName;
    public bool HasDesktop => _agent.DesktopName != null;
    public string DesktopButtonText => $"Install {_agent.DesktopName}";

    public bool RegisterChecked { get => _registerChecked; set => SetProperty(ref _registerChecked, value); }
    public string CliStatus { get => _cliStatus; private set => SetProperty(ref _cliStatus, value); }
    public string RegistrationStatus { get => _registrationStatus; private set => SetProperty(ref _registrationStatus, value); }
    public string DesktopStatus { get => _desktopStatus; private set => SetProperty(ref _desktopStatus, value); }

    public ICommand InstallCliCommand { get; }
    public ICommand InstallDesktopCommand { get; }

    public void Refresh()
    {
        _cliInstalled = _agent.IsCliInstalled();
        _desktopInstalled = _agent.IsDesktopInstalled();
        CliStatus = _cliInstalled ? "CLI installed" : "CLI not found";
        DesktopStatus = !HasDesktop ? "" : _desktopInstalled ? $"{_agent.DesktopName} installed" : $"{_agent.DesktopName} not found";

        var layout = _layout();
        if (layout == null || !layout.HasBridge)
        {
            RegistrationStatus = "";
            return;
        }

        var state = _agent.GetRegistration(layout.BridgeExe);
        RegistrationStatus = state switch
        {
            RegistrationState.Registered => "Bridge registered ✓",
            RegistrationState.PathMismatch => "Registered to a different path — re-apply to fix",
            _ => "Bridge not registered"
        };

        // Pre-check agents that are already registered or whose CLI is present.
        if (state != RegistrationState.NotRegistered || _cliInstalled)
            RegisterChecked = true;
    }

    public async Task ApplyRegistrationAsync()
    {
        if (!RegisterChecked) return;
        var layout = _layout();
        if (layout == null || !layout.HasBridge) return;

        try
        {
            await _agent.RegisterAsync(layout.BridgeExe, _log);
        }
        catch (Exception ex)
        {
            _log($"ERROR registering {DisplayName}: {ex.Message}");
        }
    }

    private async Task InstallCliAsync()
    {
        _log($"Installing {DisplayName}…");
        try
        {
            await _agent.InstallCliAsync(_log);
        }
        catch (Exception ex)
        {
            _log($"ERROR installing {DisplayName}: {ex.Message}");
        }
        _refreshAll();
    }

    private async Task InstallDesktopAsync()
    {
        _log($"Installing {_agent.DesktopName}…");
        try
        {
            await _agent.InstallDesktopAsync(_log);
        }
        catch (Exception ex)
        {
            _log($"ERROR installing {_agent.DesktopName}: {ex.Message}");
        }
        _refreshAll();
    }
}
