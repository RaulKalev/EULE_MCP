using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using RevitMCP.Addin.UI.Themes;
using RevitMCP.Addin.UI.ViewModels;

namespace RevitMCP.Addin.UI;

public partial class McpWindow : Window
{
    private static readonly string SuppressFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP", "direct_edit_suppress.flag");

    private static readonly string DefaultWindowStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP", "window-state.json");

    /// <summary>Redirects the window state file (bounds, appearance, detail level); used by the UI harness.</summary>
    public static string? WindowStatePathOverride { get; set; }

    private static string WindowStatePath => WindowStatePathOverride ?? DefaultWindowStatePath;

    private readonly ThemeManager _theme;
    private readonly JsonObject _savedState;
    private Action<bool>? _directEditCompleted;
    private IInputElement? _focusBeforeSheet;

    public McpWindowViewModel ViewModel { get; }

    /// <param name="revitIsDark">Revit's own theme; the window follows it until the user picks an appearance.</param>
    public McpWindow(McpWindowViewModel viewModel, bool revitIsDark = true)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.RequestDirectEditConfirmation = ShowDirectEditWarning;

        _savedState = LoadSavedState();
        _theme = new ThemeManager(this, ReadBool(_savedState, "DarkMode") ?? revitIsDark);
        _theme.Apply();
        UpdateThemeIcon();
        ViewModel.IsDebugMode = ReadBool(_savedState, "Debug") ?? false;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(McpWindowViewModel.IsDebugMode) && IsLoaded) SaveWindowState();
            else if (e.PropertyName == nameof(McpWindowViewModel.IsAgentActive)) UpdateActivityLine();
            else if (e.PropertyName == nameof(McpWindowViewModel.PendingCount)) ClosePeekIfDecided();
        };
        ViewModel.ApprovalArrived += OpenPeek;
        _theme.Applied += RefreshPaletteBindings;

        _peekTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = PeekDuration };
        _peekTimer.Tick += (_, _) => ClosePeek();

        StateChanged += (_, _) => UpdateMaximizedState();
        Closing += (_, _) => SaveWindowState();
    }

    public void HideAndSave()
    {
        SaveWindowState();
        Hide();
    }

    /// <summary>
    /// The ribbon command. A strip that is already showing expands; a hidden window comes back in the mode it was
    /// left in (a window closed while collapsed reopens collapsed).
    /// </summary>
    public void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        if (IsVisible && ViewModel.IsCompact)
            Expand();

        if (!IsVisible)
            Show();

        Activate();
    }

    public void SetRevitOwner(IntPtr revitHandle)
    {
        var helper = new WindowInteropHelper(this);
        helper.Owner = revitHandle;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RestoreWindowState();
    }

    // ── Caption ──────────────────────────────────────────────────────────────
    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _theme.SetDarkMode(!_theme.IsDarkMode);
        UpdateThemeIcon();
        SaveWindowState();
    }

    private void UpdateThemeIcon() =>
        ThemeIcon.Kind = _theme.IsDarkMode ? PackIconKind.WeatherNight : PackIconKind.WhiteBalanceSunny;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => Collapse();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideAndSave();

    private void UpdateMaximizedState()
    {
        // A maximized WindowChrome window extends past the work area by the resize frame; pad the content back in.
        var frame = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4)
            : new Thickness(0);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DirectEditSheet.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseDirectEditSheet(false);
                e.Handled = true;
            }
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.M)
        {
            if (ViewModel.IsCompact) Expand();
            else Collapse();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsCompact)
        {
            if (e.Key == Key.Escape)
            {
                Expand();
                e.Handled = true;
            }
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.D)
        {
            ViewModel.IsDebugMode = !ViewModel.IsDebugMode;
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var page = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 when ViewModel.IsDebugMode => McpWindowViewModel.ActivityTabIndex,
            _ => -1
        };
        if (page < 0) return;
        ViewModel.SelectedTabIndex = page;
        e.Handled = true;
    }

    // ── Compact status strip ─────────────────────────────────────────────────
    private const double CompactWidth = 280;
    private const double CompactHeight = 40;
    private static readonly TimeSpan PeekDuration = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _peekTimer;
    private WindowPlacement? _normalBounds;
    private ResizeMode _normalResizeMode = ResizeMode.CanResize;
    private double _normalMinWidth;
    private double _normalMinHeight;
    private bool _peekOpen;
    private double _topBeforePeek;
    private Point? _pressPoint;

    /// <summary>
    /// Minimize: shrink in place to the 40 px status strip. A Revit-owned window has no taskbar entry, so the native
    /// minimized state would leave an unreachable thumbnail; the strip keeps the state visible instead.
    /// </summary>
    public void Collapse()
    {
        if (ViewModel.IsCompact) return;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

        _normalBounds = CurrentBounds();
        _normalResizeMode = ResizeMode;
        _normalMinWidth = MinWidth;
        _normalMinHeight = MinHeight;

        MinWidth = 0;
        MinHeight = 0;
        ResizeMode = ResizeMode.NoResize;
        SetBounds(WindowPlacementMath.Compact(_normalBounds, AvailableArea(), CompactWidth, CompactHeight));
        ViewModel.IsCompact = true;
        UpdateActivityLine();
        if (IsLoaded) SaveWindowState();
    }

    /// <summary>Back to the full window at its saved size; its corner follows the strip if the strip was moved.</summary>
    public void Expand()
    {
        if (!ViewModel.IsCompact) return;
        HidePeek();

        ViewModel.IsCompact = false;
        ResizeMode = _normalResizeMode;
        MinWidth = _normalMinWidth;
        MinHeight = _normalMinHeight;

        var normal = _normalBounds ?? new WindowPlacement(Left, Top, 360, 620);
        SetBounds(WindowPlacementMath.Normalize(
            new WindowPlacement(Left, Top, normal.Width, normal.Height), AvailableArea(), MinWidth, MinHeight, 360, 620));
        _normalBounds = null;
        SaveWindowState();
    }

    private WindowPlacement CurrentBounds()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height)
            : RestoreBounds;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private void SetBounds(WindowPlacement bounds)
    {
        BeginAnimation(HeightProperty, null);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>The whole desktop across monitors, as for restoring saved bounds (a strip may sit on any monitor).</summary>
    private static WindowPlacement AvailableArea() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => Expand();

    // A press on the strip (not on a button: buttons handle their own presses) expands on release, unless the pointer
    // moved past the system drag threshold first; then it drags the window.
    private void CompactStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _pressPoint = e.GetPosition(this);

    private void CompactStrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressPoint = null;
        try { DragMove(); } catch (InvalidOperationException) { }
        SaveWindowState();
    }

    private void CompactStrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressPoint == null) return;
        _pressPoint = null;
        Expand();
    }

    private void CompactStrip_MouseEnter(object sender, MouseEventArgs e) => _peekTimer.Stop();

    private void CompactStrip_MouseLeave(object sender, MouseEventArgs e)
    {
        _pressPoint = null;
        if (_peekOpen) _peekTimer.Start();
    }

    /// <summary>
    /// A new request while collapsed: grow the strip to show it with Approve/Reject. Never activates the window, so
    /// Revit keeps the focus. A second request while open only replaces the content and restarts the timer.
    /// </summary>
    private void OpenPeek(PendingApprovalItem item)
    {
        if (!ViewModel.IsCompact || !IsVisible) return;
        PeekPanel.DataContext = item;
        if (!CompactStrip.IsMouseOver)
        {
            _peekTimer.Stop();
            _peekTimer.Start();
        }
        if (_peekOpen) return;

        _peekOpen = true;
        PeekPanel.Visibility = Visibility.Visible;
        PeekPanel.Measure(new Size(Width, double.PositiveInfinity));
        var peek = WindowPlacementMath.Compact(
            new WindowPlacement(Left, Top, Width, Height), AvailableArea(), Width, CompactHeight + PeekPanel.DesiredSize.Height);
        _topBeforePeek = Top;
        Top = peek.Top;
        Motion.Animate(this, HeightProperty, peek.Height);
        Motion.Play(PeekPanel, 4);
    }

    private void ClosePeek()
    {
        _peekTimer.Stop();
        if (!_peekOpen) return;
        _peekOpen = false;
        Motion.Animate(this, HeightProperty, CompactHeight, () =>
        {
            if (_peekOpen) return;   // reopened meanwhile
            PeekPanel.Visibility = Visibility.Collapsed;
            Top = _topBeforePeek;
        });
    }

    /// <summary>Closes the peek at once, without animation (expanding, or a decision made elsewhere).</summary>
    private void HidePeek()
    {
        _peekTimer.Stop();
        if (!_peekOpen) return;
        _peekOpen = false;
        PeekPanel.Visibility = Visibility.Collapsed;
        BeginAnimation(HeightProperty, null);
        Height = CompactHeight;
        Top = _topBeforePeek;
    }

    // Click runs before the button's command, which still gets its CommandParameter: the DataContext stays.
    private void PeekDecision_Click(object sender, RoutedEventArgs e) => HidePeek();

    private void ClosePeekIfDecided()
    {
        if (_peekOpen && PeekPanel.DataContext is PendingApprovalItem item &&
            ViewModel.PendingApprovals.All(p => p.ApprovalId != item.ApprovalId))
            ClosePeek();
    }

    /// <summary>The strip's thin line: fades in and pulses while the agent calls tools; static with animations off.</summary>
    private void UpdateActivityLine()
    {
        var active = ViewModel.IsAgentActive && ViewModel.IsCompact;
        var fade = TimeSpan.FromMilliseconds(ThemeManager.ReducedMotion ? 100 : 200);
        if (!active)
        {
            _pulsing = false;
            ActivityLine.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 0, Duration = fade }, HandoffBehavior.SnapshotAndReplace);
            return;
        }
        if (ThemeManager.ReducedMotion)
        {
            _pulsing = false;
            ActivityLine.BeginAnimation(OpacityProperty, new DoubleAnimation { To = 0.8, Duration = fade }, HandoffBehavior.SnapshotAndReplace);
            return;
        }
        if (_pulsing) return;   // already fading in or pulsing; a further tool call changes nothing
        _pulsing = true;
        var fadeIn = new DoubleAnimation { To = 0.3, Duration = fade };
        fadeIn.Completed += (_, _) =>
        {
            if (!_pulsing) return;
            ActivityLine.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.3,
                To = 0.8,
                Duration = TimeSpan.FromSeconds(1.2),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        };
        ActivityLine.BeginAnimation(OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);
    }

    private bool _pulsing;

    /// <summary>The status dot's brush comes from a converter, which does not follow a palette swap by itself.</summary>
    private void RefreshPaletteBindings()
    {
        BindingOperations.GetMultiBindingExpression(StatusDot, System.Windows.Shapes.Shape.FillProperty)?.UpdateTarget();
        if (!IsLoaded) return;
        _pulsing = false;   // the animation setting may have changed: restart the line in the right form
        UpdateActivityLine();
    }

    // ── Direct Edit confirmation (in-window sheet) ───────────────────────────
    private void ShowDirectEditWarning(Action<bool> completed)
    {
        if (File.Exists(SuppressFilePath))
        {
            completed(true);
            return;
        }

        if (_directEditCompleted != null) return;   // already asking

        _directEditCompleted = completed;
        _focusBeforeSheet = Keyboard.FocusedElement;
        DontAskAgainCheck.IsChecked = false;
        DirectEditSheet.Visibility = Visibility.Visible;
        Motion.Play(DirectEditCard, 10);
        DirectEditCancelButton.Focus();
    }

    private void DirectEditConfirm_Click(object sender, RoutedEventArgs e) => CloseDirectEditSheet(true);

    private void DirectEditCancel_Click(object sender, RoutedEventArgs e) => CloseDirectEditSheet(false);

    private void CloseDirectEditSheet(bool confirmed)
    {
        if (confirmed && DontAskAgainCheck.IsChecked == true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SuppressFilePath)!);
                File.WriteAllText(SuppressFilePath, string.Empty);
            }
            catch { }
        }

        DirectEditSheet.Visibility = Visibility.Collapsed;
        var completed = _directEditCompleted;
        _directEditCompleted = null;
        _focusBeforeSheet?.Focus();
        _focusBeforeSheet = null;
        completed?.Invoke(confirmed);
    }

    // ── Window state (bounds, appearance, detail level) ──────────────────────
    private static JsonObject LoadSavedState()
    {
        try
        {
            if (File.Exists(WindowStatePath) && JsonNode.Parse(File.ReadAllText(WindowStatePath)) is JsonObject state)
                return state;
        }
        catch { }
        return new JsonObject();
    }

    private static bool? ReadBool(JsonObject state, string key)
    {
        try { return state[key]?.GetValue<bool>(); }
        catch { return null; }
    }

    private void RestoreWindowState()
    {
        try
        {
            var state = _savedState.Deserialize<WindowPlacement>();
            if (state == null || !_savedState.ContainsKey(nameof(WindowPlacement.Width))) return;

            var availableArea = new WindowPlacement(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            var restored = WindowPlacementMath.Normalize(
                state,
                availableArea,
                MinWidth,
                MinHeight,
                Width,
                Height);

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = restored.Left;
            Top = restored.Top;
            Width = restored.Width;
            Height = restored.Height;
        }
        catch { }

        // Closed while collapsed: reopen collapsed (the bounds above are the full window's, at the strip's corner).
        if (ReadBool(_savedState, "Compact") == true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Collapse();
        }
    }

    private void SaveWindowState()
    {
        try
        {
            // Collapsed: the full window's size at the strip's corner, so expanding and reopening both restore it.
            var bounds = ViewModel.IsCompact && _normalBounds != null
                ? new Rect(Left, Top, _normalBounds.Width, _normalBounds.Height)
                : WindowState == WindowState.Normal
                    ? new Rect(Left, Top, ActualWidth, ActualHeight)
                    : RestoreBounds;
            var state = new JsonObject
            {
                [nameof(WindowPlacement.Left)] = bounds.Left,
                [nameof(WindowPlacement.Top)] = bounds.Top,
                [nameof(WindowPlacement.Width)] = bounds.Width,
                [nameof(WindowPlacement.Height)] = bounds.Height,
                ["DarkMode"] = _theme.IsDarkMode,
                ["Debug"] = ViewModel.IsDebugMode,
                ["Compact"] = ViewModel.IsCompact
            };
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStatePath)!);
            File.WriteAllText(WindowStatePath, state.ToJsonString());
        }
        catch { }
    }
}
