using System.Windows.Input;
using RevitMCP.Setup.Services;

namespace RevitMCP.Setup.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly SetupSettings _settings;
    private readonly RevitAddinInstaller _addinInstaller = new();

    private string _packageRoot = "";
    private string _packageStatus = "";
    private string _revit2026Status = "";
    private string _revit2024Status = "";
    private string _logText = "";

    public MainViewModel()
    {
        _settings = SetupSettings.Load();
        _packageRoot = _settings.PackageRoot ?? "";

        Agents = AgentCatalog.All
            .Select(a => new AgentRowViewModel(a, Log, GetLayout, Refresh))
            .ToList();

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => GetLayout()?.IsValid == true);
        RefreshCommand = new RelayCommand(_ => Refresh());

        Refresh();
    }

    public List<AgentRowViewModel> Agents { get; }

    public string PackageRoot
    {
        get => _packageRoot;
        set
        {
            if (!SetProperty(ref _packageRoot, value)) return;
            _settings.PackageRoot = value;
            _settings.Save();
            Refresh();
        }
    }

    public string PackageStatus { get => _packageStatus; private set => SetProperty(ref _packageStatus, value); }
    public string Revit2026Status { get => _revit2026Status; private set => SetProperty(ref _revit2026Status, value); }
    public string Revit2024Status { get => _revit2024Status; private set => SetProperty(ref _revit2024Status, value); }
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }

    public ICommand ApplyCommand { get; }
    public ICommand RefreshCommand { get; }

    private PackageLayout? GetLayout() =>
        string.IsNullOrWhiteSpace(_packageRoot) ? null : new PackageLayout(_packageRoot);

    public void Refresh()
    {
        var layout = GetLayout();
        PackageStatus = layout?.Describe() ?? "No package folder selected.";

        if (layout != null && layout.IsValid)
        {
            Revit2026Status = DescribeManifest("2026", layout.Addin2026Dll);
            Revit2024Status = DescribeManifest("2024", layout.Addin2024Dll);
        }
        else
        {
            Revit2026Status = "—";
            Revit2024Status = "—";
        }

        foreach (var agent in Agents)
            agent.Refresh();
    }

    private string DescribeManifest(string year, string expectedDll)
    {
        var status = _addinInstaller.GetStatus(year, expectedDll);
        if (!status.Installed) return "Not installed";
        return status.PathMatches ? $"Installed ✓  ({status.Location})" : "Installed, but points at a different DLL — apply to fix";
    }

    private async Task ApplyAsync()
    {
        var layout = GetLayout();
        if (layout == null || !layout.IsValid)
        {
            Log("Select a valid package folder first.");
            return;
        }

        Log("── Applying ─────────────────────────────");

        // Revit manifests — always both versions, pointing at the package folder.
        foreach (var (year, dll) in new[] { ("2026", layout.Addin2026Dll), ("2024", layout.Addin2024Dll) })
        {
            try
            {
                var path = _addinInstaller.Install(year, dll);
                Log($"Revit {year} manifest written: {path}");
            }
            catch (Exception ex)
            {
                Log($"ERROR writing Revit {year} manifest: {ex.Message}");
            }
        }

        // Agent registrations — only the checked ones.
        foreach (var agent in Agents)
            await agent.ApplyRegistrationAsync();

        Log("Done. Restart Revit and your AI agent to pick up changes.");
        Refresh();
    }

    private void Log(string line)
    {
        // Property assignment is thread-safe for WPF scalar bindings.
        LogText += line + Environment.NewLine;
    }
}
