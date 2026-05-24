namespace RevitMCP.Core.Models.FileSystem;

public class FileWriteTextRequest
{
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Overwrite { get; set; } = false;
    public bool CreateDirectories { get; set; } = true;
}
