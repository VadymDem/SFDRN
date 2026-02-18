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
        _logger.LogInformation("Received gossip from {SenderId}. Nodes: {NodeCount}, Clients: {ClientCount}",
            message.SenderNodeId,
            message.KnownNodes?.Count ?? 0,
            message.ClientMap?.Count ?? 0);

        // 1. Обновляем информацию об узлах
        if (message.KnownNodes != null)
        {
            var deduplicatedIncoming = message.KnownNodes
                .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
                .Select(group =>
                {
                    var realNode = group.FirstOrDefault(n => !n.NodeId.StartsWith("temp-"));
                    return realNode ?? group.OrderByDescending(n => n.LastSeen).First();
                })
                .ToList();

            // Используем BatchUpdate, который мы добавили в NodeRegistry
            _nodeRegistry.BatchUpdateNodes(deduplicatedIncoming);
        }

        // 2. ✅ СИНХРОНИЗАЦИЯ КЛИЕНТОВ
        if (message.ClientMap != null)
        {
            _nodeRegistry.SyncClientMap(message.ClientMap);
        }

        // 3. Подготавливаем ответ (наши знания о сети)
        var allNodes = _nodeRegistry.GetAllNodes();
        var nodesToShare = allNodes
            .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
            .Select(group =>
            {
                var realNode = group.FirstOrDefault(n => !n.NodeId.StartsWith("temp-"));
                return realNode ?? group.OrderByDescending(n => n.LastSeen).First();
            })
            .Where(n => n.NodeId != _nodeRegistry.LocalNodeId)
            .ToList();

        var localNodeInfo = _nodeRegistry.GetNode(_nodeRegistry.LocalNodeId);
        if (localNodeInfo != null)
        {
            nodesToShare.Add(new NodeInfo
            {
                NodeId = localNodeInfo.NodeId,
                Region = localNodeInfo.Region,
                PublicEndpoint = localNodeInfo.PublicEndpoint,
                Transports = localNodeInfo.Transports,
                LastSeen = DateTime.UtcNow,
                Status = NodeStatus.Alive,
                DirectNeighbors = localNodeInfo.DirectNeighbors
            });
        }

        // Возвращаем и ноды, и нашу карту клиентов
        return Ok(new GossipResponse
        {
            KnownNodes = nodesToShare,
            ClientMap = _nodeRegistry.GetClientMap() // ✅ Отдаем актуальную карту клиентов
        });
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();

        var uniqueCount = _nodeRegistry.GetAllNodes()
            .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
            .Count();

        return Ok(new
        {
            status = "healthy",
            nodeId = _nodeRegistry.LocalNodeId,
            uptime = uptime.ToString(@"hh\:mm\:ss"),
            activeTransports = new[] { "HTTPS", "WebSocket" },
            knownNodes = uniqueCount
        });
    }

    [HttpGet("network")]
    public IActionResult GetNetworkSnapshot()
    {
        var allNodes = _nodeRegistry.GetAllNodes();

        var uniqueNodes = allNodes
            .GroupBy(n => NormalizeUrl(n.PublicEndpoint))
            .Select(group =>
            {
                var realNode = group.FirstOrDefault(n => !n.NodeId.StartsWith("temp-"));
                return realNode ?? group.OrderByDescending(n => n.LastSeen).First();
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