using Microsoft.EntityFrameworkCore;
using SFDRN.Server.Database.Models;

namespace SFDRN.Server.Database;

public class DatabaseContext : DbContext
{
    public DbSet<StoredProfile> Profiles { get; set; }
    public DbSet<StoredMessage> Messages { get; set; }

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

            entity.HasIndex(e => new { e.ToNodeId, e.Timestamp })
                .HasDatabaseName("IX_Messages_Recipient_Timestamp");

            entity.HasIndex(e => new { e.ToNodeId, e.DeliveredAt })
                .HasDatabaseName("IX_Messages_Recipient_Delivered");

            entity.HasIndex(e => e.StoredAt)
                .HasDatabaseName("IX_Messages_StoredAt");

            entity.Property(e => e.IsRead)
                .HasDefaultValue(false);

            entity.Property(e => e.Ttl)
                .HasDefaultValue(10);

            entity.Property(e => e.StoredAt)
                .HasDefaultValueSql("datetime('now')");
        });
    }

    public async Task InitializeAsync()
    {
        await Database.MigrateAsync();
    }
}