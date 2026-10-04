using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

public class RevitBusyDiagnosisTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ModalDialogWinsAndIsNamed()
    {
        var s = new RevitBusySnapshot
        {
            NowUtc = Now,
            MainWindowEnabled = false,
            ModalDialogTitle = "Synchronize with Central",
            ActiveTool = "revit_get_selected_elements",
            QueueLength = 2,
            LastExecuteUtc = Now.AddSeconds(-125)
        };

        Assert.Equal("modal_dialog", RevitBusyDiagnosis.Cause(s));
        var text = RevitBusyDiagnosis.Describe(s);
        Assert.Contains("'Synchronize with Central'", text);
        Assert.Contains("2 requests waiting", text);
        Assert.Contains("last processed a request 2 min ago", text);
        Assert.Contains("Close the dialog", RevitBusyDiagnosis.Hint(s));
    }

    [Fact]
    public void DisabledMainWindowWithoutTitleIsStillAModalDialog()
    {
        var s = new RevitBusySnapshot { NowUtc = Now, MainWindowEnabled = false };

        Assert.Equal("modal_dialog", RevitBusyDiagnosis.Cause(s));
        Assert.StartsWith("A modal dialog is open in Revit;", RevitBusyDiagnosis.Describe(s));
    }

    [Fact]
    public void RunningToolReportsItsDuration()
    {
        var s = new RevitBusySnapshot
        {
            NowUtc = Now,
            MainWindowEnabled = true,
            ActiveTool = "revit_run_clash_preset",
            ActiveSinceUtc = Now.AddSeconds(-42),
            QueueLength = 1,
            LastExecuteUtc = Now.AddSeconds(-42)
        };

        Assert.Equal("executing_tool", RevitBusyDiagnosis.Cause(s));
        Assert.Contains("still executing revit_run_clash_preset (running for 42 s)", RevitBusyDiagnosis.Describe(s));
        Assert.Contains("1 request waiting", RevitBusyDiagnosis.Describe(s));
    }

    [Fact]
    public void HungWindowMeansLongOperation()
    {
        var s = new RevitBusySnapshot { NowUtc = Now, MainWindowEnabled = true, MainWindowResponding = false, QueueLength = 1 };

        Assert.Equal("ui_not_responding", RevitBusyDiagnosis.Cause(s));
        Assert.Contains("not responding", RevitBusyDiagnosis.Describe(s));
    }

    [Fact]
    public void QueuedWorkOnAnIdleLookingRevitMeansActiveCommand()
    {
        var s = new RevitBusySnapshot { NowUtc = Now, MainWindowEnabled = true, MainWindowResponding = true, QueueLength = 1 };

        Assert.Equal("not_idle", RevitBusyDiagnosis.Cause(s));
        Assert.Contains("Esc", RevitBusyDiagnosis.Hint(s));
        Assert.Contains("has not processed a request since the connector started", RevitBusyDiagnosis.Describe(s));
    }

    [Fact]
    public void UnknownStateStillGivesASentence()
    {
        var s = new RevitBusySnapshot { NowUtc = Now };

        Assert.Equal("unknown", RevitBusyDiagnosis.Cause(s));
        Assert.EndsWith(".", RevitBusyDiagnosis.Describe(s));
    }

    [Fact]
    public void CancelBeforeStartAbandonsAQueuedItem()
    {
        var item = NewItem();

        Assert.True(item.TryCancelBeforeStart("busy", RevitBusyDiagnosis.BusyStatus, new { revitBusy = true }));

        Assert.False(item.MarkStarted()); // the Revit thread must skip it
        Assert.True(item.CancellationToken.IsCancellationRequested);
        var result = item.Completion.Task.Result;
        Assert.Equal(RevitBusyDiagnosis.BusyStatus, result.Status);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void CancelBeforeStartLeavesARunningItemAlone()
    {
        var item = NewItem();

        Assert.True(item.MarkStarted());
        Assert.True(item.IsStarted);
        Assert.False(item.TryCancelBeforeStart("busy", RevitBusyDiagnosis.BusyStatus));

        Assert.False(item.CancellationToken.IsCancellationRequested);
        Assert.False(item.Completion.Task.IsCompleted);
    }

    private static ExternalEventWorkItem NewItem() => new(
        new McpToolRequest { RequestId = Guid.NewGuid().ToString(), ToolName = "revit_get_selected_elements" },
        new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously));
}
