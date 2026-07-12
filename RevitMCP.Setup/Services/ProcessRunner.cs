using System.Diagnostics;

namespace RevitMCP.Setup.Services;

/// <summary>Runs console commands with output streamed back to the UI log.</summary>
public static class ProcessRunner
{
    public record RunResult(int ExitCode, string Output);

    /// <summary>
    /// Runs a command through cmd.exe (so .cmd/.ps1 shims like npm, claude, and winget
    /// resolve the same way they do in a user terminal) and captures all output.
    /// </summary>
    public static async Task<RunResult> RunAsync(string commandLine, Action<string>? onOutput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c " + commandLine,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();

        void Handle(string? line)
        {
            if (line == null) return;
            output.AppendLine(line);
            onOutput?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return new RunResult(process.ExitCode, output.ToString());
    }

    /// <summary>True when <paramref name="command"/> resolves on PATH (via where.exe).</summary>
    public static bool ExistsOnPath(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens a URL in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
