using SFDRN.Server.Transport;

namespace SFDRN.Server.Configuration;

/// <summary>
/// Расширения для регистрации транспортных сервисов
/// </summary>
public static class TransportServiceExtensions
{
    /// <summary>
    /// Добавить транспортные сервисы в DI
    /// </summary>
    public static IServiceCollection AddNodeTransport(this IServiceCollection services)
    {
        // ═════════════════════════════════════════════════════════
        // HttpClient для HTTP транспорта
        // ═════════════════════════════════════════════════════════

        services.AddHttpClient("NodeHttp", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "SFDRN-Node/1.0");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient("NodeHttps", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "SFDRN-Node/1.0");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // ═════════════════════════════════════════════════════════
        // Транспортные сервисы
        // ═════════════════════════════════════════════════════════

        services.AddSingleton<INodeTransportFactory, NodeTransportFactory>();
        services.AddSingleton<NodeTransportService>();

        _ = services; // Для future расширений

        return services;
    }

    /// <summary>
    /// Добавить HTTPS транспорт с mTLS (для будущего)
    /// </summary>
    public static IServiceCollection AddNodeTransportWithMtls(
        this IServiceCollection services,
        string certificatePath,
        string certificatePassword)
    {
        // TODO: Реализовать mTLS конфигурацию
        // var handler = new HttpClientHandler();
        // handler.ClientCertificates.Add(new X509Certificate2(certificatePath, certificatePassword));

        services.AddNodeTransport();

        return services;
    }
}
