using RevitMCP.Addin.Transactions;
using Xunit;

namespace RevitMCP.Tests;

public class TransactionSafetyCoreTests
{
    /// <summary>Scriptable fake of a Revit transaction that records every call.</summary>
    private sealed class FakeHandle : ITransactionHandle
    {
        public string Name => "Test Transaction";
        public TxStatus StartResult = TxStatus.Started;
        public Exception? StartThrows;
        public TxStatus CommitResult = TxStatus.Committed;
        public Exception? CommitThrows;
        public Exception? RollBackThrows;
        public List<string> FailureMessages = new();

        public List<string> Calls = new();
        private TxStatus _status = TxStatus.Uninitialized;

        public TxStatus Start()
        {
            Calls.Add("Start");
            if (StartThrows != null) throw StartThrows;
            _status = StartResult;
            return StartResult;
        }

        public TxStatus GetStatus() => _status;

        public TxStatus Commit()
        {
            Calls.Add("Commit");
            if (CommitThrows != null) { _status = TxStatus.Error; throw CommitThrows; }
            _status = CommitResult;
            return CommitResult;
        }

        public TxStatus RollBack()
        {
            Calls.Add("RollBack");
            if (RollBackThrows != null) throw RollBackThrows;
            _status = TxStatus.RolledBack;
            return TxStatus.RolledBack;
        }

        public IReadOnlyList<string> GetFailureMessages() => FailureMessages;
    }

    private static (bool Success, TransactionDiagnostics Diag, FakeHandle Handle) Run(
        FakeHandle handle, Action? body = null)
    {
        var (success, diag) = SafeTransactionCore.Run(
            handle, documentIsModifiable: false, documentIsReadOnly: false,
            mode: "Transaction", body ?? (() => { }));
        return (success, diag, handle);
    }

    // Test 1: a non-Started Start() preserves the real error and never runs the body.
    [Fact]
    public void Start_ReturningNonStarted_PreservesRealError_AndSkipsBody()
    {
        var handle = new FakeHandle { StartResult = TxStatus.Error };
        bool bodyRan = false;

        var (success, diag, _) = Run(handle, () => bodyRan = true);

        Assert.False(success);
        Assert.False(bodyRan);
        Assert.Equal("Error", diag.StartStatus);
        Assert.Contains("could not start", diag.OriginalError);
        Assert.Contains("Start() returned Error", diag.OriginalError);
        Assert.Null(diag.RollbackError);
    }

    // Test 2: RollBack is never called on a transaction that did not start.
    [Fact]
    public void RollBack_IsNeverCalled_WhenStartDidNotSucceed()
    {
        var handle = new FakeHandle { StartResult = TxStatus.Error };

        Run(handle);

        Assert.DoesNotContain("RollBack", handle.Calls);
        Assert.DoesNotContain("Commit", handle.Calls);
    }

    [Fact]
    public void RollBack_IsNeverCalled_WhenStartThrows()
    {
        var handle = new FakeHandle { StartThrows = new InvalidOperationException("cannot start here") };

        var (success, diag, _) = Run(handle);

        Assert.False(success);
        Assert.Equal("ExceptionOnStart", diag.StartStatus);
        Assert.Contains("cannot start here", diag.OriginalError);
        Assert.DoesNotContain("RollBack", handle.Calls);
    }

    // Test 3: a rollback failure cannot mask the original body exception.
    [Fact]
    public void RollBackFailure_CannotMask_OriginalBodyException()
    {
        var handle = new FakeHandle
        {
            RollBackThrows = new InvalidOperationException(
                "The transaction has not been started yet.")
        };

        var (success, diag, _) = Run(handle,
            () => throw new InvalidOperationException("ElectricalSystem.Create failed: the real cause"));

        Assert.False(success);
        Assert.Contains("the real cause", diag.OriginalError);
        Assert.NotNull(diag.RollbackError);
        Assert.Contains("has not been started", diag.RollbackError);
        // The rollback failure is reported separately, never as the original error.
        Assert.DoesNotContain("has not been started", diag.OriginalError);
    }

    [Fact]
    public void BodyException_RollsBack_StartedTransaction()
    {
        var handle = new FakeHandle();

        var (success, diag, _) = Run(handle, () => throw new Exception("boom"));

        Assert.False(success);
        Assert.Contains("boom", diag.OriginalError);
        Assert.Contains("RollBack", handle.Calls);
        Assert.Null(diag.RollbackError);
    }

    // Commit returning RolledBack (Revit failure processing) must not trigger a
    // second RollBack — that was the exact masking bug in production.
    [Fact]
    public void Commit_ReturningRolledBack_DoesNotRollBackAgain_AndReportsFailureMessages()
    {
        var handle = new FakeHandle
        {
            CommitResult = TxStatus.RolledBack,
            FailureMessages = { "[Error] Circuit cannot be created for the selected connector." }
        };

        var (success, diag, _) = Run(handle);

        Assert.False(success);
        Assert.DoesNotContain("RollBack", handle.Calls);
        Assert.Contains("did not commit", diag.OriginalError);
        Assert.Contains("Commit() returned RolledBack", diag.OriginalError);
        Assert.Contains(diag.FailureMessages, m => m.Contains("selected connector"));
        Assert.Null(diag.RollbackError);
    }

    [Fact]
    public void SuccessfulRun_Commits_AndReportsCommittedStatus()
    {
        var handle = new FakeHandle();

        var (success, diag, _) = Run(handle);

        Assert.True(success);
        Assert.Equal(new[] { "Start", "Commit" }, handle.Calls);
        Assert.Equal("Started", diag.StartStatus);
        Assert.Equal("Committed", diag.CommitStatus);
        Assert.Null(diag.OriginalError);
        Assert.Null(diag.RollbackError);
    }

    [Fact]
    public void CommitThrowing_PreservesCommitError_AndGuardsRollback()
    {
        var handle = new FakeHandle
        {
            CommitThrows = new InvalidOperationException("commit exploded"),
            RollBackThrows = new InvalidOperationException("rollback also exploded")
        };

        var (success, diag, _) = Run(handle);

        Assert.False(success);
        Assert.Contains("commit exploded", diag.OriginalError);
        // Status after throwing commit is Error (not Started) → no rollback attempted.
        Assert.DoesNotContain("RollBack", handle.Calls);
    }

    [Fact]
    public void DiagnosticsErrorLines_ContainDocumentState_AndKeepOriginalFirst()
    {
        var handle = new FakeHandle { RollBackThrows = new Exception("secondary") };
        var (_, diag) = SafeTransactionCore.Run(
            handle, documentIsModifiable: true, documentIsReadOnly: false,
            mode: "SubTransaction", () => throw new Exception("primary"));

        var lines = diag.ToErrorLines();
        Assert.Contains("primary", lines[0]);
        Assert.Contains(lines, l => l.Contains("doc.IsModifiable=True") && l.Contains("doc.IsReadOnly=False"));
        Assert.Contains(lines, l => l.Contains("secondary") && l.Contains("did not mask"));
    }
}
