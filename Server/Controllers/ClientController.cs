using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Database.Models;
using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Routing;
using SFDRN.Server.Services;
using SFDRN.Server.Storage;
using System.Net.Mime;
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

        var messageId = message.MessageId ?? Guid.NewGuid().ToString();
        var contentType = (MessageType)(message.ContentType ?? 0);

        // ═════════════════════════════════════════════════════════
        // ✅ PHASE 1.1: СОХРАНЯЕМ В БД СО СТАТУСОМ ReceivedByNode
        // ═════════════════════════════════════════════════════════
        var storedMessage = await _database.SaveMessageAsync(
            messageId,
            message.FromNodeId,
            message.ToNodeId,
            message.Payload,  // ← Это EncryptedPayload
            contentType);

        if (storedMessage == null)
        {
            _logger.LogError("[Send] ❌ Failed to save message to DB");
            return StatusCode(500, new { success = false, error = "Failed to store message" });
        }

        // Обновляем статус на Stored
        await _database.UpdateMessageStatusAsync(messageId, MessageStatus.Stored);
        _logger.LogInformation("[Send] ✅ Saved to DB: {MessageId}", messageId);

        var packet = new PacketEnvelope
        {
            PacketId = messageId,
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

            // ✅ Обновляем статус на Forwarded
            await _database.UpdateMessageStatusAsync(messageId, MessageStatus.Forwarded);

            await NotifyClient(message.ToNodeId, new
            {
                type = "new_message",
                messageId,
                from = packet.SourceNode
            });

            return Ok(new { success = true, method = "websocket", messageId });
        }

        // 2. Ищем, на какой ноде сидит клиент
        var gatewayId = _nodeRegistry.GetClientGateway(message.ToNodeId);

        _logger.LogInformation("[Send] GetClientGateway({To}) = {Gateway}", message.ToNodeId, gatewayId ?? "NULL");

        if (gatewayId != null)
        {
            if (gatewayId == _nodeRegistry.LocalNodeId)
            {
                _logger.LogInformation("[Send] ⚠️ Gateway is LOCAL, storing locally");
                _packetStorage.StorePacket(packet);
                return Ok(new { success = true, method = "local", messageId });
            }

            _logger.LogInformation("[Send] ✅ Routing to gateway {Gateway}", gatewayId);
            var result = await _routingEngine.RouteToClient(gatewayId, packet);

            if (result)
            {
                // ✅ Обновляем статус на Forwarded
                await _database.UpdateMessageStatusAsync(messageId, MessageStatus.Forwarded);
            }

            return Ok(new { success = result, method = "mesh", gateway = gatewayId, messageId });
        }

        // 3. Не знаем клиента - flooding
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

            // ✅ Обновляем статус на Forwarded
            await _database.UpdateMessageStatusAsync(messageId, MessageStatus.Forwarded);

            return Ok(new { success = true, method = "flood", neighbors = aliveNodes.Count, messageId });
        }

        // 4. Нет соседей - сохраняем offline
        _packetStorage.StorePacket(packet);
        return Ok(new { success = true, method = "stored_offline", messageId });
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
            Timestamp = p.CreatedAt,
            ContentType = 0  // ← Default: Text
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

            // ✅ Удаляем из ClientMap при отключении
            _nodeRegistry.RemoveClientLocation(nodeId);

            _logger.LogInformation("WebSocket disconnected: {NodeId}", nodeId);
        }
    }

    /// <summary>
    /// Получить профиль клиента по NodeId (ищет по всей сети)
    /// </summary>
    [HttpGet("profile/{nodeId}")]
    public async Task<IActionResult> GetProfile(string nodeId)
    {
        _logger.LogInformation("[GetProfile] Looking for: {NodeId}", nodeId);

        // 1. Ищем локально
        var profile = await _database.GetProfileAsync(nodeId);
        if (profile != null)
        {
            _logger.LogInformation("[GetProfile] Found locally: {DisplayName}", profile.DisplayName);
            return Ok(new ClientProfile
            {
                NodeId = profile.NodeId,
                DisplayName = profile.DisplayName,
                GlobalNickname = profile.GlobalNickname,
                Status = profile.Status,
                LastUpdated = profile.LastUpdated
            });
        }

        // 2. Ищем gateway клиента
        var gatewayId = _nodeRegistry.GetClientGateway(nodeId);
        if (string.IsNullOrEmpty(gatewayId))
        {
            _logger.LogWarning("[GetProfile] Client not found in ClientMap: {NodeId}", nodeId);
            return NotFound(new { error = "Client not found", nodeId });
        }

        // 3. Если клиент на этой ноде но профиля нет
        if (gatewayId == _nodeRegistry.LocalNodeId)
        {
            _logger.LogWarning("[GetProfile] Client is local but no profile: {NodeId}", nodeId);
            return NotFound(new { error = "Profile not found", nodeId });
        }

        // 4. Запрашиваем у удалённой ноды
        var gateway = _nodeRegistry.GetNode(gatewayId);
        if (gateway == null)
        {
            _logger.LogWarning("[GetProfile] Gateway not found: {GatewayId}", gatewayId);
            return NotFound(new { error = "Gateway not found", gatewayId });
        }

        try
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"{gateway.PublicEndpoint}/client/profile/{nodeId}";

            _logger.LogInformation("[GetProfile] Forwarding to: {Url}", url);

            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var remoteProfile = JsonSerializer.Deserialize<ClientProfile>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (remoteProfile != null)
                {
                    _logger.LogInformation("[GetProfile] Found on remote: {DisplayName}", remoteProfile.DisplayName);
                    return Ok(remoteProfile);
                }
            }

            _logger.LogWarning("[GetProfile] Remote lookup failed: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GetProfile] Remote request failed");
        }

        return NotFound(new { error = "Profile not found anywhere", nodeId });
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

    // === 1.1 MESSAGE RECEIPT CHAIN ===

    /// <summary>
    /// Получить статус сообщения
    /// </summary>
    [HttpGet("message/{messageId}/status")]
    public async Task<IActionResult> GetMessageStatus(string messageId)
    {
        var status = await _database.GetMessageStatusAsync(messageId);
        var history = await _database.GetMessageStatusHistoryAsync(messageId);

        return Ok(new
        {
            messageId,
            status = status.ToString(),
            statusValue = (int)status,
            history = history.Select(h => new
            {
                status = h.Status.ToString(),
                timestamp = h.Timestamp,
                nodeId = h.NodeId,
                details = h.Details
            })
        });
    }

    /// <summary>
    /// Пометить сообщение как доставленное (вызывается клиентом при получении)
    /// </summary>
    [HttpPost("message/{messageId}/delivered")]
    public async Task<IActionResult> MarkDelivered(string messageId, [FromBody] DeliveryAckRequest? request)
    {
        var success = await _database.MarkMessageDeliveredAsync(messageId, request?.NodeId);

        if (success)
        {
            _logger.LogInformation("[Delivery] Message {MessageId} marked as delivered", messageId);

            // Уведомляем отправителя о доставке
            // (можно реализовать через NotifyClient если отправитель онлайн)
        }

        return Ok(new { success, messageId, status = "Delivered" });
    }

    /// <summary>
    /// Пометить сообщение как прочитанное
    /// </summary>
    [HttpPost("message/{messageId}/read")]
    public async Task<IActionResult> MarkRead(string messageId, [FromBody] ReadAckRequest? request)
    {
        var success = await _database.MarkMessageReadAsync(messageId, request?.NodeId);

        if (success)
        {
            _logger.LogInformation("[Read] Message {MessageId} marked as read", messageId);

            // Уведомляем отправителя о прочтении
            // (можно реализовать через NotifyClient если отправитель онлайн)
        }

        return Ok(new { success, messageId, status = "Read" });
    }

    /// <summary>
    /// Batch mark messages as read
    /// </summary>
    [HttpPost("messages/read")]
    public async Task<IActionResult> MarkMultipleRead([FromBody] BatchReadRequest request)
    {
        var results = new List<object>();

        foreach (var messageId in request.MessageIds)
        {
            var success = await _database.MarkMessageReadAsync(messageId);
            results.Add(new { messageId, success });
        }

        return Ok(new { processed = results.Count, results });
    }

    // === 1.2 TTL & STATS ===

    /// <summary>
    /// Получить статистику сообщений на ноде
    /// </summary>
    [HttpGet("messages/stats")]
    public async Task<IActionResult> GetMessageStats()
    {
        var stats = await _database.GetMessageStatsAsync();
        return Ok(stats);
    }

    /// <summary>
    /// Принудительно запустить очистку просроченных сообщений (admin)
    /// </summary>
    [HttpPost("messages/cleanup")]
    public async Task<IActionResult> CleanupExpiredMessages()
    {
        var count = await _database.CleanupExpiredMessagesAsync();
        return Ok(new { cleaned = count, timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Получить pending сообщения с информацией о TTL
    /// </summary>
    [HttpGet("messages/{nodeId}/pending")]
    public async Task<IActionResult> GetPendingMessages(string nodeId)
    {
        var messages = await _database.GetUndeliveredMessagesAsync(nodeId);

        return Ok(new
        {
            nodeId,
            count = messages.Count,
            messages = messages.Select(m => new
            {
                messageId = m.MessageId,
                from = m.FromNodeId,
                status = m.Status.ToString(),
                timestamp = m.Timestamp,
                storedAt = m.StoredAt,
                ttlSeconds = m.TtlSeconds,
                expiresAt = m.StoredAt.AddSeconds(m.TtlSeconds),
                contentType = m.ContentType.ToString()
            })
        });
    }
}

// === MODELS ===

public class DeliveryAckRequest
{
    public string? NodeId { get; set; }
}

public class ReadAckRequest
{
    public string? NodeId { get; set; }
}

public class BatchReadRequest
{
    public List<string> MessageIds { get; set; } = new();
}

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
    public int? ContentType { get; set; }  
}

public class ClientProfileRequest
{
    public string NodeId { get; set; } = string.Empty;
    public string GlobalNickname { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "Hey! I'm using SFDRN";
}