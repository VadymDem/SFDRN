using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Services;
using System.Diagnostics;

namespace SFDRN.Server.Controllers;

[ApiController]
[Route("mesh")]
public class MeshController : ControllerBase
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly ILogger<MeshController> _logger;
    private readonly DatabaseService _database;
    private readonly IServiceProvider _serviceProvider;

    public MeshController(
        NodeRegistry nodeRegistry,
        ILogger<MeshController> logger,
        DatabaseService database,
        IServiceProvider serviceProvider)
    {
        _nodeRegistry = nodeRegistry;
        _logger = logger;
        _database = database;
        _serviceProvider = serviceProvider;
    }

    [HttpPost("gossip")]
    public async Task<IActionResult> ReceiveGossip([FromBody] GossipMessage message)
    {
        _logger.LogInformation("Received gossip from {SenderId}. Nodes: {NodeCount}, Clients: {ClientCount}, Digests: {DigestCount}",
            message.SenderNodeId,
            message.KnownNodes?.Count ?? 0,
            message.ClientMap?.Count ?? 0,
            message.ProfileDigests?.Count ?? 0);  // ✅ Изменено на Digests

        // 1. Обновляем информацию об узлах (без изменений)
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

            _nodeRegistry.BatchUpdateNodes(deduplicatedIncoming);
        }

        // 2. Синхронизация клиентов (без изменений)
        if (message.ClientMap != null)
        {
            _nodeRegistry.SyncClientMap(message.ClientMap);
        }

        // 3. ✅ НОВОЕ: Обработка дайджестов профилей
        if (message.ProfileDigests != null && message.ProfileDigests.Any())
        {
            _logger.LogInformation("Processing {Count} profile digests from {SenderId}",
                message.ProfileDigests.Count, message.SenderNodeId);

            // Асинхронно проверяем какие профили нам нужны и запрашиваем их
            // (не блокируем ответ на Gossip)
            _ = Task.Run(async () =>
            {
                try
                {
                    var missingIds = await _database.GetMissingProfileIdsAsync(message.ProfileDigests);
                    if (missingIds.Any())
                    {
                        var senderNode = _nodeRegistry.GetNode(message.SenderNodeId);
                        if (senderNode != null)
                        {
                            // ✅ Получаем ProfileSyncService через IServiceProvider
                            using var scope = _serviceProvider.CreateScope();
                            var syncService = scope.ServiceProvider.GetRequiredService<ProfileSyncService>();

                            await syncService.PullProfilesBatchAsync(missingIds, senderNode.PublicEndpoint);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to pull profiles from {SenderId}", message.SenderNodeId);
                }
            });
        }

        // 4. Подготавливаем ответ
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

        // ✅ Возвращаем дайджесты вместо полных профилей
        var profileDigests = await _database.GetProfileDigestsAsync();

        return Ok(new GossipResponse
        {
            Success = true,
            KnownNodes = nodesToShare,
            ClientMap = _nodeRegistry.GetClientMap(),
            ProfileDigests = profileDigests  // ✅ Дайджесты вместо Profiles
        });
    }

    [HttpGet("profile/{nodeId}")]
    public async Task<IActionResult> GetProfile(string nodeId)
    {
        _logger.LogInformation("Profile pull request for {NodeId}", nodeId);

        var profile = await _database.GetProfileAsync(nodeId);

        if (profile == null)
        {
            return NotFound(new { error = "Profile not found", nodeId });
        }

        return Ok(profile);
    }

    [HttpPost("profiles/batch")]
    public async Task<IActionResult> GetProfilesBatch([FromBody] List<string> nodeIds)
    {
        _logger.LogInformation("Batch profile pull request for {Count} profiles", nodeIds.Count);

        var profiles = new List<ClientProfile>();

        foreach (var nodeId in nodeIds.Take(100)) // Лимит 100 профилей за раз
        {
            var profile = await _database.GetProfileAsync(nodeId);
            if (profile != null)
            {
                profiles.Add(profile);
            }
        }

        _logger.LogInformation("Returning {Count} profiles", profiles.Count);

        return Ok(profiles);
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