namespace SFDRN.Server.Models;

public class PacketEnvelope
{
    public string PacketId { get; set; } = Guid.NewGuid().ToString();
    public string SourceNode { get; set; } = string.Empty;
    public string DestinationNode { get; set; } = string.Empty;
    public int Ttl { get; set; } = 10;
    public byte[] EncryptedPayload { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ForwardResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? NextHop { get; set; }
}