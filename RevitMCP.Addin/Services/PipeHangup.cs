using System.IO;

namespace RevitMCP.Addin.Services;

/// <summary>
/// Tells a client hanging up from a real pipe handler failure. The bridge closes its end after each response, so the
/// read loop ends normally and disposing the StreamWriter then flushes into a closed pipe ("Pipe is broken"). That is
/// a normal close and must not land in the activity log as an error on every call.
/// Pure and linked into the tests.
/// </summary>
public static class PipeHangup
{
    /// <summary>
    /// True when <paramref name="ex"/> is the I/O error of a pipe whose client already left: the read loop had ended
    /// (<paramref name="readLoopEnded"/>) and the error is an <see cref="IOException"/> or
    /// <see cref="ObjectDisposedException"/>. Errors while a request is still being read or answered stay failures.
    /// </summary>
    public static bool IsAfterClose(Exception ex, bool readLoopEnded) =>
        readLoopEnded && ex is IOException or ObjectDisposedException;
}
