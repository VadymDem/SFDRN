using SFDRN.Server.Services;

namespace SFDRN.Server.Background;

/// <summary>
/// Background service для автоматической очистки просроченных сообщений
/// </summary>
public class TTLCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TTLCleanupService> _logger;

    // Интервал очистки: каждые 30 минут
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(30);

    public TTLCleanupService(
        IServiceProvider serviceProvider,
        ILogger<TTLCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[TTL] Cleanup service started. Interval: {Interval}", _cleanupInterval);

        // Начальная задержка перед первым запуском (30 сек)
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TTL] Cleanup failed");
            }

            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("[TTL] Cleanup service stopped");
    }

    private async Task CleanupAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DatabaseService>();

        var count = await database.CleanupExpiredMessagesAsync();

        if (count > 0)
        {
            _logger.LogInformation("[TTL] Cleaned up {Count} expired messages", count);
        }

        // Логируем статистику
        var stats = await database.GetMessageStatsAsync();
        _logger.LogDebug("[TTL] Stats: Total={Total}, Pending={Pending}, Delivered={Delivered}, Expired={Expired}",
            stats.Total, stats.Pending, stats.Delivered, stats.Expired);
    }
}

/// <summary>
/// Extension method для регистрации сервиса
/// </summary>
public static class TTLCleanupExtensions
{
    public static IServiceCollection AddTTLCleanup(this IServiceCollection services)
    {
        services.AddHostedService<TTLCleanupService>();
        return services;
    }
}
