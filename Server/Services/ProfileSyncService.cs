using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using System.Text.Json;

namespace SFDRN.Server.Services;

/// <summary>
/// Сервис для Pull-based синхронизации профилей между нодами
/// Вместо того чтобы отправлять все профили через Gossip,
/// мы запрашиваем только те, которые отсутствуют или устарели
/// </summary>
public class ProfileSyncService : BackgroundService
{
    private readonly DatabaseService _database;
    private readonly NodeRegistry _nodeRegistry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProfileSyncService> _logger;
    private readonly TimeSpan _syncInterval = TimeSpan.FromMinutes(5);

    public ProfileSyncService(
        DatabaseService database,
        NodeRegistry nodeRegistry,
        IHttpClientFactory httpClientFactory,
        ILogger<ProfileSyncService> logger)
    {
        _database = database;
        _nodeRegistry = nodeRegistry;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProfileSyncService started");

        // Подождем 30 секунд после старта (чтобы Gossip успел обменяться дайджестами)
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncMissingProfilesAsync(stoppingToken);
                await Task.Delay(_syncInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in ProfileSyncService cycle");
            }
        }
    }

    /// <summary>
    /// Периодическая синхронизация: проверяем не появились ли новые профили у соседей
    /// </summary>
    private async Task SyncMissingProfilesAsync(CancellationToken cancellationToken)
    {
        // Пока заглушка - реальная логика будет в Pull-on-demand
        // Здесь можно добавить фоновую синхронизацию если нужно
    }

    /// <summary>
    /// Запросить конкретный профиль у ноды (Pull-модель)
    /// </summary>
    public async Task<ClientProfile?> PullProfileAsync(string nodeId, string gatewayEndpoint)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var url = $"{gatewayEndpoint}/mesh/profile/{nodeId}";
            _logger.LogInformation("Pulling profile {NodeId} from {Gateway}", nodeId, gatewayEndpoint);

            var response = await client.GetAsync(url, cancellationToken: default);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to pull profile {NodeId}: {StatusCode}",
                    nodeId, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var profile = JsonSerializer.Deserialize<ClientProfile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (profile != null)
            {
                // Сохраняем в БД
                await _database.SaveProfileAsync(profile);
                _logger.LogInformation("Profile pulled and saved: {NodeId} (@{Nickname})",
                    profile.NodeId, profile.GlobalNickname);
            }

            return profile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pulling profile {NodeId} from {Gateway}",
                nodeId, gatewayEndpoint);
            return null;
        }
    }

    /// <summary>
    /// Запросить пакет профилей (batch pull)
    /// </summary>
    public async Task<List<ClientProfile>> PullProfilesBatchAsync(
        List<string> nodeIds,
        string gatewayEndpoint)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var url = $"{gatewayEndpoint}/mesh/profiles/batch";
            _logger.LogInformation("Batch pulling {Count} profiles from {Gateway}",
                nodeIds.Count, gatewayEndpoint);

            var content = new StringContent(
                JsonSerializer.Serialize(nodeIds),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to batch pull profiles: {StatusCode}",
                    response.StatusCode);
                return new List<ClientProfile>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var profiles = JsonSerializer.Deserialize<List<ClientProfile>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<ClientProfile>();

            // Сохраняем все в БД
            foreach (var profile in profiles)
            {
                await _database.SaveProfileAsync(profile);
            }

            _logger.LogInformation("Batch pulled and saved {Count} profiles", profiles.Count);

            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error batch pulling profiles from {Gateway}", gatewayEndpoint);
            return new List<ClientProfile>();
        }
    }
}