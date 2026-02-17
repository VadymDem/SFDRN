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
        if (packet.Ttl <= 0)
        {
            _logger.LogWarning("Packet {PacketId} dropped: TTL expired", packet.PacketId);
            return new ForwardResponse { Success = false, Message = "TTL expired" };
        }

        // Пакет для нас - сохраняем
        if (packet.DestinationNode == _nodeRegistry.LocalNodeId)
        {
            _packetStorage.StorePacket(packet);
            _logger.LogInformation("Packet {PacketId} delivered to local node", packet.PacketId);
            return new ForwardResponse { Success = true, Message = "Delivered to destination" };
        }

        packet.Ttl--;

        // ✅ Dijkstra: ищем путь от текущего узла до назначения
        var nextHopId = FindNextHop(_nodeRegistry.LocalNodeId, packet.DestinationNode);

        if (nextHopId == null)
        {
            _logger.LogWarning("Packet {PacketId} dropped: no route to {Destination}",
                packet.PacketId, packet.DestinationNode);
            return new ForwardResponse { Success = false, Message = "No route to destination" };
        }

        var nextHopNode = _nodeRegistry.GetNode(nextHopId);
        if (nextHopNode == null)
        {
            _logger.LogWarning("Packet {PacketId} dropped: next hop {NextHop} not found in registry",
                packet.PacketId, nextHopId);
            return new ForwardResponse { Success = false, Message = "Next hop not found" };
        }

        _logger.LogInformation("Packet {PacketId}: {Source} → {Destination}, next hop: {NextHop}",
            packet.PacketId, packet.SourceNode, packet.DestinationNode, nextHopId);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var url = $"{nextHopNode.PublicEndpoint}/routing/forward";
            var content = new StringContent(
                JsonSerializer.Serialize(packet),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Packet {PacketId} forwarded to {NodeId}",
                    packet.PacketId, nextHopId);

                return new ForwardResponse
                {
                    Success = true,
                    Message = "Forwarded",
                    NextHop = nextHopId
                };
            }
            else
            {
                _logger.LogWarning("Failed to forward packet {PacketId} to {NodeId}: {StatusCode}",
                    packet.PacketId, nextHopId, response.StatusCode);

                _nodeRegistry.MarkNodeDead(nextHopId);

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
                packet.PacketId, nextHopId);

            _nodeRegistry.MarkNodeDead(nextHopId);

            return new ForwardResponse
            {
                Success = false,
                Message = $"Forward error: {ex.Message}"
            };
        }
    }

    // ✅ Dijkstra: возвращает первый шаг на пути от source до destination
    private string? FindNextHop(string sourceId, string destinationId)
    {
        var graph = _nodeRegistry.BuildGraph();

        _logger.LogDebug("Dijkstra graph: {Graph}",
            string.Join(", ", graph.Select(kv => $"{kv.Key}→[{string.Join(",", kv.Value)}]")));

        if (!graph.ContainsKey(sourceId))
        {
            _logger.LogWarning("Source {SourceId} not in graph", sourceId);
            return null;
        }

        if (!graph.ContainsKey(destinationId))
        {
            _logger.LogWarning("Destination {DestinationId} not in graph", destinationId);
            return null;
        }

        // Инициализация
        var dist = new Dictionary<string, int>();
        var prev = new Dictionary<string, string?>();
        var unvisited = new HashSet<string>();

        foreach (var nodeId in graph.Keys)
        {
            dist[nodeId] = int.MaxValue;
            prev[nodeId] = null;
            unvisited.Add(nodeId);
        }

        dist[sourceId] = 0;

        while (unvisited.Count > 0)
        {
            // Берем узел с минимальной дистанцией
            var current = unvisited
                .Where(n => dist.ContainsKey(n))
                .OrderBy(n => dist[n])
                .FirstOrDefault();

            if (current == null || dist[current] == int.MaxValue)
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

        // Восстанавливаем путь
        if (prev[destinationId] == null && destinationId != sourceId)
        {
            _logger.LogWarning("No path found from {Source} to {Destination}", sourceId, destinationId);
            return null;
        }

        // Идем от destination назад до source, находим первый шаг
        var path = new List<string>();
        var step = destinationId;

        while (step != null)
        {
            path.Insert(0, step);
            prev.TryGetValue(step, out step);
        }

        _logger.LogInformation("Route found: {Path}", string.Join(" → ", path));

        // Первый шаг после source
        return path.Count >= 2 ? path[1] : null;
    }
}