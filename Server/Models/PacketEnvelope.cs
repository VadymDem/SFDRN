using System.Net.Sockets;

namespace SFDRN.Server.Models;

public enum PacketType
{
    Data = 0,
    Ack = 1
}

public class PacketEnvelope
{
    public string PacketId { get; set; } = Guid.NewGuid().ToString();
    public PacketType Type { get; set; } = PacketType.Data;

    public string SourceNode { get; set; } = string.Empty;
    public string DestinationNode { get; set; } = string.Empty;

    public int Ttl { get; set; } = 10;

    public byte[] EncryptedPayload { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
