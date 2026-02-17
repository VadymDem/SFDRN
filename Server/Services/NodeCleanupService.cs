using SFDRN.Server.Mesh;
using SFDRN.Server.Models;

namespace SFDRN.Server.Services;

/// <summary>
/// Фоновый сервис для очистки временных placeholder узлов.
/// ВАЖНО: Real узлы НИКОГДА не удаляются, только меняют статус Alive/Dead.
/// </summary>
public class NodeCleanupService : BackgroundService
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly ILogger<NodeCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromSeconds(30);

    public NodeCleanupService(
        NodeRegistry nodeRegistry,
        ILogger<NodeCleanupService> logger)
    {
        _nodeRegistry = nodeRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NodeCleanupService started");

        // Даем сети время на инициализацию перед первой очисткой
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PerformCleanup();
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during node cleanup");
            }
        }
    }

    private void PerformCleanup()
    {
        var allNodes = _nodeRegistry.GetAllNodes();
        var removedCount = 0;

        // 🧹 ЕДИНСТВЕННОЕ правило удаления: temp узлы удаляются если есть real узел с тем же endpoint
        // Это placeholder'ы из конфига которые ждут первого успешного gossip
        var tempNodesToRemove = allNodes
            .Where(n => n.NodeId.StartsWith("temp-"))
            .Where(tempNode =>
            {
                // Проверяем: появился ли real узел с таким же endpoint?
                var hasRealNode = allNodes.Any(realNode =>
                    !realNode.NodeId.StartsWith("temp-") &&
                    NormalizeUrl(realNode.PublicEndpoint) == NormalizeUrl(tempNode.PublicEndpoint));

                return hasRealNode;
            })
            .ToList();

        foreach (var node in tempNodesToRemove)
        {
            _nodeRegistry.RemoveNode(node.NodeId);
            removedCount++;
            _logger.LogInformation("Removed temporary placeholder {TempId} for {Endpoint} (real node discovered)",
                node.NodeId, node.PublicEndpoint);
        }

        // ❌ Real узлы (не начинающиеся с temp-) НИКОГДА не удаляются!
        // Они представляют физические узлы сети (Германия, Чехия, Польша и т.д.)
        // Если узел недоступен - он помечается как Dead, но остается в реестре
        // При восстановлении связи он снова станет Alive

        if (removedCount > 0)
        {
            _logger.LogInformation("Cleanup completed: removed {Count} temporary placeholders", removedCount);
        }
        else
        {
            _logger.LogDebug("Cleanup completed: no temp nodes to remove");
        }

        // Логируем статистику по real узлам для мониторинга
        var realNodes = allNodes.Where(n => !n.NodeId.StartsWith("temp-")).ToList();
        var aliveCount = realNodes.Count(n => n.Status == NodeStatus.Alive);
        var deadCount = realNodes.Count(n => n.Status == NodeStatus.Dead);
        var unknownCount = realNodes.Count(n => n.Status == NodeStatus.Unknown);

        _logger.LogDebug("Network state: {Alive} alive, {Dead} dead, {Unknown} unknown nodes",
            aliveCount, deadCount, unknownCount);
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        return url.Trim().TrimEnd('/').ToLowerInvariant();
    }
}