using ClosedXML.Excel;
using RevitMCP.Addin.Excel;
using Xunit;

namespace RevitMCP.Tests;

public class ExcelHeaderDetectorTests
{
    private static IXLWorksheet CreateWorksheet(string[,] data)
    {
        var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        for (int r = 0; r < data.GetLength(0); r++)
            for (int c = 0; c < data.GetLength(1); c++)
                ws.Cell(r + 1, c + 1).SetValue(data[r, c]);
        return ws;
    }

    [Fact]
    public void DetectHeaders_SimpleSheet_ReturnsFirstRow()
    {
        var ws = CreateWorksheet(new string[,]
        {
            { "Name", "Value", "Date" },
            { "Item A", "100",  "2024-01-01" },
            { "Item B", "200",  "2024-01-02" }
        });

        var (row, headers) = ExcelHeaderDetector.DetectHeaders(ws);

        Assert.Equal(1, row);
        Assert.Equal(3, headers.Count);
        Assert.Equal("Name", headers[0]);
        Assert.Equal("Value", headers[1]);
        Assert.Equal("Date", headers[2]);
    }

    [Fact]
    public void DetectHeaders_EmptyWorksheet_ReturnsZero()
    {
        var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Empty");

        var (row, headers) = ExcelHeaderDetector.DetectHeaders(ws);

        Assert.Equal(0, row);
        Assert.Empty(headers);
    }

    [Fact]
    public void DetectHeaders_SkipsLeadingEmptyRows()
    {
        var ws = CreateWorksheet(new string[,]
        {
            { "",       "",       "" },
            { "Header1","Header2","Header3" },
            { "val1",   "val2",   "val3" }
        });

        var (row, headers) = ExcelHeaderDetector.DetectHeaders(ws);

        Assert.Equal(2, row);
        Assert.Equal("Header1", headers[0]);
    }

    [Fact]
    public void FindColumnByHeader_ExactMatch_ReturnsColumnNumber()
    {
        var ws = CreateWorksheet(new string[,]
        {
            { "Dokumendi nr", "Nimetus", "Eriala" },
            { "1626_EL-01",   "Plaan",   "EL" }
        });

        var col = ExcelHeaderDetector.FindColumnByHeader(ws, headerRow: 1, headerName: "Nimetus");

        Assert.Equal(2, col);
    }

    [Fact]
    public void FindColumnByHeader_CaseInsensitive_ReturnsColumnNumber()
    {
        var ws = CreateWorksheet(new string[,]
        {
            { "DOKUMENDI NR", "nimetus" },
            { "val1", "val2" }
        });

        var col = ExcelHeaderDetector.FindColumnByHeader(ws, headerRow: 1, headerName: "Nimetus");

        Assert.Equal(2, col);
    }

    [Fact]
    public void FindColumnByHeader_NotFound_ReturnsZero()
    {
        var ws = CreateWorksheet(new string[,]
        {
            { "ColA", "ColB" },
            { "val1", "val2" }
        });

        var col = ExcelHeaderDetector.FindColumnByHeader(ws, headerRow: 1, headerName: "NonExistent");

        Assert.Equal(0, col);
    }
}
