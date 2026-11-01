namespace RevitMCP.Addin.Approval;

/// <summary>
/// Validates that an approved request still targets the exact native document instance
/// that was active when the approval prompt was created.
/// </summary>
public static class ApprovalContextGuard
{
    public static bool IsValid(bool isDocumentBound, object? expectedDocument, object? activeDocument)
    {
        if (!isDocumentBound)
            return true;

        if (expectedDocument == null || activeDocument == null)
            return false;

        if (ReferenceEquals(expectedDocument, activeDocument))
            return true;

        try
        {
            // Revit may return a new managed Document wrapper for the same open native
            // document. Autodesk.Revit.DB.Document.Equals compares that native identity,
            // whereas ReferenceEquals incorrectly rejects the equivalent wrapper.
            return expectedDocument.Equals(activeDocument);
        }
        catch
        {
            // A closed/disposed document must never validate an approval.
            return false;
        }
    }

    /// <summary>
    /// Whether the model version still allows an approved request to run. A tool that re-reads its targets at run
    /// time (<c>IApprovalRevalidatesTargets</c>) does not depend on the model staying as it was (#138).
    /// </summary>
    public static bool IsModelVersionValid(bool revalidatesTargets, long expectedVersion, long currentVersion) =>
        revalidatesTargets || expectedVersion == currentVersion;

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
