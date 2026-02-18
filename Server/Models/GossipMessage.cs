using SFDRN.Server.Models;

namespace SFDRN.Server.Mesh;

public class GossipMessage
{
    public string SenderNodeId { get; set; } = string.Empty;
    public List<NodeInfo> KnownNodes { get; set; } = new();

    public Dictionary<string, string> ClientMap { get; set; } = new();
}

public class GossipResponse
{
    public bool Success { get; set; }
    public List<NodeInfo>? KnownNodes { get; set; }

    public Dictionary<string, string> ClientMap { get; set; } = new();
}