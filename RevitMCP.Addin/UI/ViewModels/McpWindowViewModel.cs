using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Logging;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Logging;

namespace RevitMCP.Addin.UI.ViewModels;

public class McpWindowViewModel : BaseViewModel
{
    private readonly ConnectorService _connector;
    private readonly ActivityLogger _logger;
    private readonly ApprovalService? _approvalService;

    private bool _isRunning;
    private string _statusText = "Stopped";
    private string _pipeName = "RKTools.RevitMCP.2026";
    private bool _isActiveInstance;
    private string _modelTitle = "No document open";
    private string _activeView = "—";
    private bool _isWorkshared;
    private string _revitUsername = "—";
    private int _selectedElementCount;
    private bool _isDarkTheme = true;
    private string _pendingTabHeader = "PENDING";
    private bool _isDirectEditEnabled;
    private int _selectedTabIndex;

    public McpWindowViewModel(ConnectorService connector, ActivityLogger logger, ApprovalService? approvalService = null)
    {
        _connector = connector;
        _logger = logger;
        _approvalService = approvalService;

        _connector.StatusChanged += OnConnectorStatusChanged;
        _logger.EntryLogged += OnEntryLogged;

        if (_connector.PipeName != null)
            _pipeName = _connector.PipeName;
        _isActiveInstance = _connector.IsActiveInstance;

        ActivityLog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoActivity));
        PendingApprovals.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoPendingApprovals));
            PendingTabHeader = PendingApprovals.Count > 0
                ? $"PENDING ({PendingApprovals.Count})"
                : "PENDING";
        };

        if (_approvalService != null)
            _approvalService.PendingChanged += OnPendingChanged;

        StartCommand = new RelayCommand(_ => _connector.Start(), _ => !_isRunning);
        StopCommand = new RelayCommand(_ => _connector.Stop(), _ => _isRunning);
        PanicStopCommand = new RelayCommand(_ => _connector.PanicStop());
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        ClearActivityLogCommand = new RelayCommand(_ => ActivityLog.Clear());
        ApproveCommand = new RelayCommand(id => ApproveItem(id as string));
        RejectCommand = new RelayCommand(id => RejectItem(id as string));
        RejectAllCommand = new RelayCommand(_ => _approvalService?.RejectAll(), _ => PendingApprovals.Count > 0);
        ToggleDirectEditCommand = new RelayCommand(_ => ToggleDirectEdit());
        MakeActiveCommand = new RelayCommand(_ => MakeActive(), _ => !_isActiveInstance);
    }

    // ── Status tab ────────────────────────────────────────────────────────────
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string PipeName { get => _pipeName; set => SetProperty(ref _pipeName, value); }
    public bool IsActiveInstance { get => _isActiveInstance; private set => SetProperty(ref _isActiveInstance, value); }
    public string ModelTitle { get => _modelTitle; set => SetProperty(ref _modelTitle, value); }
    public string ActiveView { get => _activeView; set => SetProperty(ref _activeView, value); }
    public bool IsWorkshared { get => _isWorkshared; set => SetProperty(ref _isWorkshared, value); }
    public string RevitUsername { get => _revitUsername; set => SetProperty(ref _revitUsername, value); }
    public int SelectedElementCount { get => _selectedElementCount; set => SetProperty(ref _selectedElementCount, value); }
    public bool IsDarkTheme { get => _isDarkTheme; private set => SetProperty(ref _isDarkTheme, value); }

    // ── Activity tab ──────────────────────────────────────────────────────────
    public ObservableCollection<ActivityLogItem> ActivityLog { get; } = [];
    public bool HasNoActivity => ActivityLog.Count == 0;

    // ── Pending tab ──────────────────────────────────────────────────────────
    public ObservableCollection<PendingApprovalItem> PendingApprovals { get; } = [];
    public bool HasNoPendingApprovals => PendingApprovals.Count == 0;
    public string PendingTabHeader { get => _pendingTabHeader; private set => SetProperty(ref _pendingTabHeader, value); }
    public bool IsDirectEditEnabled { get => _isDirectEditEnabled; private set => SetProperty(ref _isDirectEditEnabled, value); }
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    public Action<Action<bool>>? RequestDirectEditConfirmation { get; set; }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PanicStopCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand ClearActivityLogCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand RejectAllCommand { get; }
    public ICommand ToggleDirectEditCommand { get; }
    public ICommand MakeActiveCommand { get; }

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

    // ── Pending approval handlers ────────────────────────────────────────────
    private void OnPendingChanged()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var previousCount = PendingApprovals.Count;
            PendingApprovals.Clear();
            if (_approvalService == null) return;

            foreach (var p in _approvalService.GetPending())
            {
                PendingApprovals.Add(new PendingApprovalItem
                {
                    ApprovalId = p.ApprovalId,
                    Time = p.CreatedAt.ToString("HH:mm:ss"),
                    Tool = p.ToolName,
                    Summary = p.Summary,
                    Client = p.ClientName,
                    Model = p.IsDocumentBound
                        ? p.OriginDocumentTitle + (p.IsSelectionBound ? $" ({p.OriginSelectionIds.Count} selected)" : string.Empty)
                        : "Not document-bound"
                });
            }

            // Auto-switch to PENDING tab when new approval requests arrive
            if (PendingApprovals.Count > previousCount)
                SelectedTabIndex = 1;

            CommandManager.InvalidateRequerySuggested();
        });
    }

    private void ApproveItem(string? approvalId)
    {
        if (approvalId != null)
            _approvalService?.Approve(approvalId);
    }

    private void RejectItem(string? approvalId)
    {
        if (approvalId != null)
            _approvalService?.Reject(approvalId);
    }

    private void ToggleDirectEdit()
    {
        if (!_isDirectEditEnabled)
        {
            if (RequestDirectEditConfirmation == null)
            {
                IsDirectEditEnabled = true;
                return;
            }

            RequestDirectEditConfirmation(confirmed =>
            {
                if (confirmed)
                    IsDirectEditEnabled = true;
            });
        }
        else
        {
            IsDirectEditEnabled = false;
        }
    }

    private void MakeActive()
    {
        _connector.MakeActive();
        IsActiveInstance = _connector.IsActiveInstance;
        CommandManager.InvalidateRequerySuggested();
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
            // Another Revit instance may have claimed the active marker in the meantime.
            IsActiveInstance = _connector.IsActiveInstance;
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
