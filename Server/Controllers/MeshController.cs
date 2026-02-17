using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using System.Diagnostics;

namespace SFDRN.Server.Controllers;

[ApiController]
[Route("mesh")]
public class MeshController : ControllerBase
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly ILogger<MeshController> _logger;

    public MeshController(NodeRegistry nodeRegistry, ILogger<MeshController> logger)
    {
        _nodeRegistry = nodeRegistry;
        _logger = logger;
    }

    [HttpPost("gossip")]
    public IActionResult ReceiveGossip([FromBody] GossipMessage message)
    {
        _logger.LogInformation("Received gossip from {SenderId} with {Count} nodes",
            message.SenderNodeId, message.KnownNodes?.Count ?? 0);

        // ✅ Сначала дедуплицируем входящие данные
        if (message.KnownNodes != null)
        {
            var deduplicatedIncoming = message.KnownNodes
                .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
                .Select(group =>
                {
                    // Предпочитаем реальные ID
                    var realNode = group.FirstOrDefault(n => !n.NodeId.StartsWith("temp-"));
                    return realNode ?? group.OrderByDescending(n => n.LastSeen).First();
                })
                .ToList();

            _logger.LogDebug("Deduplicated {Original} incoming nodes to {Final} unique entries",
                message.KnownNodes.Count, deduplicatedIncoming.Count);

            // Обновляем реестр только дедуплицированными узлами
            foreach (var node in deduplicatedIncoming)
            {
                if (node.NodeId != _nodeRegistry.LocalNodeId)
                {
                    _nodeRegistry.UpdateNode(node);
                }
            }
        }

        // ✅ Получаем все узлы из реестра и дедуплицируем для ответа
        var allNodes = _nodeRegistry.GetAllNodes();

        var deduplicatedNodes = allNodes
            .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
            .Select(group =>
            {
                var realNode = group.FirstOrDefault(n => !n.NodeId.StartsWith("temp-"));
                return realNode ?? group.OrderByDescending(n => n.LastSeen).First();
            })
            .ToList();

        // ✅ Исключаем себя из списка для отправки
        var nodesToShare = deduplicatedNodes
            .Where(n => n.NodeId != _nodeRegistry.LocalNodeId)
            .ToList();

        // ✅ Добавляем информацию о себе с правильным статусом
        var localNodeInfo = _nodeRegistry.GetNode(_nodeRegistry.LocalNodeId);
        if (localNodeInfo != null)
        {
            var localNodeCopy = new NodeInfo
            {
                NodeId = localNodeInfo.NodeId,
                Region = localNodeInfo.Region,
                PublicEndpoint = localNodeInfo.PublicEndpoint,
                Transports = localNodeInfo.Transports,
                LastSeen = DateTime.UtcNow,
                Status = NodeStatus.Alive
            };
            nodesToShare.Add(localNodeCopy);
        }

        _logger.LogDebug("Responding with {Count} deduplicated nodes (including self)", nodesToShare.Count);

        var response = new GossipResponse
        {
            KnownNodes = nodesToShare
        };

        return Ok(response);
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();

        // ✅ Дедуплицируем для правильного подсчета
        var allNodes = _nodeRegistry.GetAllNodes();
        var uniqueNodes = allNodes
            .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
            .Select(group => group.FirstOrDefault(n => !n.NodeId.StartsWith("temp-"))
                          ?? group.OrderByDescending(n => n.LastSeen).First())
            .ToList();

        return Ok(new
        {
            status = "healthy",
            nodeId = _nodeRegistry.LocalNodeId,
            uptime = uptime.ToString(@"hh\:mm\:ss"),
            activeTransports = new[] { "HTTPS", "WebSocket" },
            knownNodes = uniqueNodes.Count  // ✅ Теперь правильное число
        });
    }

    [HttpGet("network")]
    public IActionResult GetNetworkSnapshot()
    {
        var allNodes = _nodeRegistry.GetAllNodes();

        // ✅ Дедуплицируем узлы по эндпоинту для отображения
        var uniqueNodes = allNodes
            .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
            .Select(group =>
            {
                // Предпочитаем реальные имена, самые свежие
                var realNode = group.FirstOrDefault(n => !n.NodeId.StartsWith("temp-"));
                var selectedNode = realNode ?? group.OrderByDescending(n => n.LastSeen).First();

                // Логируем если есть дубликаты
                if (group.Count() > 1)
                {
                    _logger.LogWarning("Endpoint {Endpoint} has {Count} duplicate entries: {Ids}",
                        selectedNode.PublicEndpoint,
                        group.Count(),
                        string.Join(", ", group.Select(n => n.NodeId)));
                }

                return selectedNode;
            })
            .OrderBy(n => n.NodeId)
            .ToList();

        return Ok(new
        {
            localNodeId = _nodeRegistry.LocalNodeId,
            totalNodes = uniqueNodes.Count,
            aliveNodes = uniqueNodes.Count(n => n.Status == NodeStatus.Alive),
            deadNodes = uniqueNodes.Count(n => n.Status == NodeStatus.Dead),
            unknownNodes = uniqueNodes.Count(n => n.Status == NodeStatus.Unknown),
            nodes = uniqueNodes,
            packetsStored = 0,
            timestamp = DateTime.UtcNow
        });
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        return url.Trim().TrimEnd('/').ToLowerInvariant();
    }
}