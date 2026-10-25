using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
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

    // Captured on the Revit UI thread at construction. Revit 2024 hosts WPF without a
    // System.Windows.Application, so Application.Current is null there and updates routed
    // through it were silently dropped (status chip stuck on "Stopped", empty Activity and
    // Pending tabs, stale "Active" flag).
    private readonly Dispatcher _dispatcher;

    private bool _isRunning;
    private string _statusText = "Stopped";
    private string _pipeName = "RKTools.RevitMCP.2026";
    private bool _isActiveInstance;
    private string _modelTitle = "No document open";
    private string _activeView = "—";
    private bool _isWorkshared;
    private string _revitUsername = "—";
    private int _selectedElementCount;
    private bool _isDirectEditEnabled;
    private int _selectedTabIndex;

    public McpWindowViewModel(ConnectorService connector, ActivityLogger logger, ApprovalService? approvalService = null)
    {
        _connector = connector;
        _logger = logger;
        _approvalService = approvalService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _connector.StatusChanged += OnConnectorStatusChanged;
        _logger.EntryLogged += OnEntryLogged;

        if (_connector.PipeName != null)
            _pipeName = _connector.PipeName;
        _isActiveInstance = _connector.IsActiveInstance;

        ActivityLog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoActivity));
        PendingApprovals.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoPendingApprovals));
            OnPropertyChanged(nameof(PendingCount));
        };
        Plans.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPlans));
        };
        if (RevitMCP.Addin.Jobs.JobService.Current is { } jobService)
        {
            jobService.JobsChanged += () => RunOnUi(RefreshJobs);
            RunOnUi(RefreshJobs);   // journaled jobs of earlier sessions
        }
        if (RevitMCP.Addin.Plans.PlanService.Current is { } planService)
        {
            planService.Registry.Changed += () => RunOnUi(RefreshPlans);
            // Plans expire on time even when no call checks them.
            _planTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
            {
                planService.ExpireDue();
                RefreshPlans();
                RefreshJobs();   // elapsed time of running jobs
            }, _dispatcher);
            _planTimer.Start();
        }

        RecentApprovalOutcomes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentApprovalOutcomes));
        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoJobs));

        if (_approvalService != null)
        {
            _approvalService.PendingChanged += OnPendingChanged;
            _approvalService.OutcomeRecorded += _ => RunOnUi(RefreshRecentOutcomes);
        }

        StartCommand = new RelayCommand(_ => _connector.Start(), _ => !_isRunning);
        StopCommand = new RelayCommand(_ => _connector.Stop(), _ => _isRunning);
        PanicStopCommand = new RelayCommand(_ => _connector.PanicStop());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        UsageReportCommand = new RelayCommand(_ => RunUsageReport(), _ => !_usageReportRunning);
        DismissUsageReportMessageCommand = new RelayCommand(_ => UsageReportMessage = string.Empty);
        ClearActivityLogCommand = new RelayCommand(_ => ActivityLog.Clear());
        ApproveCommand = new RelayCommand(id => ApproveItem(id as string));
        RejectCommand = new RelayCommand(id => RejectItem(id as string));
        RejectAllCommand = new RelayCommand(_ => _approvalService?.RejectAll(), _ => PendingApprovals.Count > 0);
        RevokePlanCommand = new RelayCommand(id => RevokePlan(id as string));
        PauseJobCommand = new RelayCommand(id => RevitMCP.Addin.Jobs.JobService.Current?.Pause(id as string ?? string.Empty));
        CancelJobCommand = new RelayCommand(id => RevitMCP.Addin.Jobs.JobService.Current?.Cancel(id as string ?? string.Empty, "cancelled in the MCP window"));
        ResumeJobCommand = new RelayCommand(id => SendJobRequest("revit_job_resume", id as string, approved: true), _ => DispatchInternal != null);
        OpenJobReportCommand = new RelayCommand(id => OpenJobReport(id as string), _ => DispatchInternal != null);
        ShowPlanChangesCommand = new RelayCommand(id => ShowPlanChanges(id as string), _ => DispatchInternal != null);
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

    // ── Activity tab ──────────────────────────────────────────────────────────
    public ObservableCollection<ActivityLogItem> ActivityLog { get; } = [];
    public bool HasNoActivity => ActivityLog.Count == 0;

    // ── Pending tab ──────────────────────────────────────────────────────────
    public ObservableCollection<PendingApprovalItem> PendingApprovals { get; } = [];
    public bool HasNoPendingApprovals => PendingApprovals.Count == 0;

    /// <summary>Approval requests already decided (running, done, failed, rejected), newest first.</summary>
    public ObservableCollection<ApprovalOutcomeItem> RecentApprovalOutcomes { get; } = [];
    public bool HasRecentApprovalOutcomes => RecentApprovalOutcomes.Count > 0;
    private const int RecentOutcomeCount = 10;
    public int PendingCount => PendingApprovals.Count;

    /// <summary>Jobs (#100): this session's and journaled ones, newest first.</summary>
    public ObservableCollection<JobItem> Jobs { get; } = [];
    public bool HasNoJobs => Jobs.Count == 0;
    private int _runningJobCount;
    public int RunningJobCount { get => _runningJobCount; private set { if (SetProperty(ref _runningJobCount, value)) OnPropertyChanged(nameof(HasRunningJobs)); } }
    public bool HasRunningJobs => _runningJobCount > 0;

    /// <summary>Plans of this session (#99), newest first; active and exhausted ones can be revoked.</summary>
    public ObservableCollection<PlanItem> Plans { get; } = [];
    public bool HasPlans => Plans.Count > 0;
    private readonly DispatcherTimer? _planTimer;

    /// <summary>Sends an internal request to the Revit API thread (wired in App); used by "Show changes".</summary>
    public Func<RevitMCP.Core.Models.McpToolRequest, Task<RevitMCP.Core.Models.McpToolResult>>? DispatchInternal { get; set; }
    public bool IsDirectEditEnabled { get => _isDirectEditEnabled; private set => SetProperty(ref _isDirectEditEnabled, value); }
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }

    private bool _isDebugMode;

    /// <summary>
    /// Technical details (pipe, view, routing, Direct Edit, plan limits, the activity log). Off by default: what a
    /// normal user needs is the connector state, the model and what waits for approval. The window persists it.
    /// </summary>
    public bool IsDebugMode
    {
        get => _isDebugMode;
        set
        {
            if (!SetProperty(ref _isDebugMode, value)) return;
            if (!value && SelectedTabIndex == ActivityTabIndex) SelectedTabIndex = 0;
        }
    }

    public const int ActivityTabIndex = 3;
    public Action<Action<bool>>? RequestDirectEditConfirmation { get; set; }

    // ── Project Village (read-only visualizer; null when unavailable) ─────────
    private Village.Hosting.VillageStatusViewModel? _village;
    public Village.Hosting.VillageStatusViewModel? Village
    {
        get => _village;
        set { if (SetProperty(ref _village, value)) OnPropertyChanged(nameof(HasVillage)); }
    }
    public bool HasVillage => _village != null;

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PanicStopCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand UsageReportCommand { get; }
    public ICommand DismissUsageReportMessageCommand { get; }
    public ICommand ClearActivityLogCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand RejectAllCommand { get; }
    public ICommand RevokePlanCommand { get; }
    public ICommand PauseJobCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand ResumeJobCommand { get; }
    public ICommand OpenJobReportCommand { get; }
    public ICommand ShowPlanChangesCommand { get; }
    public ICommand ToggleDirectEditCommand { get; }
    public ICommand MakeActiveCommand { get; }

    // ── Handlers ──────────────────────────────────────────────────────────────
    private void OnConnectorStatusChanged(bool running)
    {
        RunOnUi(() =>
        {
            IsRunning = running;
            StatusText = running ? "Running" : "Stopped";
            CommandManager.InvalidateRequerySuggested();
        });
    }

    /// <summary>Runs inline on the UI thread; from pipe/worker threads, queues without blocking.</summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }

    private void OnEntryLogged(LogEntry entry)
    {
        RunOnUi(() =>
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
        RunOnUi(() =>
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
                    Client = ClientLabel(p.ClientName),
                    Model = p.IsDocumentBound
                        ? p.OriginDocumentTitle + (p.IsSelectionBound ? $" ({p.OriginSelectionIds.Count} selected)" : string.Empty)
                        : "Not document-bound"
                });
            }

            // Auto-switch to PENDING tab when new approval requests arrive
            if (PendingApprovals.Count > previousCount)
                SelectedTabIndex = 1;

            RefreshRecentOutcomes();
            CommandManager.InvalidateRequerySuggested();
        });
    }

    /// <summary>Rebuilds "Recent decisions" from the approval history. Must run on the UI thread.</summary>
    private void RefreshRecentOutcomes()
    {
        RecentApprovalOutcomes.Clear();
        if (_approvalService == null) return;

        // Over-fetch: pending entries are shown above and skipped here.
        foreach (var o in _approvalService.GetRecentOutcomes(RecentOutcomeCount * 3)
                     .Where(o => o.State != ApprovalOutcome.Pending)
                     .Take(RecentOutcomeCount))
        {
            var failed = o.State == ApprovalOutcome.Failed;
            RecentApprovalOutcomes.Add(new ApprovalOutcomeItem
            {
                Time = (o.CompletedAt ?? o.DecidedAt ?? o.CreatedAt).ToString("HH:mm:ss"),
                Tool = o.ToolName,
                Summary = o.Summary,
                StateLabel = o.State switch
                {
                    ApprovalOutcome.Running => "RUNNING",
                    ApprovalOutcome.Succeeded => "DONE",
                    ApprovalOutcome.Rejected => "REJECTED",
                    _ => "FAILED"
                },
                // Successful results are in the activity log; show the message only where it explains something.
                Message = o.State == ApprovalOutcome.Succeeded || o.State == ApprovalOutcome.Rejected
                    ? string.Empty
                    : o.Result?.Message ?? string.Empty,
                IsSucceeded = o.State == ApprovalOutcome.Succeeded,
                IsFailed = failed
            });
        }
    }

    // ── Plans (#99) ──────────────────────────────────────────────────────────
    /// <summary>Rebuilds the Plans section from the registry. Must run on the UI thread.</summary>
    private void RefreshPlans()
    {
        var service = RevitMCP.Addin.Plans.PlanService.Current;
        Plans.Clear();
        if (service == null) return;
        var now = DateTimeOffset.Now;
        foreach (var plan in service.Registry.All().Take(20))
        {
            var env = plan.Envelope;
            var parts = new List<string>
            {
                $"{plan.Calls}/{env.MaxCalls} calls",
                $"{plan.Touched.Count}/{env.MaxElementsTotal} elements"
            };
            if (plan.Violations.Count > 0) parts.Add($"{plan.Violations.Count} not covered");
            if (!plan.IsEnded && plan.ExpiresAt is { } at) parts.Add($"expires in {Math.Max(0, (int)Math.Ceiling((at - now).TotalMinutes))} min");
            Plans.Add(new PlanItem
            {
                PlanId = plan.Id,
                Title = plan.Title,
                Client = plan.ClientName,
                Document = plan.DocumentTitle,
                StateLabel = plan.State.ToString().ToUpperInvariant(),
                IsActive = plan.IsActive,
                CanRevoke = !plan.IsEnded,
                Progress = string.Join(" · ", parts),
                StateReason = plan.StateReason ?? string.Empty
            });
        }
        CommandManager.InvalidateRequerySuggested();
    }

    // ── Jobs (#100) ──────────────────────────────────────────────────────────
    /// <summary>Rebuilds the JOBS tab from the job service. Must run on the UI thread.</summary>
    private void RefreshJobs()
    {
        var service = RevitMCP.Addin.Jobs.JobService.Current;
        Jobs.Clear();
        if (service == null) return;
        var now = DateTimeOffset.Now;
        foreach (var job in service.All().Take(30))
        {
            var elapsed = job.ElapsedMs + (job.SegmentStartedAt is { } since ? (long)(now - since).TotalMilliseconds : 0);
            Jobs.Add(new JobItem
            {
                JobId = job.Id,
                Title = job.Title,
                Client = job.ClientName,
                Document = job.DocumentTitle,
                StateLabel = job.State == RevitMCP.Addin.Jobs.JobState.WaitingApproval ? "WAITING" : job.State.ToString().ToUpperInvariant(),
                IsRunning = job.State is RevitMCP.Addin.Jobs.JobState.Running or RevitMCP.Addin.Jobs.JobState.WaitingApproval,
                IsBad = job.State is RevitMCP.Addin.Jobs.JobState.Failed or RevitMCP.Addin.Jobs.JobState.Suspended,
                Progress = $"{job.DoneCount} / {job.Steps.Count} · {Math.Round(elapsed / 60000.0, 1)} min",
                Done = job.DoneCount,
                Total = job.Steps.Count,
                StopReason = job.StopReason ?? string.Empty,
                CanPause = job.State is RevitMCP.Addin.Jobs.JobState.Running or RevitMCP.Addin.Jobs.JobState.WaitingApproval,
                CanResume = job.IsResumable,
                CanCancel = !job.IsTerminal,
                HasReport = job.ReportUrl != null || job.CheckpointId != null
            });
        }
        RunningJobCount = Jobs.Count(j => j.IsRunning);
        OnPropertyChanged(nameof(HasNoJobs));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Sends a job request to the Revit API thread. A resume from this window is the user's own decision, so it
    /// goes in approved.
    /// </summary>
    private async void SendJobRequest(string tool, string? jobId, bool approved, bool open = false)
    {
        if (jobId == null || DispatchInternal == null) return;
        try
        {
            var arguments = new Dictionary<string, object?> { ["jobId"] = jobId };
            if (open) arguments["open"] = true;
            await DispatchInternal(new RevitMCP.Core.Models.McpToolRequest
            {
                ToolName = tool,
                ClientName = "MCP window",
                IsApproved = approved,
                Arguments = arguments
            });
        }
        catch
        {
            // A busy Revit answers later or not at all; the job list shows the outcome.
        }
    }

    private void OpenJobReport(string? jobId)
    {
        var url = RevitMCP.Addin.Jobs.JobService.Current?.Get(jobId)?.ReportUrl;
        if (url != null)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return; }
            catch { }
        }
        // No report yet (or it moved): publish it now and open it.
        SendJobRequest(RevitMCP.Addin.Jobs.JobService.ReportTool, jobId, approved: false, open: true);
    }

    /// <summary>"Job 'Tag sockets' (job-3fa9c1)" for a job step's approval, the client name otherwise.</summary>
    private static string ClientLabel(string client)
    {
        var jobId = RevitMCP.Addin.Jobs.JobIds.JobIdOfClient(client);
        var job = jobId == null ? null : RevitMCP.Addin.Jobs.JobService.Current?.Get(jobId);
        return job == null ? client : $"Job '{job.Title}' ({job.Id})";
    }

    private void RevokePlan(string? planId)
    {
        var service = RevitMCP.Addin.Plans.PlanService.Current;
        var plan = service?.Registry.Get(planId);
        if (plan != null) service!.Finish(plan, RevitMCP.Addin.Plans.PlanState.Revoked, "revoked in the MCP window");
    }

    private async void ShowPlanChanges(string? planId)
    {
        if (planId == null || DispatchInternal == null) return;
        try
        {
            await DispatchInternal(new RevitMCP.Core.Models.McpToolRequest
            {
                ToolName = RevitMCP.Addin.Tools.Plans.PlanShowChangesTool.ToolName,
                ClientName = "MCP window",
                Arguments = new Dictionary<string, object?> { ["planId"] = planId }
            });
        }
        catch
        {
            // Selecting is a convenience; a busy Revit simply does not select.
        }
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

    /// <summary>
    /// Starts in Direct Edit without the confirmation prompt; used when the user opted in through
    /// <c>approval.directEditByDefault</c> in their own config (see <see cref="Approval.DirectEditDefault"/>).
    /// </summary>
    public void EnableDirectEditByDefault() => IsDirectEditEnabled = true;

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

    private bool _usageReportRunning;
    private string _usageReportMessage = string.Empty;

    /// <summary>Why the last usage report did not open (shown inline on the Activity page instead of a message box).</summary>
    public string UsageReportMessage
    {
        get => _usageReportMessage;
        private set { if (SetProperty(ref _usageReportMessage, value)) OnPropertyChanged(nameof(HasUsageReportMessage)); }
    }
    public bool HasUsageReportMessage => _usageReportMessage.Length > 0;

    /// <summary>
    /// The monthly usage report for the previous month (docs/usage-reporting.md) — the same code as revit_usage_report,
    /// run off the UI thread; opens the page or file when done.
    /// </summary>
    private void RunUsageReport()
    {
        _usageReportRunning = true;
        UsageReportMessage = string.Empty;
        CommandManager.InvalidateRequerySuggested();
        Task.Run(() =>
        {
            string? open = null;
            string message;
            try
            {
                var (result, url) = RevitMCP.Addin.Tools.Usage.UsageReportService.Run(null, null, RevitMCP.Addin.Usage.UsageReportRunner.FormatBoth, "Connector window");
                message = result.Message;
                if (result.Success) open = url ?? result.HtmlPath;
            }
            catch (Exception ex)
            {
                message = "The usage report failed: " + ex.Message;
            }
            RunOnUi(() =>
            {
                _usageReportRunning = false;
                CommandManager.InvalidateRequerySuggested();
                if (open != null) RevitMCP.Addin.Tools.Usage.UsageReportService.Open(open);
                else UsageReportMessage = message;
            });
        });
    }

    private void OpenLogFolder()
    {
        var dir = _logger.LogDirectory;
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    public void UpdateFromRevitContext(string modelTitle, string activeView, bool isWorkshared, string username, int selectedCount)
    {
        RunOnUi(() =>
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
