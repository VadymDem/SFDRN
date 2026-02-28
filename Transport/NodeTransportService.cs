using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Storage;

namespace SFDRN.Server.Transport;

/// <summary>
/// Сервис для отправки сообщений через транспорт
/// Заменяет прямые вызовы HttpClient в RoutingEngine и других сервисах
/// </summary>
public class NodeTransportService
{
    private readonly INodeTransportFactory _transportFactory;
    private readonly NodeRegistry _nodeRegistry;
    private readonly ILogger<NodeTransportService> _logger;

    public NodeTransportService(
        INodeTransportFactory transportFactory,
        NodeRegistry nodeRegistry,
        ILogger<NodeTransportService> logger)
    {
        _transportFactory = transportFactory;
        _nodeRegistry = nodeRegistry;
        _logger = logger;
    }

    /// <summary>
    /// Отправить сообщение на ноду по ID
    /// </summary>
    public async Task<bool> SendToNodeAsync(string nodeId, NodeMessage message, CancellationToken cancellationToken = default)
    {
        var endpoint = GetNodeEndpoint(nodeId);
        if (endpoint == null)
        {
            _logger.LogWarning("[TransportSvc] Node not found: {NodeId}", nodeId);
            return false;
        }

        return await SendToNodeAsync(endpoint, message, cancellationToken);
    }

    /// <summary>
    /// Отправить сообщение на ноду по endpoint
    /// </summary>
    public async Task<bool> SendToNodeAsync(NodeEndpoint node, NodeMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var transport = _transportFactory.Create(node);

            _logger.LogDebug("[TransportSvc] Sending {MessageId} to {NodeId} via {Protocol}",
                message.MessageId, node.NodeId, node.Protocol);

            var success = await transport.SendAsync(node, message, cancellationToken);

            if (success)
            {
                _logger.LogDebug("[TransportSvc] ✓ Sent {MessageId} to {NodeId}", message.MessageId, node.NodeId);
            }
            else
            {
                _logger.LogWarning("[TransportSvc] ✗ Failed to send {MessageId} to {NodeId}", message.MessageId, node.NodeId);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TransportSvc] Error sending {MessageId} to {NodeId}", message.MessageId, node.NodeId);
            return false;
        }
    }

    /// <summary>
    /// Отправить пакет на ноду
    /// </summary>
    public async Task<bool> SendPacketAsync(string nodeId, PacketEnvelope packet, CancellationToken cancellationToken = default)
    {
        var message = NodeMessage.FromPacket(packet);
        return await SendToNodeAsync(nodeId, message, cancellationToken);
    }

    /// <summary>
    /// Запросить ответ от ноды
    /// </summary>
    public async Task<NodeResponse?> RequestFromNodeAsync(string nodeId, NodeMessage message, CancellationToken cancellationToken = default)
    {
        var endpoint = GetNodeEndpoint(nodeId);
        if (endpoint == null)
        {
            _logger.LogWarning("[TransportSvc] Node not found: {NodeId}", nodeId);
            return new NodeResponse { Success = false, Error = "Node not found" };
        }

        return await RequestFromNodeAsync(endpoint, message, cancellationToken);
    }

    /// <summary>
    /// Запросить ответ от ноды по endpoint
    /// </summary>
    public async Task<NodeResponse?> RequestFromNodeAsync(NodeEndpoint node, NodeMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var transport = _transportFactory.Create(node);

            _logger.LogDebug("[TransportSvc] Requesting {MessageId} from {NodeId} via {Protocol}",
                message.MessageId, node.NodeId, node.Protocol);

            var response = await transport.RequestAsync(node, message, cancellationToken);

            if (response?.Success == true)
            {
                _logger.LogDebug("[TransportSvc] ✓ Got response from {NodeId}", node.NodeId);
            }
            else
            {
                _logger.LogWarning("[TransportSvc] ✗ Failed to get response from {NodeId}: {Error}",
                    node.NodeId, response?.Error);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TransportSvc] Error requesting from {NodeId}", node.NodeId);
            return new NodeResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Проверить доступность ноды
    /// </summary>
    public async Task<bool> PingNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        var endpoint = GetNodeEndpoint(nodeId);
        if (endpoint == null) return false;

        try
        {
            var transport = _transportFactory.Create(endpoint);
            return await transport.PingAsync(endpoint, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TransportSvc] Ping failed for {NodeId}", nodeId);
            return false;
        }
    }

    /// <summary>
    /// Широковещательная отправка всем соседям
    /// </summary>
    public async Task<int> BroadcastAsync(NodeMessage message, CancellationToken cancellationToken = default)
    {
        var neighbors = _nodeRegistry.GetAliveNodes()
            .Where(n => n.NodeId != _nodeRegistry.LocalNodeId)
            .ToList();

        if (neighbors.Count == 0)
        {
            _logger.LogDebug("[TransportSvc] No neighbors to broadcast");
            return 0;
        }

        _logger.LogDebug("[TransportSvc] Broadcasting {MessageId} to {Count} neighbors",
            message.MessageId, neighbors.Count);

        var tasks = neighbors.Select(async node =>
        {
            var endpoint = ConvertToEndpoint(node);
            var transport = _transportFactory.Create(endpoint);
            return await transport.SendAsync(endpoint, message, cancellationToken);
        });

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);

        _logger.LogDebug("[TransportSvc] Broadcast complete: {Success}/{Total}",
            successCount, neighbors.Count);

        return successCount;
    }

    // ═════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════

    private NodeEndpoint? GetNodeEndpoint(string nodeId)
    {
        var node = _nodeRegistry.GetNode(nodeId);
        return node != null ? ConvertToEndpoint(node) : null;
    }

    private NodeEndpoint ConvertToEndpoint(NodeInfo node)
    {
        // Парсим PublicEndpoint (например, "http://node1.sfdrn.io:5000")
        var uri = new Uri(node.PublicEndpoint);

        return new NodeEndpoint
        {
            NodeId = node.NodeId,
            Host = uri.Host,
            Port = uri.Port,
            Protocol = uri.Scheme.ToLowerInvariant() switch
            {
                "https" => NodeProtocol.Https,
                "http" => NodeProtocol.Http,
                "wss" => NodeProtocol.WebSocketSecure,
                "ws" => NodeProtocol.WebSocket,
                _ => NodeProtocol.Http
            },
            Timeout = TimeSpan.FromSeconds(10)
        };
    }
}