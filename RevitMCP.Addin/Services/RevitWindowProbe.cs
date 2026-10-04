using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Reads the state of Revit's main window with plain Win32 calls, so it is safe from the pipe thread
/// while the Revit API thread is blocked: whether a modal dialog disabled the window, which dialog it is,
/// and whether the window still answers messages. Every probe is best-effort and returns null on failure.
/// </summary>
internal static class RevitWindowProbe
{
    private const uint GwOwner = 4;
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ResponseTimeoutMs = 500;

    public static void Fill(RevitBusySnapshot snapshot)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var main = process.MainWindowHandle;
            if (main == IntPtr.Zero) return;

            snapshot.MainWindowEnabled = IsWindowEnabled(main);
            snapshot.MainWindowResponding =
                SendMessageTimeout(main, WmNull, UIntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, ResponseTimeoutMs, out _) != IntPtr.Zero;

            if (snapshot.MainWindowEnabled == false)
                snapshot.ModalDialogTitle = FindOwnedDialogTitle(main, (uint)process.Id);
        }
        catch
        {
            // Diagnostics only — never let a probe failure affect request handling.
        }
    }

    private static string? FindOwnedDialogTitle(IntPtr main, uint processId)
    {
        string? title = null;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != processId || hwnd == main || !IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd))
                return true;
            if (GetWindow(hwnd, GwOwner) != main)
                return true;

            var text = ReadTitle(hwnd);
            if (string.IsNullOrWhiteSpace(text))
                return true;

            title = text;
            return false;
        }, IntPtr.Zero);
        return title;
    }

    private static string ReadTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out UIntPtr result);
}
