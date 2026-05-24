namespace RevitMCP.Core.Models.FileSystem;

public class FileListDirectoryRequest
{
    public string FolderPath { get; set; } = string.Empty;
    public string SearchPattern { get; set; } = "*";
    public bool Recursive { get; set; } = false;
    public int MaxResults { get; set; } = 500;
}
