namespace RevitMCP.Core.Models.Excel;

public class ExcelAppendRowsRequest
{
    public string FilePath { get; set; } = string.Empty;
    public string WorksheetName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public bool MatchHeaders { get; set; } = true;
    public List<Dictionary<string, string>> Rows { get; set; } = new();
    public bool BackupBeforeSave { get; set; } = true;
}
