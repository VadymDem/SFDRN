namespace SFDRN.Server.Models;

public class NodeInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string PublicEndpoint { get; set; } = string.Empty;
    public List<string> Transports { get; set; } = new();
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public NodeStatus Status { get; set; } = NodeStatus.Unknown;
    public List<string> DirectNeighbors { get; set; } = new();
}

public enum NodeStatus
{
    Unknown,    // Temp placeholder, еще не контактировали
    Alive,      // Heartbeat OK
    Suspicious, // Локальная метка: connection error на этой ноде, исключен из графа
    Dead        // Глобальная метка: подтверждено через gossip timeout
}