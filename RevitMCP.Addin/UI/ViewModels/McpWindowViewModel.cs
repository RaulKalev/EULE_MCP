using System.Windows;
using System.Windows.Input;
using RevitMCP.Addin.Services;

namespace RevitMCP.Addin.UI.ViewModels;

public class McpWindowViewModel : BaseViewModel
{
    private readonly ConnectorService _connector;

    private bool _isRunning;
    private string _statusText = "Stopped";
    private string _pipeName = "RKTools.RevitMCP.2026";
    private string _modelTitle = "No document open";
    private string _activeView = "—";
    private bool _isWorkshared;
    private string _revitUsername = "—";
    private int _selectedElementCount;
    private bool _isDarkTheme = true;

    public McpWindowViewModel(ConnectorService connector)
    {
        _connector = connector;
        _connector.StatusChanged += OnConnectorStatusChanged;

        StartCommand = new RelayCommand(_ => _connector.Start(), _ => !_isRunning);
        StopCommand = new RelayCommand(_ => _connector.Stop(), _ => _isRunning);
        PanicStopCommand = new RelayCommand(_ => _connector.PanicStop());
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
    }

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string PipeName { get => _pipeName; set => SetProperty(ref _pipeName, value); }
    public string ModelTitle { get => _modelTitle; set => SetProperty(ref _modelTitle, value); }
    public string ActiveView { get => _activeView; set => SetProperty(ref _activeView, value); }
    public bool IsWorkshared { get => _isWorkshared; set => SetProperty(ref _isWorkshared, value); }
    public string RevitUsername { get => _revitUsername; set => SetProperty(ref _revitUsername, value); }
    public int SelectedElementCount { get => _selectedElementCount; set => SetProperty(ref _selectedElementCount, value); }
    public bool IsDarkTheme { get => _isDarkTheme; private set => SetProperty(ref _isDarkTheme, value); }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PanicStopCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    private void OnConnectorStatusChanged(bool running)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsRunning = running;
            StatusText = running ? "Running" : "Stopped";
            CommandManager.InvalidateRequerySuggested();
        });
    }

    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        var themeName = IsDarkTheme ? "DarkTheme" : "LightTheme";
        var themeUri = new Uri($"pack://application:,,,/RevitMCP.Addin;component/UI/Themes/{themeName}.xaml");

        var resources = Application.Current.Resources.MergedDictionaries;
        var existing = resources.FirstOrDefault(d => d.Source?.OriginalString.Contains("Theme.xaml") == true);
        if (existing != null) resources.Remove(existing);
        resources.Add(new System.Windows.ResourceDictionary { Source = themeUri });
    }

    public void UpdateFromRevitContext(string modelTitle, string activeView, bool isWorkshared, string username, int selectedCount)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ModelTitle = modelTitle;
            ActiveView = activeView;
            IsWorkshared = isWorkshared;
            RevitUsername = username;
            SelectedElementCount = selectedCount;
        });
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
