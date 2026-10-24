using System.IO;
using RevitMCP.Addin.Services;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>The bridge hanging up after a response is a normal close, not a "Client handler error" in the activity log.</summary>
public class PipeHangupTests
{
    [Fact]
    public void BrokenPipe_AfterTheReadLoopEnded_IsAHangup()
    {
        Assert.True(PipeHangup.IsAfterClose(new IOException("Pipe is broken."), readLoopEnded: true));
        Assert.True(PipeHangup.IsAfterClose(new ObjectDisposedException("pipe"), readLoopEnded: true));
    }

    [Fact]
    public void BrokenPipe_WhileARequestIsHandled_StaysAnError()
    {
        Assert.False(PipeHangup.IsAfterClose(new IOException("Pipe is broken."), readLoopEnded: false));
        Assert.False(PipeHangup.IsAfterClose(new ObjectDisposedException("pipe"), readLoopEnded: false));
    }

    [Fact]
    public void OtherErrors_AfterTheReadLoopEnded_StayErrors()
    {
        Assert.False(PipeHangup.IsAfterClose(new InvalidOperationException("boom"), readLoopEnded: true));
    }
}
