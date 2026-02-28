namespace SFDRN.Server.Transport;

/// <summary>
/// Протокол транспорта
/// </summary>
public enum NodeProtocol
{
    Http,
    Https,
    WebSocket,
    WebSocketSecure,
    Tcp,
    Quic
}

/// <summary>
/// Информация о ноде для подключения
/// </summary>
public class NodeEndpoint
{
    public string NodeId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public NodeProtocol Protocol { get; set; } = NodeProtocol.Http;
    public string? ApiKey { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Базовый URL для HTTP/HTTPS
    /// </summary>
    public string BaseUrl => Protocol switch
    {
        NodeProtocol.Http => $"http://{Host}:{Port}",
        NodeProtocol.Https => $"https://{Host}:{Port}",
        NodeProtocol.WebSocket => $"ws://{Host}:{Port}",
        NodeProtocol.WebSocketSecure => $"wss://{Host}:{Port}",
        _ => $"http://{Host}:{Port}"
    };

    /// <summary>
    /// WebSocket URL
    /// </summary>
    public string WebSocketUrl => Protocol switch
    {
        NodeProtocol.Http or NodeProtocol.Https
            => $"ws://{Host}:{Port}",
        NodeProtocol.WebSocket
            => $"ws://{Host}:{Port}",
        NodeProtocol.WebSocketSecure
            => $"wss://{Host}:{Port}",
        _ => $"ws://{Host}:{Port}"
    };
}

/// <summary>
/// Сообщение для отправки между нодами
/// </summary>
public class NodeMessage
{
    /// <summary>
    /// ID сообщения
    /// </summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// ID пакета (для compatibility с PacketEnvelope)
    /// </summary>
    public string? PacketId { get; set; }

    /// <summary>
    /// Отправитель
    /// </summary>
    public string FromNodeId { get; set; } = string.Empty;

    /// <summary>
    /// Получатель (нода)
    /// </summary>
    public string ToNodeId { get; set; } = string.Empty;

    /// <summary>
    /// Payload (зашифрованный)
    /// </summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Тип сообщения
    /// </summary>
    public NodeMessageType Type { get; set; } = NodeMessageType.Data;

    /// <summary>
    /// TTL (hops)
    /// </summary>
    public int Ttl { get; set; } = 10;

    /// <summary>
    /// Timestamp
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Дополнительные метаданные
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Конвертация из PacketEnvelope
    /// </summary>
    public static NodeMessage FromPacket(SFDRN.Server.Models.PacketEnvelope packet)
    {
        return new NodeMessage
        {
            PacketId = packet.PacketId,
            FromNodeId = packet.SourceNode,
            ToNodeId = packet.DestinationNode,
            Payload = packet.EncryptedPayload,
            Ttl = packet.Ttl,
            Timestamp = packet.CreatedAt
        };
    }
}

/// <summary>
/// Тип сообщения между нодами
/// </summary>
public enum NodeMessageType
{
    /// <summary>Данные (сообщение пользователя)</summary>
    Data = 0,

    /// <summary>Ping для проверки связи</summary>
    Ping = 1,

    /// <summary>Ответ на Ping</summary>
    Pong = 2,

    /// <summary>Синхронизация профилей</summary>
    ProfileSync = 3,

    /// <summary>Запрос профиля</summary>
    ProfileRequest = 4,

    /// <summary>Ответ с профилем</summary>
    ProfileResponse = 5,

    /// <summary>Обновление статуса сообщения</summary>
    StatusUpdate = 6,

    /// <summary>Broadcast сообщение</summary>
    Broadcast = 7
}

/// <summary>
/// Ответ от ноды
/// </summary>
public class NodeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public byte[]? Payload { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}