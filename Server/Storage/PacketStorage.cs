using SFDRN.Server.Models;
using System.Collections.Concurrent;

namespace SFDRN.Server.Storage;

public class PacketStorage
{
    private readonly ConcurrentQueue<PacketEnvelope> _packets = new();
    private readonly ILogger<PacketStorage> _logger;
    private const int MaxQueueSize = 1000;

    private readonly ConcurrentDictionary<string, DateTime> _seenPackets = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();

    // ✅ Хранилище сообщений по получателям (для клиентов)
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PacketEnvelope>> _nodeMessages = new();

    public PacketStorage(ILogger<PacketStorage> logger)
    {
        _logger = logger;
    }

    public void StorePacket(PacketEnvelope packet)
    {
        _packets.Enqueue(packet);

        // ✅ Также сохраняем в очередь получателя для клиентского API
        if (!_nodeMessages.ContainsKey(packet.DestinationNode))
        {
            _nodeMessages[packet.DestinationNode] = new ConcurrentQueue<PacketEnvelope>();
        }

        _nodeMessages[packet.DestinationNode].Enqueue(packet);

        _logger.LogInformation("Packet {PacketId} stored for {DestinationNode}",
            packet.PacketId, packet.DestinationNode);

        while (_packets.Count > MaxQueueSize)
        {
            if (_packets.TryDequeue(out var oldPacket))
            {
                _logger.LogWarning("Packet {PacketId} removed from queue (overflow)",
                    oldPacket.PacketId);
            }
        }
    }

    // ✅ Получить сообщения для конкретного узла/клиента
    public List<PacketEnvelope> GetPacketsForNode(string nodeId)
    {
        if (!_nodeMessages.TryGetValue(nodeId, out var queue))
        {
            return new List<PacketEnvelope>();
        }

        var packets = new List<PacketEnvelope>();
        while (queue.TryDequeue(out var packet))
        {
            packets.Add(packet);
        }

        _logger.LogInformation("Retrieved {Count} packets for {NodeId}", packets.Count, nodeId);
        return packets;
    }

    // ✅ Получить количество непрочитанных сообщений
    public int GetUnreadCount(string nodeId)
    {
        if (!_nodeMessages.TryGetValue(nodeId, out var queue))
        {
            return 0;
        }

        return queue.Count;
    }

    public List<PacketEnvelope> GetAllPackets()
    {
        return _packets.ToList();
    }

    public int GetPacketCount()
    {
        return _packets.Count;
    }

    public bool TryMarkAsSeen(string packetId)
    {
        return _seenPackets.TryAdd(packetId, DateTime.UtcNow);
    }

    public bool HasSeen(string packetId)
    {
        return _seenPackets.ContainsKey(packetId);
    }

    public void RegisterPendingAck(string packetId)
    {
        _pendingAcks.TryAdd(packetId, new TaskCompletionSource<bool>());
    }

    public void CompleteAck(string packetId)
    {
        if (_pendingAcks.TryRemove(packetId, out var tcs))
            tcs.TrySetResult(true);
    }

    public Task<bool>? GetPendingAckTask(string packetId)
    {
        return _pendingAcks.TryGetValue(packetId, out var tcs)
            ? tcs.Task
            : null;
    }
}