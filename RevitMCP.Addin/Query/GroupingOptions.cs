namespace RevitMCP.Addin.Query;

public class GroupingOptions
{
    public List<GroupKeyOptions> GroupBy { get; set; } = new();
    public bool IncludeElements { get; set; }
    public int Limit { get; set; } = 5000;
}
