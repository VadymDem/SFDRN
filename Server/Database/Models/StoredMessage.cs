using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SFDRN.Server.Database.Models;

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
    /// Когда сообщение было создано
    /// </summary>
    [Required]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Когда сообщение было доставлено получателю (null если не доставлено)
    /// </summary>
    public DateTime? DeliveredAt { get; set; }

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
    /// TTL сообщения (количество оставшихся прыжков)
    /// </summary>
    [Required]
    public int Ttl { get; set; } = 10;

    // Navigation properties (опционально для Foreign Keys)
    // public StoredProfile? FromProfile { get; set; }
    // public StoredProfile? ToProfile { get; set; }
}