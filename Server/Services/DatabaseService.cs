using Microsoft.EntityFrameworkCore;
using SFDRN.Server.Database.Models;
using SFDRN.Server.Models;

namespace SFDRN.Server.Services;

/// <summary>
/// Сервис для работы с SQLite БД (профили + сообщения)
/// </summary>
public class DatabaseService
{
    private readonly IDbContextFactory<Database.DatabaseContext> _contextFactory;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(
        IDbContextFactory<Database.DatabaseContext> contextFactory,
        ILogger<DatabaseService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    // =========================================================
    // PROFILES
    // =========================================================

    public async Task<bool> SaveProfileAsync(ClientProfile profile)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var existing = await context.Profiles
                .FirstOrDefaultAsync(p => p.NodeId == profile.NodeId);

            var now = DateTime.UtcNow;

            if (existing != null)
            {
                if (profile.LastUpdated <= existing.LastUpdated)
                {
                    existing.LastSeenAt = now;
                    await context.SaveChangesAsync();
                    return false;
                }

                existing.GlobalNickname = profile.GlobalNickname;
                existing.DisplayName = profile.DisplayName;
                existing.Status = profile.Status;
                existing.LastUpdated = profile.LastUpdated;
                existing.LastSeenAt = now;
                existing.UpdateHash();
            }
            else
            {
                var stored = new StoredProfile
                {
                    NodeId = profile.NodeId,
                    GlobalNickname = profile.GlobalNickname,
                    DisplayName = profile.DisplayName,
                    Status = profile.Status,
                    LastUpdated = profile.LastUpdated,
                    DiscoveredAt = now,
                    LastSeenAt = now
                };
                stored.UpdateHash();
                context.Profiles.Add(stored);
            }

            await context.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save profile {NodeId}", profile.NodeId);
            return false;
        }
    }

    public async Task<ClientProfile?> GetProfileAsync(string nodeId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var stored = await context.Profiles.FirstOrDefaultAsync(p => p.NodeId == nodeId);
            if (stored == null) return null;

            return new ClientProfile
            {
                NodeId = stored.NodeId,
                GlobalNickname = stored.GlobalNickname,
                DisplayName = stored.DisplayName,
                Status = stored.Status,
                LastUpdated = stored.LastUpdated
            };
        }
        catch { return null; }
    }

    public async Task<List<ClientProfile>> SearchProfilesAsync(string query, int limit = 50)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var lowerQuery = query.ToLowerInvariant().Trim();

            return await context.Profiles
                .Where(p => p.GlobalNickname.Contains(lowerQuery) ||
                            p.DisplayName.ToLower().Contains(lowerQuery))
                .OrderByDescending(p => p.LastSeenAt)
                .Take(limit)
                .Select(p => new ClientProfile
                {
                    NodeId = p.NodeId,
                    GlobalNickname = p.GlobalNickname,
                    DisplayName = p.DisplayName,
                    Status = p.Status,
                    LastUpdated = p.LastUpdated
                })
                .ToListAsync();
        }
        catch { return new(); }
    }

    public async Task<List<ProfileDigest>> GetProfileDigestsAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var count = await context.Profiles.CountAsync();
            _logger.LogInformation("GetProfileDigestsAsync: Found {Count} profiles in DB", count);

            var digests = await context.Profiles
                .Select(p => new ProfileDigest
                {
                    NodeId = p.NodeId,
                    LastUpdated = p.LastUpdated,
                    Hash = p.Hash
                })
                .ToListAsync();

            _logger.LogInformation("GetProfileDigestsAsync: Returning {Count} digests", digests.Count);
            return digests;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetProfileDigestsAsync FAILED!");
            return new();
        }
    }

    public async Task<List<string>> GetMissingProfileIdsAsync(List<ProfileDigest> remoteDigests)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var missingIds = new List<string>();

            foreach (var remote in remoteDigests)
            {
                var local = await context.Profiles.FirstOrDefaultAsync(p => p.NodeId == remote.NodeId);

                if (local == null || (local.Hash != remote.Hash && remote.LastUpdated > local.LastUpdated))
                {
                    missingIds.Add(remote.NodeId);
                }
            }

            return missingIds;
        }
        catch { return new(); }
    }

    // =========================================================
    // MESSAGES - Core Operations
    // =========================================================

    /// <summary>
    /// Сохранить сообщение с автоматическим вычислением хеша и статуса
    /// </summary>
    public async Task<StoredMessage?> SaveMessageAsync(
        string messageId,
        string fromNodeId,
        string toNodeId,
        byte[] payload,
        MessageType contentType = MessageType.Text,
        int? customTtl = null)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // ✅ PHASE 1.3: Проверяем дедупликацию по MessageId
            var existingById = await context.Messages.FindAsync(messageId);
            if (existingById != null)
            {
                _logger.LogInformation("[DB] Duplicate message by ID: {MessageId}", messageId);
                return existingById;
            }

            // Создаём сообщение
            var message = new StoredMessage
            {
                MessageId = messageId,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                EncryptedPayload = payload,
                Timestamp = DateTime.UtcNow,
                StoredAt = DateTime.UtcNow,
                ContentType = contentType,
                TtlSeconds = customTtl ?? MessageTTL.GetTTL(contentType),
                TtlHops = 10,
                Status = MessageStatus.Stored
            };

            // ✅ PHASE 1.3: Вычисляем хеш для дедупликации
            message.UpdateContentHash();

            // ✅ PHASE 1.3: Проверяем дедупликацию по ContentHash
            var existingByHash = await context.Messages
                .FirstOrDefaultAsync(m => m.ContentHash == message.ContentHash);

            if (existingByHash != null)
            {
                _logger.LogInformation("[DB] Duplicate message by hash: {Hash}", message.ContentHash);
                return existingByHash;
            }

            context.Messages.Add(message);

            // ✅ PHASE 1.1: Добавляем запись в историю статусов
            context.MessageStatusHistory.Add(new MessageStatusRecord
            {
                MessageId = messageId,
                Status = MessageStatus.Stored,
                Timestamp = DateTime.UtcNow,
                Details = "Message stored on node"
            });

            await context.SaveChangesAsync();

            _logger.LogInformation("[DB] Message saved: {MessageId}, Hash: {Hash}, TTL: {TTL}s",
                messageId, message.ContentHash[..8] + "...", message.TtlSeconds);

            return message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Failed to save message {MessageId}", messageId);
            return null;
        }
    }

    /// <summary>
    /// Получить недоставленные сообщения для клиента
    /// </summary>
    public async Task<List<StoredMessage>> GetUndeliveredMessagesAsync(string nodeId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var now = DateTime.UtcNow;

            return await context.Messages
                .Where(m => m.ToNodeId == nodeId &&
                            m.Status < MessageStatus.Delivered &&
                            !m.IsExpired)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();
        }
        catch { return new(); }
    }

    // =========================================================
    // MESSAGES - Status Chain (PHASE 1.1)
    // =========================================================

    /// <summary>
    /// Обновить статус сообщения
    /// </summary>
    public async Task<bool> UpdateMessageStatusAsync(
        string messageId,
        MessageStatus newStatus,
        string? nodeId = null,
        string? details = null)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var message = await context.Messages.FindAsync(messageId);
            if (message == null)
            {
                _logger.LogWarning("[DB] Message not found for status update: {MessageId}", messageId);
                return false;
            }

            var oldStatus = message.Status;
            message.Status = newStatus;

            // Обновляем временные метки
            if (newStatus == MessageStatus.Delivered)
            {
                message.DeliveredAt = DateTime.UtcNow;
            }
            else if (newStatus == MessageStatus.Read)
            {
                message.ReadAt = DateTime.UtcNow;
                message.IsRead = true;
            }

            // Добавляем запись в историю
            context.MessageStatusHistory.Add(new MessageStatusRecord
            {
                MessageId = messageId,
                Status = newStatus,
                Timestamp = DateTime.UtcNow,
                NodeId = nodeId,
                Details = details ?? $"Status changed: {oldStatus} → {newStatus}"
            });

            await context.SaveChangesAsync();

            _logger.LogInformation("[DB] Message status updated: {MessageId} → {Status}", messageId, newStatus);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Failed to update message status: {MessageId}", messageId);
            return false;
        }
    }

    /// <summary>
    /// Получить историю статусов сообщения
    /// </summary>
    public async Task<List<MessageStatusRecord>> GetMessageStatusHistoryAsync(string messageId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.MessageStatusHistory
                .Where(h => h.MessageId == messageId)
                .OrderBy(h => h.Timestamp)
                .ToListAsync();
        }
        catch { return new(); }
    }

    /// <summary>
    /// Получить текущий статус сообщения
    /// </summary>
    public async Task<MessageStatus> GetMessageStatusAsync(string messageId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var message = await context.Messages.FindAsync(messageId);
            return message?.Status ?? MessageStatus.Failed;
        }
        catch { return MessageStatus.Failed; }
    }

    /// <summary>
    /// Пометить сообщение как доставленное
    /// </summary>
    public async Task<bool> MarkMessageDeliveredAsync(string messageId, string? nodeId = null)
    {
        return await UpdateMessageStatusAsync(
            messageId,
            MessageStatus.Delivered,
            nodeId,
            "Message delivered to recipient");
    }

    /// <summary>
    /// Пометить сообщение как прочитанное
    /// </summary>
    public async Task<bool> MarkMessageReadAsync(string messageId, string? nodeId = null)
    {
        return await UpdateMessageStatusAsync(
            messageId,
            MessageStatus.Read,
            nodeId,
            "Message read by recipient");
    }

    // =========================================================
    // MESSAGES - TTL Cleanup (PHASE 1.2)
    // =========================================================

    /// <summary>
    /// Удалить просроченные сообщения
    /// </summary>
    public async Task<int> CleanupExpiredMessagesAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var now = DateTime.UtcNow;

            // Находим просроченные сообщения
            var expiredMessages = await context.Messages
                .Where(m => m.StoredAt.AddSeconds(m.TtlSeconds) < now)
                .ToListAsync();

            if (!expiredMessages.Any())
            {
                return 0;
            }

            // Удаляем историю статусов
            var expiredIds = expiredMessages.Select(m => m.MessageId).ToList();
            var statusHistory = await context.MessageStatusHistory
                .Where(h => expiredIds.Contains(h.MessageId))
                .ToListAsync();

            context.MessageStatusHistory.RemoveRange(statusHistory);
            context.Messages.RemoveRange(expiredMessages);

            await context.SaveChangesAsync();

            _logger.LogInformation("[DB] Cleaned up {Count} expired messages", expiredMessages.Count);

            return expiredMessages.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Failed to cleanup expired messages");
            return 0;
        }
    }

    /// <summary>
    /// Получить статистику сообщений
    /// </summary>
    public async Task<MessageStats> GetMessageStatsAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var now = DateTime.UtcNow;

            return new MessageStats
            {
                Total = await context.Messages.CountAsync(),
                Pending = await context.Messages.CountAsync(m => m.Status < MessageStatus.Delivered),
                Delivered = await context.Messages.CountAsync(m => m.Status >= MessageStatus.Delivered),
                Read = await context.Messages.CountAsync(m => m.IsRead),
                Expired = await context.Messages.CountAsync(m => m.StoredAt.AddSeconds(m.TtlSeconds) < now)
            };
        }
        catch
        {
            return new MessageStats();
        }
    }

    /// <summary>
    /// Получить сообщение по ID
    /// </summary>
    public async Task<StoredMessage?> GetMessageAsync(string messageId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.Messages.FindAsync(messageId);
        }
        catch { return null; }
    }

    // =========================================================
    // MESSAGES - Deduplication (PHASE 1.3)
    // =========================================================

    /// <summary>
    /// Проверить, было ли сообщение уже обработано (по ContentHash)
    /// </summary>
    public async Task<bool> IsMessageDuplicateAsync(string contentHash)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.Messages
                .AnyAsync(m => m.ContentHash == contentHash);
        }
        catch { return false; }
    }

    /// <summary>
    /// Получить сообщение по хешу контента
    /// </summary>
    public async Task<StoredMessage?> GetMessageByHashAsync(string contentHash)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.Messages
                .FirstOrDefaultAsync(m => m.ContentHash == contentHash);
        }
        catch { return null; }
    }
}

/// <summary>
/// Статистика сообщений
/// </summary>
public class MessageStats
{
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Delivered { get; set; }
    public int Read { get; set; }
    public int Expired { get; set; }
}
