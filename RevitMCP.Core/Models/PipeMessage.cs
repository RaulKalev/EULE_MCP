namespace RevitMCP.Core.Models;

public enum PipeMessageType
{
    Request,
    Response,
    Ping,
    Pong,
    Error
}

public class PipeMessage
{
    public PipeMessageType Type { get; set; }
    public string? Payload { get; set; }
}
