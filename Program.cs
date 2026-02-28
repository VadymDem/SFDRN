using Microsoft.EntityFrameworkCore;
using SFDRN.Server.Background;
using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Routing;
using SFDRN.Server.Services;
using SFDRN.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

var nodeConfigFile = Environment.GetEnvironmentVariable("SFDRN_NODE_CONFIG");
if (!string.IsNullOrEmpty(nodeConfigFile))
{
    builder.Configuration.AddJsonFile(nodeConfigFile, optional: false, reloadOnChange: true);
}

var nodeConfig = builder.Configuration.GetSection("Node").Get<NodeConfiguration>()
    ?? throw new InvalidOperationException("Node configuration is missing");

builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? nodeConfig.PublicEndpoint
);

var dataDirectory = builder.Configuration["Database:Path"] ?? "/app/data";
Directory.CreateDirectory(dataDirectory);
var dbPath = Path.Combine(dataDirectory, "sfdrn.db");
builder.Services.AddDbContextFactory<SFDRN.Server.Database.DatabaseContext>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<ProfileSyncService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ProfileSyncService>());
builder.Services.AddSingleton(nodeConfig);
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddHostedService<NodeCleanupService>();
builder.Services.AddSingleton<PacketStorage>();
builder.Services.AddSingleton<RoutingEngine>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<GossipService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddTTLCleanup();

var app = builder.Build();

// Инициализация БД (применение миграций)
using (var scope = app.Services.CreateScope())
{
    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SFDRN.Server.Database.DatabaseContext>>();
    await using var context = await contextFactory.CreateDbContextAsync();
    await context.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied");
}

// ✅ Enable WebSocket support
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseAuthorization();
app.MapControllers();

var actualUrl = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? nodeConfig.PublicEndpoint;
Console.WriteLine($"===========================================");
Console.WriteLine($"SFDRN Node Started");
Console.WriteLine($"Node ID: {nodeConfig.NodeId}");
Console.WriteLine($"Endpoint: {actualUrl}");
Console.WriteLine($"Neighbors: {nodeConfig.Neighbors.Count}");
Console.WriteLine($"Client API: {actualUrl}/client");
Console.WriteLine($"===========================================");

app.Run();