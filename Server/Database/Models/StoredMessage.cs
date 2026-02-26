using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;

namespace SFDRN.Server.Database.Models;

/// <summary>
/// Статусы сообщения в цепочке доставки
/// </summary>
public enum MessageStatus
{
    Created = 0,      // = Sending
    ReceivedByNode = 1,
    Stored = 2,
    Forwarded = 3,
    Delivered = 4,    // = Delivered
    Read = 5,         // = Read
    Failed = 99
}

/// <summary>
/// Сообщение в хранилище ноды (для offline delivery)
/// </summary>
[Table("Messages")]
public class StoredMessage
{
    [Key]
    [MaxLength(100)]
    public string MessageId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string FromNodeId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string ToNodeId { get; set; } = string.Empty;

    /// <summary>
    /// Зашифрованный payload сообщения
    /// </summary>
    [Required]
    public byte[] EncryptedPayload { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// SHA256 хеш контента для дедупликации
    /// Формат: SHA256(MessageId + FromNodeId + ToNodeId + EncryptedPayload)
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Когда сообщение было создано отправителем
    /// </summary>
    [Required]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Текущий статус сообщения в цепочке доставки
    /// </summary>
    [Required]
    public MessageStatus Status { get; set; } = MessageStatus.Created;

    /// <summary>
    /// Когда сообщение было доставлено получателю (null если не доставлено)
    /// </summary>
    public DateTime? DeliveredAt { get; set; }

    /// <summary>
    /// Когда сообщение было прочитано (null если не прочитано)
    /// </summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>
    /// Прочитано ли получателем
    /// </summary>
    [Required]
    public bool IsRead { get; set; } = false;

    /// <summary>
    /// Когда сообщение попало на эту ноду (для очистки старых)
    /// </summary>
    [Required]
    public DateTime StoredAt { get; set; }

    /// <summary>
    /// TTL сообщения в секундах (время жизни)
    /// Default: 7 дней = 604800 секунд
    /// </summary>
    [Required]
    public int TtlSeconds { get; set; } = 604800;

    /// <summary>
    /// TTL hop count (количество оставшихся прыжков)
    /// </summary>
    [Required]
    public int TtlHops { get; set; } = 10;

    /// <summary>
    /// Тип контента (влияет на TTL)
    /// </summary>
    [Required]
    public MessageType ContentType { get; set; } = MessageType.Text;

    /// <summary>
    /// Истек ли срок жизни сообщения
    /// </summary>
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow > StoredAt.AddSeconds(TtlSeconds);

    /// <summary>
    /// Вычисляет хеш контента для дедупликации
    /// </summary>
    public string ComputeContentHash()
    {
        var data = $"{MessageId}|{FromNodeId}|{ToNodeId}|{Convert.ToBase64String(EncryptedPayload)}";
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(data);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Обновить хеш на основе текущих данных
    /// </summary>
    public void UpdateContentHash()
    {
        ContentHash = ComputeContentHash();
    }
}

/// <summary>
/// Тип контента сообщения (влияет на TTL)
/// </summary>
public enum MessageType
{
    Text = 0,
    Image = 1,
    Audio = 2,
    Video = 3,
    File = 4,
    System = 5
}

/// <summary>
/// Запись в истории статусов сообщения
/// </summary>
[Table("MessageStatusHistory")]
public class MessageStatusRecord
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string MessageId { get; set; } = string.Empty;

    [Required]
    public MessageStatus Status { get; set; }

    [Required]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// ID ноды которая установила этот статус
    /// </summary>
    [MaxLength(100)]
    public string? NodeId { get; set; }

    /// <summary>
    /// Дополнительная информация
    /// </summary>
    [MaxLength(500)]
    public string? Details { get; set; }
}

/// <summary>
/// TTL настройки по типу контента
/// </summary>
public static class MessageTTL
{
    /// <summary>Текст: 7 дней</summary>
    public const int TextSeconds = 604800;

    /// <summary>Изображения: 48 часов</summary>
    public const int ImageSeconds = 172800;

    /// <summary>Аудио: 48 часов</summary>
    public const int AudioSeconds = 172800;

    /// <summary>Видео: 24 часа</summary>
    public const int VideoSeconds = 86400;

    /// <summary>Файлы: 24 часа</summary>
    public const int FileSeconds = 86400;

    /// <summary>Системные: 30 дней</summary>
    public const int SystemSeconds = 2592000;

    /// <summary>
    /// Получить TTL для типа контента
    /// </summary>
    public static int GetTTL(MessageType type) => type switch
    {
        MessageType.Text => TextSeconds,
        MessageType.Image => ImageSeconds,
        MessageType.Audio => AudioSeconds,
        MessageType.Video => VideoSeconds,
        MessageType.File => FileSeconds,
        MessageType.System => SystemSeconds,
        _ => TextSeconds
    };
}
