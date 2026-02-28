using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Storage;
using SFDRN.Server.Transport;

namespace SFDRN.Server.Routing;

/// <summary>
/// Движок маршрутизации сообщений между нодами
/// </summary>
public class RoutingEngine
{
    private readonly NodeTransportService _transport;
    private readonly NodeRegistry _nodeRegistry;
    private readonly PacketStorage _packetStorage;
    private readonly ILogger<RoutingEngine> _logger;

    public RoutingEngine(
        NodeTransportService transport,
        NodeRegistry nodeRegistry,
        PacketStorage packetStorage,
        ILogger<RoutingEngine> logger)
    {
        _transport = transport;
        _nodeRegistry = nodeRegistry;
        _packetStorage = packetStorage;
        _logger = logger;
    }

    /// <summary>
    /// Маршрутизировать пакет
    /// </summary>
    public async Task<RouteResult> RoutePacket(PacketEnvelope packet)
    {
        _logger.LogInformation("[Routing] RoutePacket: {PacketId} {Source} → {Destination}",
            packet.PacketId, packet.SourceNode, packet.DestinationNode);

        // ═════════════════════════════════════════════════════════
        // ✅ 1. СНАЧАЛА: Мы получатель? (destination = local node)
        // ═════════════════════════════════════════════════════════
        if (packet.DestinationNode == _nodeRegistry.LocalNodeId)
        {
            _logger.LogInformation("[Routing] Destination is LOCAL NODE, storing");
            _packetStorage.StorePacket(packet);
            return new RouteResult
            {
                Success = true,
                Message = "Delivered locally to node",
                Method = "local"
            };
        }

        // ═════════════════════════════════════════════════════════
        // ✅ 2. Защита от loop: source == destination
        // ═════════════════════════════════════════════════════════
        if (packet.SourceNode == packet.DestinationNode)
        {
            _logger.LogWarning("[Routing] Loop detected: source == destination");
            return new RouteResult
            {
                Success = false,
                Message = "Loop detected",
                Method = "error"
            };
        }

        // ═════════════════════════════════════════════════════════
        // ✅ 3. Проверяем ClientMap - клиент на этой ноде?
        // ═════════════════════════════════════════════════════════
        var clientGateway = _nodeRegistry.GetClientGateway(packet.DestinationNode);

        if (clientGateway == _nodeRegistry.LocalNodeId)
        {
            _logger.LogInformation("[Routing] Client is local: {ClientId}", packet.DestinationNode);
            _packetStorage.StorePacket(packet);
            return new RouteResult
            {
                Success = true,
                Message = "Delivered to local client",
                Method = "local_client"
            };
        }

        // ═════════════════════════════════════════════════════════
        // ✅ 4. Знаем gateway клиента?
        // ═════════════════════════════════════════════════════════
        if (!string.IsNullOrEmpty(clientGateway))
        {
            // Защита: не отправляем сами себе
            if (clientGateway == _nodeRegistry.LocalNodeId)
            {
                _logger.LogWarning("[Routing] Gateway is self, storing locally");
                _packetStorage.StorePacket(packet);
                return new RouteResult
                {
                    Success = true,
                    Message = "Gateway is local",
                    Method = "local"
                };
            }

            _logger.LogInformation("[Routing] Routing to gateway: {Gateway}", clientGateway);
            var success = await RouteToClient(clientGateway, packet);
            return new RouteResult
            {
                Success = success,
                Message = success ? "Routed to gateway" : "Gateway unreachable",
                Method = "gateway",
                Gateway = clientGateway
            };
        }

        // ═════════════════════════════════════════════════════════
        // ✅ 5. Это известная нода?
        // ═════════════════════════════════════════════════════════
        var targetNode = _nodeRegistry.GetNode(packet.DestinationNode);
        if (targetNode != null)
        {
            // Защита: не отправляем сами себе
            if (targetNode.NodeId == _nodeRegistry.LocalNodeId)
            {
                _logger.LogWarning("[Routing] Would forward to self, storing locally");
                _packetStorage.StorePacket(packet);
                return new RouteResult
                {
                    Success = true,
                    Message = "Self-delivery",
                    Method = "local"
                };
            }

            _logger.LogInformation("[Routing] Target is known node: {NodeId}", packet.DestinationNode);
            var success = await TryForward(targetNode.PublicEndpoint, packet);
            return new RouteResult
            {
                Success = success,
                Message = success ? "Forwarded to node" : "Node unreachable",
                Method = "direct"
            };
        }

        // ═════════════════════════════════════════════════════════
        // ✅ 6. Flood на соседей
        // ═════════════════════════════════════════════════════════
        _logger.LogInformation("[Routing] Unknown destination, flooding");
        var sentCount = await FloodAsync(packet);

        return new RouteResult
        {
            Success = sentCount > 0,
            Message = sentCount > 0 ? $"Flooded to {sentCount} nodes" : "No neighbors to flood",
            Method = "flood",
            FloodCount = sentCount
        };
    }

    /// <summary>
    /// Отправить пакет на клиент через его gateway
    /// </summary>
    public async Task<bool> RouteToClient(string gatewayId, PacketEnvelope packet)
    {
        _logger.LogInformation("[Routing] RouteToClient: Gateway={Gateway}, Packet={PacketId}",
            gatewayId, packet.PacketId);

        var message = NodeMessage.FromPacket(packet);
        message.MessageId = packet.PacketId;

        var success = await _transport.SendToNodeAsync(gatewayId, message);

        if (success)
        {
            _logger.LogInformation("[Routing] ✓ Routed to client via {Gateway}", gatewayId);
        }
        else
        {
            _logger.LogWarning("[Routing] ✗ Failed to route to {Gateway}", gatewayId);
        }

        return success;
    }

    /// <summary>
    /// Попытаться переслать пакет на соседнюю ноду
    /// </summary>
    public async Task<bool> TryForward(string targetEndpoint, PacketEnvelope packet)
    {
        _logger.LogInformation("[Routing] TryForward: Endpoint={Endpoint}, Packet={PacketId}",
            targetEndpoint, packet.PacketId);

        var message = NodeMessage.FromPacket(packet);
        message.MessageId = packet.PacketId ?? Guid.NewGuid().ToString();

        var endpoint = ParseEndpoint(targetEndpoint);
        if (endpoint == null)
        {
            _logger.LogWarning("[Routing] Invalid endpoint: {Endpoint}", targetEndpoint);
            return false;
        }

        var success = await _transport.SendToNodeAsync(endpoint, message);

        _logger.LogInformation("[Routing] Forward {Status}: {PacketId}",
            success ? "✓" : "✗", packet.PacketId);

        return success;
    }

    /// <summary>
    /// Flood пакет на все соседние ноды
    /// </summary>
    public async Task<int> FloodAsync(PacketEnvelope packet)
    {
        _logger.LogInformation("[Routing] Flood: Packet={PacketId}", packet.PacketId);

        var message = NodeMessage.FromPacket(packet);
        message.MessageId = packet.PacketId ?? Guid.NewGuid().ToString();
        message.Type = NodeMessageType.Broadcast;

        var sentCount = await _transport.BroadcastAsync(message);

        _logger.LogInformation("[Routing] Flood complete: {Count} nodes", sentCount);

        return sentCount;
    }

    /// <summary>
    /// Проверить доступность ноды
    /// </summary>
    public async Task<bool> CheckNodeHealthAsync(string nodeId)
    {
        return await _transport.PingNodeAsync(nodeId);
    }

    // ═════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════

    private NodeEndpoint? ParseEndpoint(string url)
    {
        try
        {
            var uri = new Uri(url);

            return new NodeEndpoint
            {
                NodeId = uri.Host,
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80),
                Protocol = uri.Scheme.ToLowerInvariant() switch
                {
                    "https" => NodeProtocol.Https,
                    "http" => NodeProtocol.Http,
                    _ => NodeProtocol.Http
                }
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Результат маршрутизации
/// </summary>
public class RouteResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string? Gateway { get; set; }
    public int FloodCount { get; set; }
}