namespace RevitMCP.Core.Models.Excel;

public class ExcelWorkbookInspectionDto
{
    public string FilePath { get; set; } = string.Empty;
    public List<ExcelWorksheetInfoDto> Worksheets { get; set; } = new();
}

public class ExcelWorksheetInfoDto
{
    public string Name { get; set; } = string.Empty;
    public string UsedRange { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public List<string> Headers { get; set; } = new();
    public List<List<string>> PreviewRows { get; set; } = new();
}
