namespace RevitMCP.Addin.Query;

public class ElementQueryResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<ElementInfoDto> Elements { get; set; } = new();
    public int TotalMatched { get; set; }
    public List<string> Warnings { get; set; } = new();

    public static ElementQueryResult Failure(string message) =>
        new() { Success = false, Message = message };
}
