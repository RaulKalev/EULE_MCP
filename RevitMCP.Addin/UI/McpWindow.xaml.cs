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
            var state = JsonSerializer.Deserialize<SavedWindowState>(File.ReadAllText(WindowStatePath));
            if (state == null) return;

            // Clamp so the window stays reachable even if the screen layout changed
            double virtLeft   = SystemParameters.VirtualScreenLeft;
            double virtTop    = SystemParameters.VirtualScreenTop;
            double virtRight  = virtLeft + SystemParameters.VirtualScreenWidth;
            double virtBottom = virtTop  + SystemParameters.VirtualScreenHeight;

            Left   = Math.Max(virtLeft, Math.Min(state.Left, virtRight  - 100));
            Top    = Math.Max(virtTop,  Math.Min(state.Top,  virtBottom - 100));
            Width  = Math.Max(MinWidth,  state.Width);
            Height = Math.Max(MinHeight, state.Height);
        }
        catch { }
    }

    private void SaveWindowState()
    {
        try
        {
            var state = new SavedWindowState(Left, Top, Width, Height);
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStatePath)!);
            File.WriteAllText(WindowStatePath, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private sealed record SavedWindowState(double Left, double Top, double Width, double Height);

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
