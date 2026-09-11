using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using RevitMCP.Addin.UI.ViewModels;

namespace RevitMCP.Addin.Village.Hosting;

/// <summary>
/// Status-tab section for the Project Village: enable/disable (persisted), the localhost URL and
/// the viewer count. Refreshes on a UI timer; the village itself never calls back into the UI.
/// </summary>
public sealed class VillageStatusViewModel : BaseViewModel
{
    private readonly VillageService _service;
    private readonly DispatcherTimer _timer;
    private string _statusText = string.Empty;
    private string _toggleLabel = "Enable Village";
    private bool _isRunning;
    private string? _url;

    public VillageStatusViewModel(VillageService service)
    {
        _service = service;
        ToggleCommand = new RelayCommand(_ => Toggle());
        OpenCommand = new RelayCommand(_ => Open(), _ => _isRunning && _url != null);
        Refresh();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ToggleLabel { get => _toggleLabel; private set => SetProperty(ref _toggleLabel, value); }
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public string? Url { get => _url; private set => SetProperty(ref _url, value); }

    public ICommand ToggleCommand { get; }
    public ICommand OpenCommand { get; }

    public void Refresh()
    {
        try
        {
            IsRunning = _service.IsRunning;
            Url = _service.Url;
            ToggleLabel = IsRunning ? "Disable Village" : "Enable Village";

            if (IsRunning)
            {
                var viewers = _service.ViewerCount;
                StatusText = $"Listening at {Url} — {viewers} viewer{(viewers == 1 ? string.Empty : "s")} connected";
            }
            else if (_service.Options.Enabled)
            {
                StatusText = "Enabled but not listening" + (_service.LastError != null ? ": " + _service.LastError : ".");
            }
            else
            {
                StatusText = "Disabled (opt-in). Enabling starts a localhost-only viewer page.";
            }
            CommandManager.InvalidateRequerySuggested();
        }
        catch
        {
            StatusText = "Unavailable";
        }
    }

    private void Toggle()
    {
        try { _service.SetEnabled(!_service.IsRunning); }
        catch { /* surfaced through Refresh */ }
        Refresh();
    }

    private void Open()
    {
        var url = _service.Url;
        if (url == null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* browser launch failure is not fatal */ }
    }
}
