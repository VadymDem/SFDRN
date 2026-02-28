namespace SFDRN.Server.Models;

/// <summary>
/// Легковесный дайджест профиля для передачи через Gossip
/// Вместо передачи полных профилей (1KB+), передаем только метаданные (~100 байт)
/// </summary>
public class ProfileDigest
{
    /// <summary>
    /// ID клиента
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Время последнего обновления (для Last-Write-Wins)
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// SHA256 хеш профиля для быстрого сравнения
    /// Если хеши совпадают - профили идентичны, синхронизация не нужна
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Создать дайджест из StoredProfile
    /// </summary>
    public static ProfileDigest FromStoredProfile(Database.Models.StoredProfile profile)
    {
        return new ProfileDigest
        {
            NodeId = profile.NodeId,
            LastUpdated = profile.LastUpdated,
            Hash = profile.Hash
        };
    }
}