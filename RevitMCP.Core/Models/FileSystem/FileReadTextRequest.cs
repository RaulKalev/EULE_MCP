namespace RevitMCP.Core.Models.FileSystem;

public class FileReadTextRequest
{
    public string FilePath { get; set; } = string.Empty;
    public int MaxBytes { get; set; } = 1_048_576;
}
