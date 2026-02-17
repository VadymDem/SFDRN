using SFDRN.Server.Models;
using System.Collections.Concurrent;

namespace SFDRN.Server.Storage;

public class PacketStorage
{
    private readonly ConcurrentQueue<PacketEnvelope> _packets = new();
    private readonly ILogger<PacketStorage> _logger;
    private const int MaxQueueSize = 1000;

    public PacketStorage(ILogger<PacketStorage> logger)
    {
        _logger = logger;
    }

    public void StorePacket(PacketEnvelope packet)
    {
        _packets.Enqueue(packet);
        _logger.LogInformation("Packet {PacketId} stored from {SourceNode}",
            packet.PacketId, packet.SourceNode);

        // Ограничение размера очереди
        while (_packets.Count > MaxQueueSize)
        {
            if (_packets.TryDequeue(out var oldPacket))
            {
                _logger.LogWarning("Packet {PacketId} removed from queue (overflow)",
                    oldPacket.PacketId);
            }
        }
    }

    public List<PacketEnvelope> GetAllPackets()
    {
        return _packets.ToList();
    }

    public int GetPacketCount()
    {
        return _packets.Count;
    }
}