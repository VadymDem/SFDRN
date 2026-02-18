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

builder.Services.AddSingleton(nodeConfig);
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddHostedService<NodeCleanupService>();
builder.Services.AddSingleton<PacketStorage>();
builder.Services.AddSingleton<RoutingEngine>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<GossipService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

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