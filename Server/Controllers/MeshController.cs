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

            foreach (var node in deduplicatedIncoming)
            {
                if (node.NodeId != _nodeRegistry.LocalNodeId)
                    _nodeRegistry.UpdateNode(node);
            }
        }

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
                DirectNeighbors = localNodeInfo.DirectNeighbors // ✅
            });
        }

        return Ok(new GossipResponse { KnownNodes = nodesToShare });
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