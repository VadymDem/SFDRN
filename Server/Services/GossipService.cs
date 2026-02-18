using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using System.Text.Json;

namespace SFDRN.Server.Services;

public class GossipService : BackgroundService
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly NodeConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GossipService> _logger;
    private readonly TimeSpan _gossipInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _deadNodeRetryInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTime> _lastRetryAttempt = new();

    public GossipService(
        NodeRegistry nodeRegistry,
        NodeConfiguration config,
        IHttpClientFactory httpClientFactory,
        ILogger<GossipService> logger)
    {
        _nodeRegistry = nodeRegistry;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GossipService started for node {NodeId}", _config.NodeId);
        _logger.LogInformation("Neighbors configured: {Count}", _config.Neighbors.Count);
        foreach (var neighbor in _config.Neighbors)
            _logger.LogInformation("  - {Neighbor}", neighbor);

        _nodeRegistry.UpdateLocalNodeStatus(NodeStatus.Alive);

        foreach (var neighborEndpoint in _config.Neighbors)
        {
            var normalizedEndpoint = NormalizeUrl(neighborEndpoint);
            var existing = _nodeRegistry.GetAllNodes()
                .FirstOrDefault(n => NormalizeUrl(n.PublicEndpoint) == normalizedEndpoint);

            if (existing == null)
            {
                var neighborNode = new NodeInfo
                {
                    NodeId = $"temp-{Math.Abs(neighborEndpoint.GetHashCode())}",
                    PublicEndpoint = normalizedEndpoint,
                    Status = NodeStatus.Unknown,
                    Transports = new List<string> { "HTTPS" },
                    LastSeen = DateTime.UtcNow
                };
                _nodeRegistry.UpdateNode(neighborNode);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformGossip(stoppingToken);
                await RetryDeadNodes(stoppingToken);
                await Task.Delay(_gossipInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during gossip cycle");
            }
        }
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        return url.Trim().TrimEnd('/').ToLowerInvariant();
    }

    // ✅ Единственное место где создается копия локального узла
    private NodeInfo CreateLocalNodeCopy()
    {
        var local = _nodeRegistry.GetNode(_nodeRegistry.LocalNodeId)!;
        return new NodeInfo
        {
            NodeId = local.NodeId,
            Region = local.Region,
            PublicEndpoint = local.PublicEndpoint,
            Transports = local.Transports,
            LastSeen = DateTime.UtcNow,
            Status = NodeStatus.Alive,
            DirectNeighbors = local.DirectNeighbors // ✅ Передаем соседей для графа
        };
    }

    private async Task RetryDeadNodes(CancellationToken cancellationToken)
    {
        // ✅ Retry для Dead И Suspicious узлов
        var deadNodes = _nodeRegistry.GetAllNodes()
            .Where(n => (n.Status == NodeStatus.Dead || n.Status == NodeStatus.Suspicious) &&
                        n.NodeId != _nodeRegistry.LocalNodeId &&
                        !n.NodeId.StartsWith("temp-"))
            .ToList();

        foreach (var deadNode in deadNodes)
        {
            if (_lastRetryAttempt.TryGetValue(deadNode.NodeId, out var lastAttempt))
            {
                if (DateTime.UtcNow - lastAttempt < _deadNodeRetryInterval)
                    continue;
            }

            _lastRetryAttempt[deadNode.NodeId] = DateTime.UtcNow;
            _logger.LogInformation("Attempting to reconnect to dead node {NodeId} at {Endpoint}",
                deadNode.NodeId, deadNode.PublicEndpoint);

            await TryGossipWithNode(deadNode, cancellationToken);
        }
    }

    private async Task PerformGossip(CancellationToken cancellationToken)
    {
        var allNodes = _nodeRegistry.GetAllNodes();

        var deduplicatedNodes = allNodes
            .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
            .Select(group =>
            {
                var realNodes = group.Where(n => !n.NodeId.StartsWith("temp-")).ToList();
                return realNodes.Any()
                    ? realNodes.OrderByDescending(n => n.LastSeen).First()
                    : group.OrderByDescending(n => n.LastSeen).First();
            })
            .ToList();

        var nodesToShare = deduplicatedNodes
            .Where(n => n.NodeId != _nodeRegistry.LocalNodeId)
            .ToList();
        nodesToShare.Add(CreateLocalNodeCopy()); // ✅

        var activeNodes = deduplicatedNodes
            .Where(n => n.Status != NodeStatus.Dead && n.NodeId != _nodeRegistry.LocalNodeId)
            .ToList();

        if (!activeNodes.Any())
        {
            _logger.LogWarning("No active neighbors for gossip");
            return;
        }

        var random = new Random();
        var target = activeNodes[random.Next(activeNodes.Count)];

        _logger.LogInformation("Gossiping with {NodeId} ({Status}) at {Endpoint}",
            target.NodeId, target.Status, target.PublicEndpoint);

        await TryGossipWithNode(target, cancellationToken, nodesToShare);
    }

    private async Task TryGossipWithNode(NodeInfo target, CancellationToken cancellationToken, List<NodeInfo>? nodesToShare = null)
    {
        nodesToShare ??= new List<NodeInfo> { CreateLocalNodeCopy() };

        var message = new GossipMessage
        {
            SenderNodeId = _nodeRegistry.LocalNodeId,
            KnownNodes = nodesToShare,
            // ✅ Добавляем карту клиентов в сообщение
            ClientMap = _nodeRegistry.GetClientMap()
        };

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var url = $"{target.PublicEndpoint}/mesh/gossip";
            var content = new StringContent(
                JsonSerializer.Serialize(message),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var gossipResponse = JsonSerializer.Deserialize<GossipResponse>(responseContent);

                // 1. Синхронизируем ноды (твой существующий код)
                if (gossipResponse?.KnownNodes != null)
                {
                    _nodeRegistry.BatchUpdateNodes(gossipResponse.KnownNodes);
                }

                // 2. ✅ НОВОЕ: Синхронизируем карту клиентов
                if (gossipResponse?.ClientMap != null)
                {
                    _nodeRegistry.SyncClientMap(gossipResponse.ClientMap);
                    _logger.LogDebug("Synced {Count} clients from {NodeId}",
                        gossipResponse.ClientMap.Count, target.NodeId);
                }

                var updatedTargetInfo = gossipResponse?.KnownNodes?
                    .FirstOrDefault(n => NormalizeUrl(n.PublicEndpoint) == NormalizeUrl(target.PublicEndpoint));

                if (updatedTargetInfo != null)
                {
                    updatedTargetInfo.LastSeen = DateTime.UtcNow;
                    _nodeRegistry.UpdateNode(updatedTargetInfo);
                    _nodeRegistry.MarkNodeAlive(updatedTargetInfo.NodeId);
                    _logger.LogInformation("Gossip successful with {NodeId}", updatedTargetInfo.NodeId);
                }
                else
                {
                    if (target.Status == NodeStatus.Dead || target.Status == NodeStatus.Suspicious)
                        _logger.LogInformation("Node {NodeId} is back: {OldStatus} -> Alive", target.NodeId, target.Status);
                    _nodeRegistry.MarkNodeAlive(target.NodeId);
                }
            }
            else
            {
                _logger.LogWarning("Gossip failed with {NodeId}: {StatusCode}", target.NodeId, response.StatusCode);
                _nodeRegistry.MarkNodeDead(target.NodeId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to gossip with {NodeId} at {Endpoint}",
                target.NodeId, target.PublicEndpoint);
            _nodeRegistry.MarkNodeDead(target.NodeId);
        }
    }
}