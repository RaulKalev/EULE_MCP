using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Logging;

namespace RevitMCP.Addin.UI.ViewModels;

public class McpWindowViewModel : BaseViewModel
{
    private readonly ConnectorService _connector;
    private readonly ActivityLogger _logger;

    private bool _isRunning;
    private string _statusText = "Stopped";
    private string _pipeName = "RKTools.RevitMCP.2026";
    private string _modelTitle = "No document open";
    private string _activeView = "—";
    private bool _isWorkshared;
    private string _revitUsername = "—";
    private int _selectedElementCount;
    private bool _isDarkTheme = true;

    public McpWindowViewModel(ConnectorService connector, ActivityLogger logger)
    {
        _connector = connector;
        _logger = logger;

        _connector.StatusChanged += OnConnectorStatusChanged;
        _logger.EntryLogged += OnEntryLogged;

        ActivityLog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoActivity));

        StartCommand = new RelayCommand(_ => _connector.Start(), _ => !_isRunning);
        StopCommand = new RelayCommand(_ => _connector.Stop(), _ => _isRunning);
        PanicStopCommand = new RelayCommand(_ => _connector.PanicStop());
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        ClearActivityLogCommand = new RelayCommand(_ => ActivityLog.Clear());
    }

    // ── Status tab ────────────────────────────────────────────────────────────
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string PipeName { get => _pipeName; set => SetProperty(ref _pipeName, value); }
    public string ModelTitle { get => _modelTitle; set => SetProperty(ref _modelTitle, value); }
    public string ActiveView { get => _activeView; set => SetProperty(ref _activeView, value); }
    public bool IsWorkshared { get => _isWorkshared; set => SetProperty(ref _isWorkshared, value); }
    public string RevitUsername { get => _revitUsername; set => SetProperty(ref _revitUsername, value); }
    public int SelectedElementCount { get => _selectedElementCount; set => SetProperty(ref _selectedElementCount, value); }
    public bool IsDarkTheme { get => _isDarkTheme; private set => SetProperty(ref _isDarkTheme, value); }

    // ── Activity tab ──────────────────────────────────────────────────────────
    public ObservableCollection<ActivityLogItem> ActivityLog { get; } = [];
    public bool HasNoActivity => ActivityLog.Count == 0;

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PanicStopCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand ClearActivityLogCommand { get; }

    // ── Handlers ──────────────────────────────────────────────────────────────
    private void OnConnectorStatusChanged(bool running)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsRunning = running;
            StatusText = running ? "Running" : "Stopped";
            CommandManager.InvalidateRequerySuggested();
        });
    }

    private void OnEntryLogged(LogEntry entry)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ActivityLog.Insert(0, new ActivityLogItem
            {
                Time = entry.Timestamp.ToString("HH:mm:ss"),
                Tool = entry.Tool,
                Status = entry.Status,
                DurationMs = entry.DurationMs,
                Message = string.IsNullOrWhiteSpace(entry.Message) ? "—" : entry.Message,
                IsSuccess = entry.Status == "Success",
                IsError = entry.Status == "Failed"
            });

            // Keep the list bounded so long sessions don't leak memory.
            while (ActivityLog.Count > 200)
                ActivityLog.RemoveAt(ActivityLog.Count - 1);
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

    private void OpenLogFolder()
    {
        var dir = _logger.LogDirectory;
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
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
