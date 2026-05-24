namespace RevitMCP.Core.Models.Excel;

public class ExcelInsertRowsRequest
{
    public string FilePath { get; set; } = string.Empty;
    public string WorksheetName { get; set; } = string.Empty;
    public int InsertAtRow { get; set; }
    public int CopyStyleFromRow { get; set; }
    public List<Dictionary<string, string>> Rows { get; set; } = new();
    public bool BackupBeforeSave { get; set; } = true;
}
