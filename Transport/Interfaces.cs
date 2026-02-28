namespace SFDRN.Server.Transport;

/// <summary>
/// Абстракция транспорта для отправки сообщений между нодами
/// </summary>
public interface Interfaces
{
    /// <summary>
    /// Протокол транспорта
    /// </summary>
    NodeProtocol Protocol { get; }

    /// <summary>
    /// Отправить сообщение (fire-and-forget)
    /// </summary>
    Task<bool> SendAsync(NodeEndpoint node, NodeMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отправить сообщение и получить ответ (request-response)
    /// </summary>
    Task<NodeResponse?> RequestAsync(NodeEndpoint node, NodeMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверить доступность ноды
    /// </summary>
    Task<bool> PingAsync(NodeEndpoint node, CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверить, доступен ли транспорт
    /// </summary>
    bool IsAvailable { get; }
}

/// <summary>
/// Фабрика для создания транспорта
/// </summary>
public interface INodeTransportFactory
{
    /// <summary>
    /// Создать транспорт для ноды
    /// </summary>
    Interfaces Create(NodeEndpoint node);

    /// <summary>
    /// Создать транспорт по протоколу
    /// </summary>
    Interfaces Create(NodeProtocol protocol);

    /// <summary>
    /// Получить транспорт по умолчанию
    /// </summary>
    Interfaces GetDefault();
}