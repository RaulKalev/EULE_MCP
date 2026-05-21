namespace RevitMCP.Addin.Query;

public class GroupRow
{
    public Dictionary<string, string> Keys { get; set; } = new();
    public int Count { get; set; }
    public List<long>? ElementIds { get; set; }
}

public class GroupingResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<GroupRow> GroupsFlat { get; set; } = new();
    public object? GroupsNested { get; set; }
    public int TotalElements { get; set; }
    public int TotalGroups { get; set; }
    public List<string> Warnings { get; set; } = new();

    public static GroupingResult Failure(string message) =>
        new() { Success = false, Message = message };
}
