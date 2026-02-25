using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Routing;
using SFDRN.Server.Services;
using SFDRN.Server.Storage;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SFDRN.Server.Controllers;

[ApiController]
[Route("client")]
public class ClientController : ControllerBase
{
    private readonly RoutingEngine _routingEngine;
    private readonly PacketStorage _packetStorage;
    private readonly NodeRegistry _nodeRegistry;
    private readonly ILogger<ClientController> _logger;
    private readonly DatabaseService _database;
    private readonly ProfileSyncService _profileSync;

    // ✅ Активные WebSocket соединения клиентов
    private static readonly Dictionary<string, WebSocket> _clientConnections = new();
    private static readonly object _connectionsLock = new();

    public ClientController(
        RoutingEngine routingEngine,
        PacketStorage packetStorage,
        NodeRegistry nodeRegistry,
        ILogger<ClientController> logger,
        DatabaseService database,           // ✅ Добавлено
        ProfileSyncService profileSync)     // ✅ Добавлено
    {
        _routingEngine = routingEngine;
        _packetStorage = packetStorage;
        _nodeRegistry = nodeRegistry;
        _logger = logger;
        _database = database;               // ✅ Добавлено
        _profileSync = profileSync;         // ✅ Добавлено
    }

    // =========================================================
    // Registration
    // =========================================================
    [HttpPost("register")]
    public IActionResult Register([FromBody] ClientRegistration request)
    {
        var clientNodeId = $"client-{request.DeviceId ?? Guid.NewGuid().ToString()}";

        // ✅ ОЧЕНЬ ВАЖНО: Регистрируем клиента в глобальной карте меша
        _nodeRegistry.UpdateClientLocation(clientNodeId, _nodeRegistry.LocalNodeId);

        _logger.LogInformation("Client registered and mapped to local node: {ClientId}", clientNodeId);

        return Ok(new ClientRegistrationResponse
        {
            NodeId = clientNodeId,
            GatewayEndpoint = _nodeRegistry.GetNode(_nodeRegistry.LocalNodeId)?.PublicEndpoint ?? "unknown",
            NetworkSize = _nodeRegistry.GetAliveNodes().Count
        });
    }

    // =========================================================
    // Send Message
    // =========================================================
    [HttpPost("send")]
    public async Task<IActionResult> SendMessage([FromBody] ClientMessage message)
    {
        _logger.LogInformation("═══════════════════════════════════");
        _logger.LogInformation("[Send] MessageId: {MessageId}", message.MessageId);
        _logger.LogInformation("[Send] From: {From}", message.FromNodeId);
        _logger.LogInformation("[Send] To: {To}", message.ToNodeId);
        _logger.LogInformation("[Send] LocalNodeId: {Local}", _nodeRegistry.LocalNodeId);

        var packet = new PacketEnvelope
        {
            PacketId = message.MessageId ?? Guid.NewGuid().ToString(),
            SourceNode = message.FromNodeId,
            DestinationNode = message.ToNodeId,
            EncryptedPayload = message.Payload,
            Ttl = 10
        };

        // 1. Проверяем WebSocket соединение
        if (_clientConnections.ContainsKey(message.ToNodeId))
        {
            _logger.LogInformation("[Send] Found WebSocket connection for {To}", message.ToNodeId);
            _packetStorage.StorePacket(packet);
            await NotifyClient(message.ToNodeId, new { type = "new_message", from = packet.SourceNode });
            return Ok(new { success = true, method = "websocket" });
        }

        // 2. Ищем, на какой ноде сидит клиент
        var gatewayId = _nodeRegistry.GetClientGateway(message.ToNodeId);

        _logger.LogInformation("[Send] GetClientGateway({To}) = {Gateway}", message.ToNodeId, gatewayId ?? "NULL");

        if (gatewayId != null)
        {
            if (gatewayId == _nodeRegistry.LocalNodeId)
            {
                _logger.LogInformation("[Send] ⚠️ WRONG: Gateway is LOCAL, storing locally");
                _packetStorage.StorePacket(packet);
                return Ok(new { success = true, method = "local" });
            }

            _logger.LogInformation("[Send] ✅ Routing to gateway {Gateway}", gatewayId);
            var result = await _routingEngine.RouteToClient(gatewayId, packet);
            return Ok(new { success = result, method = "mesh", gateway = gatewayId });
        }

        // 3. Не знаем клиента
        _logger.LogWarning("[Send] ❓ Unknown client, flooding");

        var aliveNodes = _nodeRegistry.GetAliveNodes()
            .Where(n => n.NodeId != _nodeRegistry.LocalNodeId)
            .ToList();

        _logger.LogInformation("[Send] Alive neighbors: {Count}", aliveNodes.Count);

        if (aliveNodes.Any())
        {
            foreach (var node in aliveNodes)
            {
                _logger.LogInformation("[Send] Flooding to {NodeId}: {Endpoint}", node.NodeId, node.PublicEndpoint);
                _ = _routingEngine.TryForward(node.PublicEndpoint, packet);
            }
            return Ok(new { success = true, method = "flood", neighbors = aliveNodes.Count });
        }

        _packetStorage.StorePacket(packet);
        return Ok(new { success = true, method = "stored_offline" });
    }

    // =========================================================
    // Get Messages (HTTP Polling)
    // =========================================================
    [HttpGet("messages/{nodeId}")]
    public IActionResult GetMessages(string nodeId)
    {
        var packets = _packetStorage.GetPacketsForNode(nodeId);

        var messages = packets.Select(p => new ClientMessage
        {
            MessageId = p.PacketId,
            FromNodeId = p.SourceNode,
            ToNodeId = p.DestinationNode,
            Payload = p.EncryptedPayload,
            Timestamp = p.CreatedAt
        }).ToList();

        _logger.LogInformation("Retrieved {Count} messages for {NodeId}",
            messages.Count, nodeId);

        return Ok(new
        {
            messages,
            count = messages.Count,
            timestamp = DateTime.UtcNow
        });
    }

    // =========================================================
    // Unread Count
    // =========================================================
    [HttpGet("unread/{nodeId}")]
    public IActionResult GetUnreadCount(string nodeId)
    {
        var count = _packetStorage.GetUnreadCount(nodeId);
        return Ok(new { nodeId, unreadCount = count });
    }

    // =========================================================
    // WebSocket for Push Notifications
    // =========================================================
    [HttpGet("ws/{nodeId}")]
    public async Task WebSocketHandler(string nodeId)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

        lock (_connectionsLock)
        {
            _clientConnections[nodeId] = webSocket;
        }

        _nodeRegistry.UpdateClientLocation(nodeId, _nodeRegistry.LocalNodeId);

        // ✅ ОТПРАВЛЯЕМ OFFLINE СООБЩЕНИЯ
        var pendingPackets = _packetStorage.GetPacketsForNode(nodeId);
        foreach (var packet in pendingPackets)
        {
            await SendWebSocketMessage(webSocket, new
            {
                type = "new_message",
                from = packet.SourceNode,
                packetId = packet.PacketId
            });
        }

        _logger.LogInformation("WebSocket connected: {NodeId}. Sent {Count} pending messages.",
            nodeId, pendingPackets.Count);

        try
        {
            await SendWebSocketMessage(webSocket, new
            {
                type = "connected",
                nodeId,
                gateway = _nodeRegistry.LocalNodeId,
                timestamp = DateTime.UtcNow
            });

            var buffer = new byte[1024];
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket error for {NodeId}", nodeId);
        }
        finally
        {
            lock (_connectionsLock)
            {
                _clientConnections.Remove(nodeId);
            }

            _logger.LogInformation("WebSocket disconnected: {NodeId}", nodeId);
        }
    }

    // =========================================================
    // Internal: Notify client via WebSocket
    // =========================================================
    public static async Task NotifyClient(string nodeId, object message)
    {
        WebSocket? socket;

        lock (_connectionsLock)
        {
            if (!_clientConnections.TryGetValue(nodeId, out socket))
                return;
        }

        if (socket.State == WebSocketState.Open)
        {
            await SendWebSocketMessage(socket, message);
        }
    }

    private static async Task SendWebSocketMessage(WebSocket socket, object message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }


    [HttpPost("profile")]
    public async Task<IActionResult> PublishProfile([FromBody] ClientProfileRequest request)
    {
        var profile = new ClientProfile
        {
            NodeId = request.NodeId,
            GlobalNickname = request.GlobalNickname.ToLowerInvariant().Trim(),
            DisplayName = request.DisplayName,
            Status = request.Status ?? "Hey! I'm using SFDRN",
            LastUpdated = DateTime.UtcNow
        };

        // ✅ Сохраняем в БД вместо in-memory
        var saved = await _database.SaveProfileAsync(profile);

        if (saved)
        {
            _logger.LogInformation("Profile published: {NodeId} (@{Nickname}) - {Status}",
                profile.NodeId, profile.GlobalNickname, profile.Status);
        }

        return Ok(new { success = saved });
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchProfiles(
    [FromQuery] string query,
    [FromQuery] bool isFallback = false)  // ← Новый параметр
    {
        // ✅ Ищем в локальной БД
        var results = await _database.SearchProfilesAsync(query);

        _logger.LogInformation("Profile search: '{Query}' → {Count} results (local DB)",
            query, results.Count);

        // ✅ FALLBACK: Только если это НЕ fallback запрос (чтобы избежать loop)
        // И нашли мало результатов
        if (!isFallback && results.Count < 5)
        {
            var aliveNodes = _nodeRegistry.GetAliveNodes()
                .Where(n => n.NodeId != _nodeRegistry.LocalNodeId)
                .Take(3)
                .ToList();

            foreach (var node in aliveNodes)
            {
                try
                {
                    var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    // ← Добавляем &isFallback=true чтобы остановить рекурсию
                    var url = $"{node.PublicEndpoint}/client/search?query={Uri.EscapeDataString(query)}&isFallback=true";

                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var remoteResults = System.Text.Json.JsonSerializer.Deserialize<List<ClientProfile>>(json,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (remoteResults != null)
                        {
                            foreach (var profile in remoteResults)
                            {
                                if (!results.Any(r => r.NodeId == profile.NodeId))
                                {
                                    await _database.SaveProfileAsync(profile);
                                    results.Add(profile);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to query neighbor {NodeId} for profiles: {Error}",
                        node.NodeId, ex.Message);
                }
            }

            _logger.LogInformation("Profile search with fallback: '{Query}' → {Count} total results",
                query, results.Count);
        }

        return Ok(results);
    }
}

// =========================================================
// Models
// =========================================================
public class ClientRegistration
{
    public string? DeviceId { get; set; }
    public string? Platform { get; set; }
}

public class ClientRegistrationResponse
{
    public string NodeId { get; set; } = string.Empty;
    public string GatewayEndpoint { get; set; } = string.Empty;
    public int NetworkSize { get; set; }
}

public class ClientMessage
{
    public string? MessageId { get; set; }
    public string FromNodeId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ClientProfileRequest
{
    public string NodeId { get; set; } = string.Empty;
    public string GlobalNickname { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "Hey! I'm using SFDRN";
}