using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Storage;
using System.Text.Json;

namespace SFDRN.Server.Routing;

public class RoutingEngine
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly PacketStorage _packetStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RoutingEngine> _logger;

    public RoutingEngine(
        NodeRegistry nodeRegistry,
        PacketStorage packetStorage,
        IHttpClientFactory httpClientFactory,
        ILogger<RoutingEngine> logger)
    {
        _nodeRegistry = nodeRegistry;
        _packetStorage = packetStorage;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ForwardResponse> RoutePacket(PacketEnvelope packet)
    {
        // Проверка TTL
        if (packet.Ttl <= 0)
        {
            _logger.LogWarning("Packet {PacketId} dropped: TTL expired", packet.PacketId);
            return new ForwardResponse
            {
                Success = false,
                Message = "TTL expired"
            };
        }

        // Если пакет для нас - сохраняем
        if (packet.DestinationNode == _nodeRegistry.LocalNodeId)
        {
            _packetStorage.StorePacket(packet);
            _logger.LogInformation("Packet {PacketId} delivered to local node", packet.PacketId);
            return new ForwardResponse
            {
                Success = true,
                Message = "Delivered to destination"
            };
        }

        // Декрементируем TTL
        packet.Ttl--;

        // Находим живого соседа для форварда
        var aliveNodes = _nodeRegistry.GetAliveNodes()
            .Where(n => n.NodeId != _nodeRegistry.LocalNodeId && n.NodeId != packet.SourceNode)
            .ToList();

        if (!aliveNodes.Any())
        {
            _logger.LogWarning("Packet {PacketId} dropped: no alive neighbors", packet.PacketId);
            return new ForwardResponse
            {
                Success = false,
                Message = "No alive neighbors"
            };
        }

        // Пытаемся найти узел назначения напрямую
        var destinationNode = aliveNodes.FirstOrDefault(n => n.NodeId == packet.DestinationNode);

        if (destinationNode == null)
        {
            // Выбираем случайного живого соседа
            var random = new Random();
            destinationNode = aliveNodes[random.Next(aliveNodes.Count)];
        }

        // Форвардим пакет
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var url = $"{destinationNode.PublicEndpoint}/routing/forward";
            var content = new StringContent(
                JsonSerializer.Serialize(packet),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Packet {PacketId} forwarded to {NodeId}",
                    packet.PacketId, destinationNode.NodeId);

                return new ForwardResponse
                {
                    Success = true,
                    Message = "Forwarded",
                    NextHop = destinationNode.NodeId
                };
            }
            else
            {
                _logger.LogWarning("Failed to forward packet {PacketId} to {NodeId}: {StatusCode}",
                    packet.PacketId, destinationNode.NodeId, response.StatusCode);

                return new ForwardResponse
                {
                    Success = false,
                    Message = $"Forward failed: {response.StatusCode}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forwarding packet {PacketId} to {NodeId}",
                packet.PacketId, destinationNode.NodeId);

            return new ForwardResponse
            {
                Success = false,
                Message = $"Forward error: {ex.Message}"
            };
        }
    }
}