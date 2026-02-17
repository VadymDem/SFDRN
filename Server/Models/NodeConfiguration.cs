namespace SFDRN.Server.Models;

public class NodeConfiguration
{
    public string NodeId { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string PublicEndpoint { get; set; } = string.Empty;
    public List<string> Neighbors { get; set; } = new();
}