using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SFDRN.Server.Database;

/// <summary>
/// Design-time factory для EF Core миграций
/// Нужен чтобы 'dotnet ef migrations' мог создать DbContext без запуска приложения
/// </summary>
public class DatabaseContextFactory : IDesignTimeDbContextFactory<DatabaseContext>
{
    public DatabaseContext CreateDbContext(string[] args)
    {
        // Путь к БД при миграциях (можно любой, это только для scaffold)
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "sfdrn.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var optionsBuilder = new DbContextOptionsBuilder<DatabaseContext>();
        optionsBuilder.UseSqlite($"Data Source={dbPath}");

        return new DatabaseContext(optionsBuilder.Options);
    }
}