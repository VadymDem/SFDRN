namespace SFDRN.Server.Models;

public class NodeInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string PublicEndpoint { get; set; } = string.Empty;
    public List<string> Transports { get; set; } = new();
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public NodeStatus Status { get; set; } = NodeStatus.Unknown;
}

public enum NodeStatus
{
    Unknown,
    Alive,
    Dead,
    Suspicious
}