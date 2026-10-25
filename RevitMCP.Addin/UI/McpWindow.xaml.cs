using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
        };

        StateChanged += (_, _) => UpdateMaximizedState();
        Closing += (_, _) => SaveWindowState();
    }

    /// <summary>
    /// A Revit-owned window has no taskbar entry. Sending it to the native minimized state leaves an inaccessible
    /// desktop thumbnail, so minimize by hiding it and let the ribbon command restore the same window instance.
    /// </summary>
    public void MinimizeToRevit()
    {
        SaveWindowState();
        Hide();
    }

    public void HideAndSave()
    {
        SaveWindowState();
        Hide();
    }

    public void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

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

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => MinimizeToRevit();

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
    }

    private void SaveWindowState()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            var state = new JsonObject
            {
                [nameof(WindowPlacement.Left)] = bounds.Left,
                [nameof(WindowPlacement.Top)] = bounds.Top,
                [nameof(WindowPlacement.Width)] = bounds.Width,
                [nameof(WindowPlacement.Height)] = bounds.Height,
                ["DarkMode"] = _theme.IsDarkMode,
                ["Debug"] = ViewModel.IsDebugMode
            };
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStatePath)!);
            File.WriteAllText(WindowStatePath, state.ToJsonString());
        }
        catch { }
    }
}
