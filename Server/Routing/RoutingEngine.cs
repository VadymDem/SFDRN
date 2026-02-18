using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Storage;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SFDRN.Server.Routing;

public class RoutingEngine
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly PacketStorage _packetStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RoutingEngine> _logger;

    private const int HttpTimeoutSeconds = 5;
    private const int AckTimeoutMs = 5000;
    private const int MaxRetries = 3;

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

    // =========================================================
    // PUBLIC: обычная маршрутизация
    // =========================================================
    public async Task<ForwardResponse> RoutePacket(PacketEnvelope packet)
    {
        if (packet.Ttl <= 0)
        {
            _logger.LogWarning("Packet {PacketId} dropped: TTL expired", packet.PacketId);
            return Fail("TTL expired");
        }

        if (packet.DestinationNode == _nodeRegistry.LocalNodeId)
        {
            // Data хранится, Ack не хранится
            if (packet.Type == PacketType.Data)
            {
                _packetStorage.StorePacket(packet);
            }

            _logger.LogInformation("Packet {PacketId} delivered locally", packet.PacketId);
            return Success("Delivered to destination");
        }

        packet.Ttl--;

        var attempted = new HashSet<string>();

        while (true)
        {
            var nextHop = FindNextHop(
                _nodeRegistry.LocalNodeId,
                packet.DestinationNode,
                attempted);

            if (nextHop == null)
            {
                var reason = attempted.Any()
                    ? "No alternative route"
                    : "No route to destination";

                _logger.LogWarning("Packet {PacketId} dropped: {Reason}",
                    packet.PacketId, reason);

                return Fail(reason);
            }

            var node = _nodeRegistry.GetNode(nextHop);
            if (node == null)
            {
                attempted.Add(nextHop);
                continue;
            }

            _logger.LogInformation(
                "Packet {PacketId} {Source}->{Destination}, trying hop {NextHop}",
                packet.PacketId, packet.SourceNode, packet.DestinationNode, nextHop);

            var forwardResult = await TryForward(node.PublicEndpoint, packet);

            if (forwardResult)
            {
                return Success("Forwarded", nextHop);
            }

            _nodeRegistry.MarkNodeSuspicious(nextHop);
            attempted.Add(nextHop);
        }
    }

    // =========================================================
    // PUBLIC: отправка с ожиданием ACK (At-Least-Once)
    // =========================================================
    public async Task<bool> SendWithAck(PacketEnvelope packet)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            _packetStorage.RegisterPendingAck(packet.PacketId);

            var result = await RoutePacket(packet);

            if (!result.Success)
                return false;

            var ackTask = _packetStorage.GetPendingAckTask(packet.PacketId);

            if (ackTask != null)
            {
                var completed = await Task.WhenAny(
                    ackTask,
                    Task.Delay(AckTimeoutMs));

                if (completed == ackTask)
                {
                    _logger.LogInformation("ACK received for {PacketId}", packet.PacketId);
                    return true;
                }
            }

            _logger.LogWarning("ACK timeout for {PacketId}, retry {Attempt}",
                packet.PacketId, attempt);
        }

        _logger.LogError("Delivery failed for {PacketId}", packet.PacketId);
        return false;
    }

    // =========================================================
    // HTTP Forward
    // =========================================================
    public async Task<bool> TryForward(string endpoint, PacketEnvelope packet)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds);

            var url = $"{endpoint}/routing/forward";

            var content = new StringContent(
                JsonSerializer.Serialize(packet),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Packet {PacketId} forwarded successfully",
                    packet.PacketId);
                return true;
            }

            _logger.LogWarning("Forward failed: {Status}",
                response.StatusCode);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Transport error: {Message}", ex.Message);
            return false;
        }
    }

    // =========================================================
    // DIJKSTRA
    // =========================================================
    private string? FindNextHop(
        string sourceId,
        string destinationId,
        HashSet<string>? exclude = null)
    {
        var graph = _nodeRegistry.BuildGraph();

        if (exclude?.Any() == true)
        {
            foreach (var node in exclude)
            {
                graph.Remove(node);
                foreach (var neighbors in graph.Values)
                    neighbors.Remove(node);
            }
        }

        if (!graph.ContainsKey(sourceId) ||
            !graph.ContainsKey(destinationId))
            return null;

        var dist = new Dictionary<string, int>();
        var prev = new Dictionary<string, string?>();
        var unvisited = new HashSet<string>(graph.Keys);

        foreach (var node in graph.Keys)
        {
            dist[node] = int.MaxValue;
            prev[node] = null;
        }

        dist[sourceId] = 0;

        while (unvisited.Count > 0)
        {
            var current = unvisited
                .OrderBy(n => dist[n])
                .First();

            if (dist[current] == int.MaxValue)
                break;

            if (current == destinationId)
                break;

            unvisited.Remove(current);

            foreach (var neighbor in graph[current])
            {
                if (!unvisited.Contains(neighbor))
                    continue;

                var alt = dist[current] + 1;

                if (alt < dist[neighbor])
                {
                    dist[neighbor] = alt;
                    prev[neighbor] = current;
                }
            }
        }

        if (prev[destinationId] == null &&
            destinationId != sourceId)
            return null;

        var path = new List<string>();
        var step = destinationId;

        while (step != null)
        {
            path.Insert(0, step);
            prev.TryGetValue(step, out step);
        }

        _logger.LogInformation("Route: {Path}",
            string.Join(" → ", path));

        return path.Count >= 2 ? path[1] : null;
    }

    public async Task<bool> RouteToClient(string targetGatewayId, PacketEnvelope packet)
    {
        // Если целевая нода — это мы сами, значит клиент подключен к нам
        if (targetGatewayId == _nodeRegistry.LocalNodeId)
        {
            // Логика доставки клиенту (через WebSocket) уже должна быть в контроллере
            // Но для надежности можно вызвать RoutePacket, он поймет, что это Local
            var res = await RoutePacket(packet);
            return res.Success;
        }

        // Если клиент на другой ноде, просим Дейкстру найти путь до ЭТОЙ НОДЫ
        // Мы создаем "транзитный" поиск: Destination остается клиентским для финальной доставки, 
        // но путь мы ищем до шлюза.

        _logger.LogInformation("Routing packet {PacketId} to gateway {GatewayId} for client {Client}",
            packet.PacketId, targetGatewayId, packet.DestinationNode);

        // Вызываем поиск следующего прыжка до ШЛЮЗА
        var nextHop = FindNextHop(_nodeRegistry.LocalNodeId, targetGatewayId);

        if (nextHop == null) return false;

        var node = _nodeRegistry.GetNode(nextHop);
        if (node == null) return false;

        return await TryForward(node.PublicEndpoint, packet);
    }

    // =========================================================
    // helpers
    // =========================================================
    private ForwardResponse Success(string message, string? nextHop = null)
        => new()
        {
            Success = true,
            Message = message,
            NextHop = nextHop
        };

    private ForwardResponse Fail(string message)
        => new()
        {
            Success = false,
            Message = message
        };
}
