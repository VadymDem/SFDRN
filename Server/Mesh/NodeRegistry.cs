using System.Collections.Concurrent;
using SFDRN.Server.Models;
using Microsoft.Extensions.Logging;

namespace SFDRN.Server.Mesh;

public class NodeRegistry
{
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new();
    private readonly NodeConfiguration _localConfig;
    private readonly ILogger<NodeRegistry>? _logger;

    // Объект блокировки для атомарных операций обновления
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
            Status = NodeStatus.Alive
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
        // ✅ КРИТИЧНО: Никогда не обновляем локальный узел из внешних источников!
        if (nodeInfo.NodeId == _localConfig.NodeId)
        {
            _logger?.LogDebug("Ignoring update for local node {NodeId}", nodeInfo.NodeId);
            return;
        }

        // Нормализуем данные сразу
        var normalizedEndpoint = NormalizeUrl(nodeInfo.PublicEndpoint);
        nodeInfo.PublicEndpoint = normalizedEndpoint;
        nodeInfo.LastSeen = DateTime.UtcNow;

        // !!! КРИТИЧНО: Блокируем весь процесс обновления для предотвращения гонок
        lock (_lockObject)
        {
            // 1. Находим ВСЕ существующие узлы с таким же эндпоинтом, но другим ID
            var duplicates = _nodes.Values
                .Where(n => n.NodeId != nodeInfo.NodeId &&
                            n.NodeId != _localConfig.NodeId && // ✅ Не трогаем локальный узел!
                            NormalizeUrl(n.PublicEndpoint) == normalizedEndpoint)
                .ToList();

            // 2. Решаем, какой ID оставить приоритетным
            bool incomingIsReal = !nodeInfo.NodeId.StartsWith("temp-");

            foreach (var dup in duplicates)
            {
                bool existingIsReal = !dup.NodeId.StartsWith("temp-");

                // Удаляем дубликат, если входящий приоритетнее
                if (incomingIsReal || !existingIsReal)
                {
                    if (_nodes.TryRemove(dup.NodeId, out _))
                    {
                        _logger?.LogDebug("Removed duplicate node {OldId} ({Endpoint}) to be replaced by {NewId}",
                            dup.NodeId, dup.PublicEndpoint, nodeInfo.NodeId);
                    }
                }
            }

            // 3. Проверяем, есть ли уже узел с этим эндпоинтом после чистки
            var existing = _nodes.Values.FirstOrDefault(n =>
                n.NodeId != _localConfig.NodeId && // ✅ Не трогаем локальный узел!
                NormalizeUrl(n.PublicEndpoint) == normalizedEndpoint);

            if (existing != null)
            {
                // Если уже есть узел (значит мы не удалили его выше, потому что он Real, а новый Temp)
                if (!incomingIsReal && !existing.NodeId.StartsWith("temp-"))
                {
                    // Новый - temp, Старый - real. Не перезаписываем имя!
                    // Просто обновляем статус и LastSeen у существующего
                    existing.LastSeen = nodeInfo.LastSeen;
                    existing.Status = nodeInfo.Status;
                    existing.Transports = nodeInfo.Transports;
                    return;
                }

                // В остальных случаях - разрешаем перезапись через AddOrUpdate ниже
                if (existing.NodeId != nodeInfo.NodeId)
                {
                    _nodes.TryRemove(existing.NodeId, out _);
                }
            }

            // 4. Добавляем или обновляем
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
        // Возвращаем копию, чтобы не ломать блокировку вне метода
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
        // ✅ Никогда не помечаем локальный узел как мертвый
        if (nodeId == _localConfig.NodeId)
        {
            _logger?.LogWarning("Attempted to mark local node as dead - ignoring");
            return;
        }

        lock (_lockObject)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = NodeStatus.Dead;
                _logger?.LogWarning("Marked node {NodeId} as dead", nodeId);
            }
        }
    }

    /// <summary>
    /// Обновляет статус локального узла (например, при старте/остановке транспортов)
    /// </summary>
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

    /// <summary>
    /// Удаляет узел из реестра (используется сервисом очистки)
    /// </summary>
    public bool RemoveNode(string nodeId)
    {
        // ✅ Никогда не удаляем локальный узел
        if (nodeId == _localConfig.NodeId)
        {
            _logger?.LogWarning("Attempted to remove local node - ignoring");
            return false;
        }

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

    public string LocalNodeId => _localConfig.NodeId;
}