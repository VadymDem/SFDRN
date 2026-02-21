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
    private readonly Dictionary<string, ClientProfile> _clientProfiles = new();
    private readonly object _profilesLock = new();

    // [ID Клиента] -> [ID Ноды-шлюза]
    private readonly ConcurrentDictionary<string, string> _clientToNodeMap = new();

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
            DirectNeighbors = localConfig.Neighbors.Select(NormalizeUrl).ToList()
        };
        _nodes.TryAdd(localConfig.NodeId, localNode);

        _logger?.LogInformation("Node {NodeId} initialized. Neighbors in config: {Count}",
            localConfig.NodeId, localConfig.Neighbors.Count);
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        return url.Trim().TrimEnd('/').ToLowerInvariant();
    }

    // --- ЛОГИКА КЛИЕНТОВ ---

    public void UpdateClientLocation(string clientId, string gatewayNodeId)
    {
        _clientToNodeMap[clientId] = gatewayNodeId;
    }

    /// <summary>
    /// Массовое обновление карты клиентов (при получении Gossip-пакета)
    /// </summary>
    public void SyncClientMap(Dictionary<string, string> remoteClientMap)
    {
        foreach (var (clientId, gatewayId) in remoteClientMap)
        {
            // Обновляем только если это не наш локальный клиент (наша инфа приоритетнее)
            // И если мы вообще знаем такую ноду-шлюз
            if (gatewayId != _localConfig.NodeId && _nodes.ContainsKey(gatewayId))
            {
                _clientToNodeMap[clientId] = gatewayId;
            }
        }
    }

    public string? GetClientGateway(string clientId)
    {
        _clientToNodeMap.TryGetValue(clientId, out var nodeId);
        return nodeId;
    }

    public Dictionary<string, string> GetClientMap() => _clientToNodeMap.ToDictionary(k => k.Key, v => v.Value);

    // --- ЛОГИКА НОД ---

    /// <summary>
    /// Метод для Gossip: обновляет знания о нескольких нодах сразу
    /// </summary>
    public void BatchUpdateNodes(IEnumerable<NodeInfo> incomingNodes)
    {
        foreach (var node in incomingNodes)
        {
            UpdateNode(node);
        }
    }

    public void UpdateNode(NodeInfo nodeInfo)
    {
        if (nodeInfo.NodeId == _localConfig.NodeId)
            return;

        var normalizedEndpoint = NormalizeUrl(nodeInfo.PublicEndpoint);

        if (normalizedEndpoint == NormalizeUrl(_localConfig.PublicEndpoint))
            return;

        nodeInfo.PublicEndpoint = normalizedEndpoint;

        // Если пришло извне, обновляем время, когда видели
        if (nodeInfo.LastSeen == default) nodeInfo.LastSeen = DateTime.UtcNow;

        lock (_lockObject)
        {
            // Проверка дубликатов по Endpoint
            var existingByEndpoint = _nodes.Values.FirstOrDefault(n =>
                n.NodeId != _localConfig.NodeId &&
                NormalizeUrl(n.PublicEndpoint) == normalizedEndpoint);

            if (existingByEndpoint != null && existingByEndpoint.NodeId != nodeInfo.NodeId)
            {
                // Если ID другой, но адрес тот же — удаляем старую запись (перерегистрация ноды)
                _nodes.TryRemove(existingByEndpoint.NodeId, out _);
            }

            // Логика защиты статуса Suspicious (не даем сразу стать Alive без проверки)
            if (_nodes.TryGetValue(nodeInfo.NodeId, out var current) &&
                current.Status == NodeStatus.Suspicious &&
                nodeInfo.Status == NodeStatus.Alive)
            {
                nodeInfo.Status = NodeStatus.Suspicious;
            }

            _nodes.AddOrUpdate(nodeInfo.NodeId, nodeInfo, (_, _) => nodeInfo);

            // DYNAMIC MESH: Если нода живая и "настоящая", добавляем в список прямых соседей
            if (!nodeInfo.NodeId.StartsWith("temp-") && nodeInfo.Status == NodeStatus.Alive)
            {
                if (_nodes.TryGetValue(_localConfig.NodeId, out var localNode))
                {
                    if (!localNode.DirectNeighbors.Contains(normalizedEndpoint))
                    {
                        localNode.DirectNeighbors.Add(normalizedEndpoint);
                        _logger?.LogInformation("Mesh expanded: added neighbor {NodeId}", nodeInfo.NodeId);
                    }
                }
            }
        }
    }

    // --- СТАНДАРТНЫЕ МЕТОДЫ (Get/Mark/Remove) ---

    public NodeInfo? GetNode(string nodeId) => _nodes.TryGetValue(nodeId, out var node) ? node : null;

    public List<NodeInfo> GetAllNodes() { lock (_lockObject) return _nodes.Values.ToList(); }

    public List<NodeInfo> GetAliveNodes() { lock (_lockObject) return _nodes.Values.Where(n => n.Status == NodeStatus.Alive).ToList(); }

    public void MarkNodeDead(string nodeId)
    {
        if (nodeId == _localConfig.NodeId) return;
        lock (_lockObject)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = NodeStatus.Dead;
                if (_nodes.TryGetValue(_localConfig.NodeId, out var localNode))
                    localNode.DirectNeighbors.Remove(NormalizeUrl(node.PublicEndpoint));
            }
        }
    }

    public void MarkNodeSuspicious(string nodeId)
    {
        if (nodeId == _localConfig.NodeId) return;
        lock (_lockObject)
        {
            if (_nodes.TryGetValue(nodeId, out var node) && node.Status == NodeStatus.Alive)
                node.Status = NodeStatus.Suspicious;
        }
    }

    public void MarkNodeAlive(string nodeId)
    {
        if (nodeId == _localConfig.NodeId) return;
        lock (_lockObject)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = NodeStatus.Alive;
                node.LastSeen = DateTime.UtcNow;
                if (_nodes.TryGetValue(_localConfig.NodeId, out var localNode))
                {
                    var url = NormalizeUrl(node.PublicEndpoint);
                    if (!localNode.DirectNeighbors.Contains(url)) localNode.DirectNeighbors.Add(url);
                }
            }
        }
    }

    public void UpdateLocalNodeStatus(NodeStatus status)
    {
        if (_nodes.TryGetValue(_localConfig.NodeId, out var localNode))
        {
            localNode.Status = status;
            localNode.LastSeen = DateTime.UtcNow;
        }
    }

    public bool RemoveNode(string nodeId)
    {
        if (nodeId == _localConfig.NodeId) return false;
        lock (_lockObject)
        {
            if (_nodes.TryRemove(nodeId, out var removed))
            {
                if (_nodes.TryGetValue(_localConfig.NodeId, out var localNode))
                    localNode.DirectNeighbors.Remove(NormalizeUrl(removed.PublicEndpoint));
                return true;
            }
            return false;
        }
    }

    public Dictionary<string, List<string>> BuildGraph()
    {
        lock (_lockObject)
        {
            var graph = new Dictionary<string, List<string>>();
            var allNodes = _nodes.Values.ToList();
            var routingExcluded = new HashSet<string>(
                allNodes.Where(n => n.Status == NodeStatus.Dead || n.Status == NodeStatus.Suspicious).Select(n => n.NodeId)
            );

            foreach (var node in allNodes)
            {
                if (routingExcluded.Contains(node.NodeId)) continue;
                if (!graph.ContainsKey(node.NodeId)) graph[node.NodeId] = new List<string>();

                foreach (var neighborEndpoint in node.DirectNeighbors)
                {
                    var neighbor = allNodes.FirstOrDefault(n =>
                        NormalizeUrl(n.PublicEndpoint) == NormalizeUrl(neighborEndpoint) &&
                        !routingExcluded.Contains(n.NodeId));

                    if (neighbor != null && neighbor.NodeId != node.NodeId)
                        graph[node.NodeId].Add(neighbor.NodeId);
                }
            }
            return graph;
        }
    }

    // =========================================================
    // Профили клиентов (Distributed Phonebook)
    // =========================================================

    /// <summary>
    /// Получить все профили для отправки соседям через Gossip
    /// </summary>
    public Dictionary<string, ClientProfile> GetProfiles()
    {
        lock (_profilesLock)
        {
            return _clientProfiles.ToDictionary(k => k.Key, v => v.Value);
        }
    }

    /// <summary>
    /// Локальное обновление профиля (когда клиент публикует свой профиль)
    /// </summary>
    public void UpdateClientProfile(ClientProfile profile)
    {
        lock (_profilesLock)
        {
            _clientProfiles[profile.NodeId] = profile;
        }

        _logger?.LogDebug("Profile updated locally: {NodeId} (@{Nickname})",
            profile.NodeId, profile.GlobalNickname);
    }

    /// <summary>
    /// Синхронизация профилей из сети (через Gossip, Last-Write-Wins)
    /// </summary>
    public void SyncProfiles(Dictionary<string, ClientProfile> remoteProfiles)
    {
        lock (_profilesLock)
        {
            foreach (var remote in remoteProfiles)
            {
                // Если у нас нет этого профиля ИЛИ профиль из сети новее - обновляем
                if (!_clientProfiles.TryGetValue(remote.Key, out var local) ||
                    remote.Value.LastUpdated > local.LastUpdated)
                {
                    _clientProfiles[remote.Key] = remote.Value;
                }
            }
        }
    }

    /// <summary>
    /// Поиск профилей по nickname или имени
    /// </summary>
    public List<ClientProfile> SearchProfiles(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<ClientProfile>();

        string lowerQuery = query.ToLowerInvariant().Trim();

        lock (_profilesLock)
        {
            return _clientProfiles.Values
                .Where(p => p.GlobalNickname.Contains(lowerQuery) ||
                            p.DisplayName.ToLowerInvariant().Contains(lowerQuery))
                .Take(50)
                .ToList();
        }
    }

    public string LocalNodeId => _localConfig.NodeId;
}