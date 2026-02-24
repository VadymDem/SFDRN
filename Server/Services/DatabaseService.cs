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

            // ✅ DEBUG: проверяем что БД видит
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
    // MESSAGES
    // =========================================================

    public async Task<bool> SaveMessageAsync(string messageId, string fromNodeId, string toNodeId, byte[] payload, int ttl = 10)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            context.Messages.Add(new StoredMessage
            {
                MessageId = messageId,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                EncryptedPayload = payload,
                Timestamp = DateTime.UtcNow,
                StoredAt = DateTime.UtcNow,
                Ttl = ttl
            });

            await context.SaveChangesAsync();
            return true;
        }
        catch { return false; }
    }

    public async Task<List<StoredMessage>> GetUndeliveredMessagesAsync(string nodeId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.Messages
                .Where(m => m.ToNodeId == nodeId && m.DeliveredAt == null)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();
        }
        catch { return new(); }
    }

    public async Task MarkMessageDeliveredAsync(string messageId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var message = await context.Messages.FindAsync(messageId);
            if (message != null)
            {
                message.DeliveredAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }
        catch { }
    }
}