using Microsoft.EntityFrameworkCore;
using SFDRN.Server.Database.Models;

namespace SFDRN.Server.Database;

public class DatabaseContext : DbContext
{
    public DbSet<StoredProfile> Profiles { get; set; }
    public DbSet<StoredMessage> Messages { get; set; }
    public DbSet<MessageStatusRecord> MessageStatusHistory { get; set; }

    // ✅ ЕДИНСТВЕННЫЙ конструктор
    public DatabaseContext(DbContextOptions<DatabaseContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // =========================================================
        // Profiles
        // =========================================================
        modelBuilder.Entity<StoredProfile>(entity =>
        {
            entity.HasKey(e => e.NodeId);

            entity.HasIndex(e => e.GlobalNickname)
                .HasDatabaseName("IX_Profiles_GlobalNickname");

            entity.HasIndex(e => e.LastUpdated)
                .HasDatabaseName("IX_Profiles_LastUpdated");

            entity.HasIndex(e => e.LastSeenAt)
                .HasDatabaseName("IX_Profiles_LastSeenAt");

            entity.HasIndex(e => e.Hash)
                .HasDatabaseName("IX_Profiles_Hash");

            entity.Property(e => e.Status)
                .HasDefaultValue("Hey! I'm using SFDRN");

            entity.Property(e => e.DiscoveredAt)
                .HasDefaultValueSql("datetime('now')");

            entity.Property(e => e.LastSeenAt)
                .HasDefaultValueSql("datetime('now')");
        });

        // =========================================================
        // Messages
        // =========================================================
        modelBuilder.Entity<StoredMessage>(entity =>
        {
            entity.HasKey(e => e.MessageId);

            // ✅ Индекс для поиска сообщений получателя по времени
            entity.HasIndex(e => new { e.ToNodeId, e.Timestamp })
                .HasDatabaseName("IX_Messages_Recipient_Timestamp");

            // ✅ Индекс для поиска недоставленных сообщений
            entity.HasIndex(e => new { e.ToNodeId, e.Status })
                .HasDatabaseName("IX_Messages_Recipient_Status");

            // ✅ Индекс для очистки по TTL
            entity.HasIndex(e => e.StoredAt)
                .HasDatabaseName("IX_Messages_StoredAt");

            // ✅ Индекс для дедупликации по хешу
            entity.HasIndex(e => e.ContentHash)
                .HasDatabaseName("IX_Messages_ContentHash")
                .IsUnique();

            // ✅ Индекс для поиска просроченных сообщений
            entity.HasIndex(e => new { e.StoredAt, e.TtlSeconds })
                .HasDatabaseName("IX_Messages_Expiration");

            entity.Property(e => e.Status)
                .HasDefaultValue(MessageStatus.Created);

            entity.Property(e => e.IsRead)
                .HasDefaultValue(false);

            entity.Property(e => e.TtlSeconds)
                .HasDefaultValue(604800); // 7 дней

            entity.Property(e => e.TtlHops)
                .HasDefaultValue(10);

            entity.Property(e => e.ContentType)
                .HasDefaultValue(MessageType.Text);

            entity.Property(e => e.StoredAt)
                .HasDefaultValueSql("datetime('now')");
        });

        // =========================================================
        // Message Status History
        // =========================================================
        modelBuilder.Entity<MessageStatusRecord>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.MessageId)
                .HasDatabaseName("IX_StatusHistory_MessageId");

            entity.HasIndex(e => new { e.MessageId, e.Timestamp })
                .HasDatabaseName("IX_StatusHistory_Message_Timestamp");

            entity.Property(e => e.Timestamp)
                .HasDefaultValueSql("datetime('now')");
        });
    }

    public async Task InitializeAsync()
    {
        await Database.MigrateAsync();
    }
}
