using Microsoft.EntityFrameworkCore;
using SFDRN.Server.Database.Models;

namespace SFDRN.Server.Database;

public class DatabaseContext : DbContext
{
    public DbSet<StoredProfile> Profiles { get; set; }
    public DbSet<StoredMessage> Messages { get; set; }

    private readonly string _dbPath;

    // Конструктор для runtime (через DI)
    public DatabaseContext(IConfiguration configuration)
    {
        var dataDirectory = configuration["Database:Path"] ?? "/app/data";
        Directory.CreateDirectory(dataDirectory);
        _dbPath = Path.Combine(dataDirectory, "sfdrn.db");
    }

    // ✅ Конструктор для design-time (миграции)
    public DatabaseContext(DbContextOptions<DatabaseContext> options)
    {
        _dbPath = "data/sfdrn.db";
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        }

#if DEBUG
        optionsBuilder.LogTo(Console.WriteLine, LogLevel.Information);
#endif
    }

    // OnModelCreating остаётся без изменений...
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ... твой существующий код
    }

    public async Task InitializeAsync()
    {
        await Database.MigrateAsync();
    }
}