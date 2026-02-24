using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SFDRN.Server.Database.Models;

/// <summary>
/// Профиль клиента в распределенной телефонной книге (SQLite)
/// </summary>
[Table("Profiles")]
public class StoredProfile
{
    [Key]
    [MaxLength(100)]
    public string NodeId { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string GlobalNickname { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Status { get; set; } = "Hey! I'm using SFDRN";

    /// <summary>
    /// Время последнего обновления профиля (от клиента)
    /// Используется для Last-Write-Wins conflict resolution
    /// </summary>
    [Required]
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// SHA256 хеш профиля для быстрого сравнения в Gossip
    /// Формат: SHA256(NodeId + GlobalNickname + DisplayName + Status + LastUpdated)
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Когда впервые обнаружили этот профиль в сети
    /// </summary>
    [Required]
    public DateTime DiscoveredAt { get; set; }

    /// <summary>
    /// Последний раз когда видели этот профиль в Gossip
    /// Используется для удаления устаревших профилей
    /// </summary>
    [Required]
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// Аватар в Base64 (опционально)
    /// </summary>
    [MaxLength(100000)]
    public string? Avatar { get; set; }

    /// <summary>
    /// Вычисляет хеш профиля для Gossip дайджеста
    /// </summary>
    public string ComputeHash()
    {
        var data = $"{NodeId}|{GlobalNickname}|{DisplayName}|{Status}|{LastUpdated:O}";
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Обновить хеш на основе текущих данных
    /// </summary>
    public void UpdateHash()
    {
        Hash = ComputeHash();
    }
}