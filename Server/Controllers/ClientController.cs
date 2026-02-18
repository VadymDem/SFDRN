using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Routing;
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

    // ✅ Активные WebSocket соединения клиентов
    private static readonly Dictionary<string, WebSocket> _clientConnections = new();
    private static readonly object _connectionsLock = new();

    public ClientController(
        RoutingEngine routingEngine,
        PacketStorage packetStorage,
        NodeRegistry nodeRegistry,
        ILogger<ClientController> logger)
    {
        _routingEngine = routingEngine;
        _packetStorage = packetStorage;
        _nodeRegistry = nodeRegistry;
        _logger = logger;
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
        var packet = new PacketEnvelope
        {
            PacketId = message.MessageId ?? Guid.NewGuid().ToString(),
            SourceNode = message.FromNodeId,
            DestinationNode = message.ToNodeId,
            EncryptedPayload = message.Payload,
            Ttl = 10
        };

        // 1. Проверяем, не наш ли это клиент (WebSocket)
        if (_clientConnections.ContainsKey(message.ToNodeId))
        {
            _packetStorage.StorePacket(packet);
            await NotifyClient(message.ToNodeId, new { type = "new_message", from = packet.SourceNode });
            return Ok(new { success = true });
        }

        // 2. Ищем, на какой ноде сидит клиент
        var gatewayId = _nodeRegistry.GetClientGateway(message.ToNodeId);

        if (gatewayId != null)
        {
            // Клиент на другой ноде — шлем туда через меш
            var result = await _routingEngine.RouteToClient(gatewayId, packet);
            return Ok(new { success = result });
        }

        // 3. Если вообще не знаем клиента — Flooding (рассылка всем живым нодам)
        _logger.LogWarning("Unknown client {To}. Flooding to all neighbors.", message.ToNodeId);
        var aliveNodes = _nodeRegistry.GetAliveNodes().Where(n => n.NodeId != _nodeRegistry.LocalNodeId);

        foreach (var node in aliveNodes)
        {
            _ = _routingEngine.TryForward(node.PublicEndpoint, packet); // Пожар и забыл
        }

        return Ok(new { success = true, status = "broadcasted" });
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

        // ✅ Сообщаем меш-сети, что клиент теперь активен через НАС
        _nodeRegistry.UpdateClientLocation(nodeId, _nodeRegistry.LocalNodeId);

        _logger.LogInformation("WebSocket connected: {NodeId}. Mesh updated.", nodeId);

        try
        {
            // Отправляем приветствие
            await SendWebSocketMessage(webSocket, new
            {
                type = "connected",
                nodeId,
                timestamp = DateTime.UtcNow
            });

            // Держим соединение открытым и слушаем ping/pong
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