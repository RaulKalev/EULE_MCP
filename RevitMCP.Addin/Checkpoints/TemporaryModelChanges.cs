namespace RevitMCP.Addin.Checkpoints;

/// <summary>
/// Marks a stretch of a read-only tool in which it changes the model temporarily inside a TransactionGroup that it
/// always rolls back (revit_export_view_image: crop and highlight for the export, H1). Revit raises DocumentChanged for
/// the inner commit and for the group's rollback; while a scope is open App.OnDocumentChanged does not feed them to
/// the checkpoint, plan and graph trackers, which would otherwise record a net-zero change by a read-only tool.
/// API thread only, like the tool itself; scopes nest.
/// </summary>
public static class TemporaryModelChanges
{
    private static int _depth;

    /// <summary>True while a scope is open.</summary>
    public static bool Active => _depth > 0;

    /// <summary>Opens a scope; dispose it after the TransactionGroup was rolled back.</summary>
    public static IDisposable Begin()
    {
        _depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_depth > 0) _depth--;
        }
    }
}
