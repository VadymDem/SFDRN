using System.Collections.Concurrent;
using SFDRN.Server.Models;
using Microsoft.Extensions.Logging;

namespace SFDRN.Server.Mesh;

public class NodeRegistry
{
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new();
    private readonly NodeConfiguration _localConfig;
    private readonly ILogger<NodeRegistry>? _logger;
    private readonly object _lockObject = new();

    public NodeRegistry(NodeConfiguration localConfig, ILogger<NodeRegistry>? logger = null)
    {
        _localConfig = localConfig;
        _logger = logger;

        var localNode = new NodeInfo
        {
            NodeId = localConfig.NodeId,
            Region = localConfig.Region,
            PublicEndpoint = NormalizeUrl(localConfig.PublicEndpoint),
            Transports = new List<string> { "HTTPS", "WebSocket" },
            LastSeen = DateTime.UtcNow,
            Status = NodeStatus.Alive,
            // ✅ Заполняем прямых соседей из конфига
            DirectNeighbors = localConfig.Neighbors.Select(NormalizeUrl).ToList()
        };
        _nodes.TryAdd(localConfig.NodeId, localNode);
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        return url.Trim().TrimEnd('/').ToLowerInvariant();
    }

    public void UpdateNode(NodeInfo nodeInfo)
    {
        if (nodeInfo.NodeId == _localConfig.NodeId)
            return;

        var normalizedEndpoint = NormalizeUrl(nodeInfo.PublicEndpoint);

        // ✅ Игнорируем узлы указывающие на наш endpoint
        if (normalizedEndpoint == NormalizeUrl(_localConfig.PublicEndpoint))
        {
            _logger?.LogDebug("Ignoring node {NodeId} pointing to local endpoint", nodeInfo.NodeId);
            return;
        }

        nodeInfo.PublicEndpoint = normalizedEndpoint;
        nodeInfo.LastSeen = DateTime.UtcNow;

        lock (_lockObject)
        {
            var duplicates = _nodes.Values
                .Where(n => n.NodeId != nodeInfo.NodeId &&
                            n.NodeId != _localConfig.NodeId &&
                            NormalizeUrl(n.PublicEndpoint) == normalizedEndpoint)
                .ToList();

            bool incomingIsReal = !nodeInfo.NodeId.StartsWith("temp-");

            foreach (var dup in duplicates)
            {
                bool existingIsReal = !dup.NodeId.StartsWith("temp-");
                if (incomingIsReal || !existingIsReal)
                {
                    if (_nodes.TryRemove(dup.NodeId, out _))
                    {
                        _logger?.LogDebug("Removed duplicate node {OldId} replaced by {NewId}",
                            dup.NodeId, nodeInfo.NodeId);
                    }
                }
            }

            var existing = _nodes.Values.FirstOrDefault(n =>
                n.NodeId != _localConfig.NodeId &&
                NormalizeUrl(n.PublicEndpoint) == normalizedEndpoint);

            if (existing != null)
            {
                if (!incomingIsReal && !existing.NodeId.StartsWith("temp-"))
                {
                    // Temp не обновляет статус real узла
                    if (nodeInfo.Transports?.Any() == true)
                        existing.Transports = nodeInfo.Transports;
                    return;
                }

                if (existing.NodeId != nodeInfo.NodeId)
                    _nodes.TryRemove(existing.NodeId, out _);
            }

            _nodes.AddOrUpdate(nodeInfo.NodeId, nodeInfo, (_, _) => nodeInfo);
            _logger?.LogDebug("Updated node {NodeId} ({Endpoint})", nodeInfo.NodeId, nodeInfo.PublicEndpoint);
        }
    }

    public NodeInfo? GetNode(string nodeId)
    {
        _nodes.TryGetValue(nodeId, out var node);
        return node;
    }

    public List<NodeInfo> GetAllNodes()
    {
        lock (_lockObject)
        {
            return _nodes.Values.ToList();
        }
    }

    public List<NodeInfo> GetAliveNodes()
    {
        lock (_lockObject)
        {
            return _nodes.Values.Where(n => n.Status == NodeStatus.Alive).ToList();
        }
    }

    public void MarkNodeDead(string nodeId)
    {
        if (nodeId == _localConfig.NodeId)
            return;

        lock (_lockObject)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = NodeStatus.Dead;
                _logger?.LogWarning("Marked node {NodeId} as dead", nodeId);
            }
        }
    }

    public void UpdateLocalNodeStatus(NodeStatus status)
    {
        lock (_lockObject)
        {
            if (_nodes.TryGetValue(_localConfig.NodeId, out var localNode))
            {
                localNode.Status = status;
                localNode.LastSeen = DateTime.UtcNow;
                _logger?.LogInformation("Local node status updated to {Status}", status);
            }
        }
    }

    public bool RemoveNode(string nodeId)
    {
        if (nodeId == _localConfig.NodeId)
            return false;

        lock (_lockObject)
        {
            if (_nodes.TryRemove(nodeId, out var removed))
            {
                _logger?.LogDebug("Removed node {NodeId} ({Endpoint})", removed.NodeId, removed.PublicEndpoint);
                return true;
            }
            return false;
        }
    }

    // ✅ Строим граф для Dijkstra: NodeId -> список NodeId соседей (только Alive)
    public Dictionary<string, List<string>> BuildGraph()
    {
        lock (_lockObject)
        {
            var graph = new Dictionary<string, List<string>>();
            var allNodes = _nodes.Values.ToList();

            foreach (var node in allNodes)
            {
                if (node.Status == NodeStatus.Dead)
                    continue;

                if (!graph.ContainsKey(node.NodeId))
                    graph[node.NodeId] = new List<string>();

                // Находим соседей по DirectNeighbors (сопоставляем endpoint -> NodeId)
                foreach (var neighborEndpoint in node.DirectNeighbors)
                {
                    var neighbor = allNodes.FirstOrDefault(n =>
                        NormalizeUrl(n.PublicEndpoint) == NormalizeUrl(neighborEndpoint) &&
                        n.Status != NodeStatus.Dead);

                    if (neighbor != null && neighbor.NodeId != node.NodeId)
                    {
                        graph[node.NodeId].Add(neighbor.NodeId);
                    }
                }
            }

            return graph;
        }
    }

    public string LocalNodeId => _localConfig.NodeId;
}