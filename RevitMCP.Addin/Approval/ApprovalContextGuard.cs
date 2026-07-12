namespace RevitMCP.Addin.Approval;

/// <summary>
/// Validates that an approved request still targets the exact document instance
/// that was active when the approval prompt was created.
/// </summary>
public static class ApprovalContextGuard
{
    public static bool IsValid(bool isDocumentBound, object? expectedDocument, object? activeDocument)
    {
        if (!isDocumentBound)
            return true;

        return expectedDocument != null && ReferenceEquals(expectedDocument, activeDocument);
    }

    public static bool IsSelectionValid(
        bool isSelectionBound,
        IEnumerable<long> expectedSelection,
        IEnumerable<long> activeSelection)
    {
        if (!isSelectionBound)
            return true;

        return expectedSelection.OrderBy(id => id)
            .SequenceEqual(activeSelection.OrderBy(id => id));
    }
}
