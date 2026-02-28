namespace SFDRN.Server.Models;

/// <summary>
/// Gossip сообщение - передаем ТОЛЬКО ДАЙДЖЕСТЫ профилей, а не полные данные
/// </summary>
public class GossipMessage
{
    public string SenderNodeId { get; set; } = string.Empty;
    public List<NodeInfo> KnownNodes { get; set; } = new();
    public Dictionary<string, string> ClientMap { get; set; } = new();

    // ✅ ИЗМЕНЕНО: вместо полных профилей отправляем дайджесты
    public List<ProfileDigest> ProfileDigests { get; set; } = new();
}

/// <summary>
/// Ответ на Gossip
/// </summary>
public class GossipResponse
{
    public bool Success { get; set; }
    public List<NodeInfo>? KnownNodes { get; set; }
    public Dictionary<string, string> ClientMap { get; set; } = new();

    // ✅ ИЗМЕНЕНО: отправляем дайджесты
    public List<ProfileDigest> ProfileDigests { get; set; } = new();
}