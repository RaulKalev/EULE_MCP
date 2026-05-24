using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Delivery;

/// <summary>
/// Builds <see cref="IssueDto"/> entries for delivery QA checks.
/// </summary>
public static class DeliveryIssueBuilder
{
    public const string Category = "Delivery";

    public static IssueReportDto BuildReport(
        string runId,
        string title,
        IEnumerable<IssueDto> issues)
    {
        return new IssueReportDto
        {
            RunId = runId,
            Title = title,
            SourceTools = new List<string> { "delivery_check" },
            CreatedAt = DateTimeOffset.UtcNow,
            Issues = issues.ToList()
        };
    }

    // ── Missing file issues ────────────────────────────────────────────────────

    public static IssueDto MissingPdf(string runId, int seq, string sheetNumber, string? sheetName)
        => Make(runId, seq, IssueSeverity.Error, "MissingPDF",
            $"PDF missing for sheet {sheetNumber}",
            $"Sheet '{sheetNumber}{(sheetName != null ? " — " + sheetName : "")}' exists in Revit but no PDF was found in the delivery folder.",
            $"Export sheet {sheetNumber} to PDF and place it in the delivery folder.",
            sheetNumber: sheetNumber);

    public static IssueDto MissingDwg(string runId, int seq, string sheetNumber, string? sheetName)
        => Make(runId, seq, IssueSeverity.Warning, "MissingDWG",
            $"DWG missing for sheet {sheetNumber}",
            $"Sheet '{sheetNumber}{(sheetName != null ? " — " + sheetName : "")}' exists in Revit but no DWG was found in the delivery folder.",
            $"Export sheet {sheetNumber} to DWG and place it in the delivery folder.",
            sheetNumber: sheetNumber);

    public static IssueDto OrphanFile(string runId, int seq, string fileName, string extension)
        => Make(runId, seq, IssueSeverity.Warning, "OrphanFile",
            $"File has no matching Revit sheet: {fileName}",
            $"The {extension.ToUpperInvariant()} file '{fileName}' was found in the delivery folder but no matching Revit sheet exists.",
            "Remove the file from the delivery folder or add the corresponding sheet to the Revit model.",
            filePath: fileName);

    public static IssueDto StageMismatch(string runId, int seq, string fileName, string fileStage, string? expectedStage)
        => Make(runId, seq, IssueSeverity.Warning, "StageMismatch",
            $"Stage mismatch: {fileName}",
            $"File stage '{fileStage}' does not match the expected stage '{expectedStage ?? "unknown"}'.",
            "Verify the stage token in the file name is correct.",
            filePath: fileName);

    public static IssueDto DisciplineMismatch(string runId, int seq, string fileName, string fileDiscipline, string sheetDiscipline)
        => Make(runId, seq, IssueSeverity.Warning, "DisciplineMismatch",
            $"Discipline mismatch: {fileName}",
            $"File discipline '{fileDiscipline}' does not match sheet discipline '{sheetDiscipline}'.",
            "Check the discipline token in the file name.",
            filePath: fileName);

    public static IssueDto DuplicateFile(string runId, int seq, string sheetNumber, string extension, IEnumerable<string> paths)
        => Make(runId, seq, IssueSeverity.Warning, "DuplicateFile",
            $"Duplicate {extension.ToUpperInvariant()} for sheet {sheetNumber}",
            $"Multiple {extension.ToUpperInvariant()} files found for sheet '{sheetNumber}': {string.Join(", ", paths)}",
            "Keep only the latest version and remove duplicates.",
            sheetNumber: sheetNumber);

    public static IssueDto EmptyFile(string runId, int seq, string fileName)
        => Make(runId, seq, IssueSeverity.Error, "EmptyFile",
            $"Zero-byte file: {fileName}",
            $"The file '{fileName}' has zero bytes and is likely corrupt or incomplete.",
            "Re-export the file from Revit.",
            filePath: fileName);

    public static IssueDto SmallFile(string runId, int seq, string fileName, long sizeBytes)
        => Make(runId, seq, IssueSeverity.Warning, "SuspiciouslySmallFile",
            $"Suspiciously small file: {fileName}",
            $"The file '{fileName}' is only {sizeBytes:N0} bytes, which may indicate an incomplete export.",
            "Verify the file is complete.",
            filePath: fileName);

    // ── Excel register issues ─────────────────────────────────────────────────

    public static IssueDto RegisterRowMissingFile(string runId, int seq, string sheetNumber, string extension)
        => Make(runId, seq, IssueSeverity.Error, "RegisterRowMissingFile",
            $"Register row has no matching file: {sheetNumber}",
            $"The document register has a row for sheet '{sheetNumber}' but no matching {extension.ToUpperInvariant()} file was found.",
            "Export the file and add it to the delivery folder.",
            sheetNumber: sheetNumber);

    public static IssueDto FileMissingFromRegister(string runId, int seq, string fileName)
        => Make(runId, seq, IssueSeverity.Warning, "FileMissingFromRegister",
            $"File not in register: {fileName}",
            $"File '{fileName}' was found in the delivery folder but has no corresponding row in the Excel register.",
            "Add the document to the register or remove the file if it is not part of this delivery.",
            filePath: fileName);

    public static IssueDto RegisterDuplicateDocNumber(string runId, int seq, string docNumber, int count)
        => Make(runId, seq, IssueSeverity.Error, "RegisterDuplicateDocNumber",
            $"Duplicate document number in register: {docNumber}",
            $"Document number '{docNumber}' appears {count} times in the Excel register.",
            "Remove duplicate rows from the register.");

    // ── Folder / structural policy issues ─────────────────────────────────────

    public static IssueDto MissingRequiredFolder(string runId, int seq, string folderName)
        => Make(runId, seq, IssueSeverity.Warning, "FolderStructure",
            $"Required folder missing: {folderName}",
            $"The required sub-folder '{folderName}' was not found inside the delivery folder.",
            "Create the folder and move the relevant files into it.",
            filePath: folderName);

    // ── Temp / backup file issues ──────────────────────────────────────────────

    public static IssueDto TempFile(string runId, int seq, string fileName)
        => Make(runId, seq, IssueSeverity.Warning, "TempFiles",
            $"Temporary or lock file: {fileName}",
            $"File '{fileName}' appears to be a temporary, lock, or backup file and should not be in the delivery package.",
            "Delete or move the file out of the delivery folder.",
            filePath: fileName);

    // ── Suspicious extension issues ────────────────────────────────────────────

    public static IssueDto SuspiciousExtension(string runId, int seq, string fileName, string extension)
        => Make(runId, seq, IssueSeverity.Warning, "SuspiciousFile",
            $"Unexpected file extension: {fileName}",
            $"File '{fileName}' has extension '{extension}' which is not an expected delivery file type.",
            "Verify that this file is intentional, or remove it from the delivery folder.",
            filePath: fileName);

    // ── Revision issues ────────────────────────────────────────────────────────

    public static IssueDto OldRevision(string runId, int seq, string fileName, string sheetNumber, string revision)
        => Make(runId, seq, IssueSeverity.Warning, "Revision",
            $"Old revision file: {fileName}",
            $"File '{fileName}' for sheet '{sheetNumber}' appears to be an older revision ({revision}). A newer revision was also found in the delivery folder.",
            "Remove the old revision file and keep only the latest.",
            sheetNumber: sheetNumber, filePath: fileName);

    // ── Required project file issues ──────────────────────────────────────────

    public static IssueDto MissingProjectFile(string runId, int seq, string extension)
        => Make(runId, seq, IssueSeverity.Warning, "ProjectFiles",
            $"No {extension.ToUpperInvariant()} project file found",
            $"No '{extension}' file was found in the delivery folder. The delivery policy requires at least one project file with this extension.",
            $"Add the required {extension.ToUpperInvariant()} project file to the delivery folder.");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IssueDto Make(
        string runId,
        int seq,
        IssueSeverity severity,
        string subCategory,
        string title,
        string description,
        string? suggestedFix,
        string? sheetNumber = null,
        string? filePath = null,
        string? discipline = null) =>
        new IssueDto
        {
            IssueId = $"{runId}-{seq:D4}",
            RunId = runId,
            SourceTool = "delivery_check",
            Severity = severity,
            Status = IssueStatus.Open,
            Category = $"{Category}.{subCategory}",
            Title = title,
            Description = description,
            SuggestedFix = suggestedFix,
            SheetNumber = sheetNumber,
            FilePath = filePath,
            Discipline = discipline,
            CreatedAt = DateTimeOffset.UtcNow
        };
}
