using SFDRN.Server.Models;

namespace SFDRN.Server.Mesh;

public class ClientProfile
{
    public string NodeId { get; set; } = string.Empty;
    public string GlobalNickname { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "Hey! I'm using SFDRN";

    /// <summary>
    /// Время последнего обновления (для Last-Write-Wins conflict resolution)
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}