namespace SFDRN.Server.Models; 

public class ClientProfile
{
    public string NodeId { get; set; } = string.Empty;
    public string GlobalNickname { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "Hey! I'm using SFDRN";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}