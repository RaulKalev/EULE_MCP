using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using RevitMCP.Addin.UI.ViewModels;

namespace RevitMCP.Addin.UI;

public partial class McpWindow : Window
{
    private DirectEditWarningDialog? _directEditWarning;
    private static readonly string SuppressFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP", "direct_edit_suppress.flag");

    private static readonly string WindowStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCP", "window-state.json");

    public McpWindowViewModel ViewModel { get; }

    public McpWindow(McpWindowViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.RequestDirectEditConfirmation = ShowDirectEditWarning;
        Closing += (_, _) => SaveWindowState();
    }

    /// <summary>
    /// A Revit-owned, borderless window has no taskbar entry. Sending it to the native
    /// minimized state leaves an inaccessible desktop thumbnail, so minimize by hiding
    /// it and let the ribbon command restore the same window instance.
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

    // Enable edge/corner resize for WindowStyle=None + AllowsTransparency windows
    // by intercepting WM_NCHITTEST and returning the correct hit-test code.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ResizeHook);
        RestoreWindowState();
    }

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(WindowStatePath)) return;
            var state = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(WindowStatePath));
            if (state == null) return;

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
            var state = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStatePath)!);
            File.WriteAllText(WindowStatePath, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private IntPtr ResizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0084) // WM_NCHITTEST
        {
            try
            {
                int screenX = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                int screenY = unchecked((short)(lParam.ToInt32() >> 16));
                var src = HwndSource.FromHwnd(hwnd);
                if (src?.CompositionTarget == null) return IntPtr.Zero;
                var pt = src.CompositionTarget.TransformFromDevice.Transform(new Point(screenX, screenY));
                pt = new Point(pt.X - Left, pt.Y - Top);
                const double B = 6;
                double w = ActualWidth, h = ActualHeight;
                bool left = pt.X < B, right = pt.X > w - B;
                bool top  = pt.Y < B, bottom = pt.Y > h - B;
                if (top    && left)  { handled = true; return new IntPtr(13); } // HTTOPLEFT
                if (top    && right) { handled = true; return new IntPtr(14); } // HTTOPRIGHT
                if (bottom && left)  { handled = true; return new IntPtr(16); } // HTBOTTOMLEFT
                if (bottom && right) { handled = true; return new IntPtr(17); } // HTBOTTOMRIGHT
                if (left)            { handled = true; return new IntPtr(10); } // HTLEFT
                if (right)           { handled = true; return new IntPtr(11); } // HTRIGHT
                if (top)             { handled = true; return new IntPtr(12); } // HTTOP
                if (bottom)          { handled = true; return new IntPtr(15); } // HTBOTTOM
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private void ShowDirectEditWarning(Action<bool> completed)
    {
        if (File.Exists(SuppressFilePath))
        {
            completed(true);
            return;
        }

        if (_directEditWarning != null)
        {
            _directEditWarning.Activate();
            return;
        }

        var dialog = new DirectEditWarningDialog { Owner = this };
        _directEditWarning = dialog;
        dialog.Closed += (_, _) =>
        {
            var confirmed = dialog.IsConfirmed;
            if (confirmed && dialog.DontShowAgain)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SuppressFilePath)!);
                    File.WriteAllText(SuppressFilePath, string.Empty);
                }
                catch { }
            }

            _directEditWarning = null;
            completed(confirmed);
        };
        dialog.Show();
    }
}
