using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Database.Models;
using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Routing;
using SFDRN.Server.Services;
using SFDRN.Server.Storage;

namespace SFDRN.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class RoutingController : ControllerBase
{
    private readonly RoutingEngine _routingEngine;
    private readonly PacketStorage _packetStorage;
    private readonly NodeRegistry _nodeRegistry;
    private readonly DatabaseService _database;
    private readonly ILogger<RoutingController> _logger;

    public RoutingController(
        RoutingEngine routingEngine,
        PacketStorage packetStorage,
        NodeRegistry nodeRegistry,
        DatabaseService database,
        ILogger<RoutingController> logger)
    {
        _routingEngine = routingEngine;
        _packetStorage = packetStorage;
        _nodeRegistry = nodeRegistry;
        _database = database;
        _logger = logger;
    }

    [HttpPost("forward")]
    public async Task<IActionResult> Forward([FromBody] PacketEnvelope packet)
    {
        _logger.LogInformation(
            "Packet {PacketId} received. Type={Type} {Source} → {Destination}",
            packet.PacketId, packet.Type, packet.SourceNode, packet.DestinationNode);

        // =========================
        // 1️⃣ ACK обработка
        // =========================
        if (packet.Type == PacketType.Ack)
        {
            _packetStorage.CompleteAck(packet.PacketId);

            // ✅ PHASE 1.1: Обновляем статус на Delivered
            await _database.MarkMessageDeliveredAsync(packet.PacketId, packet.SourceNode);

            _logger.LogInformation("ACK received for {PacketId}", packet.PacketId);
            return Ok();
        }

        // =========================
        // 2️⃣ PHASE 1.3: Дедупликация по MessageId
        // =========================
        var existingStatus = await _database.GetMessageStatusAsync(packet.PacketId);
        if (existingStatus != MessageStatus.Failed)
        {
            _logger.LogDebug("Duplicate packet {PacketId}, status: {Status}", packet.PacketId, existingStatus);

            // Отправляем ACK что сообщение уже обработано
            await SendAck(packet);
            return Ok();
        }

        // =========================
        // 3️⃣ PHASE 1.1: Сохраняем с статусом Stored
        // =========================
        var savedMessage = await _database.SaveMessageAsync(
            packet.PacketId,
            packet.SourceNode,
            packet.DestinationNode,
            packet.EncryptedPayload);

        if (savedMessage == null)
        {
            _logger.LogWarning("Failed to save packet {PacketId}", packet.PacketId);
            return BadRequest(new { error = "Failed to store message" });
        }

        // ✅ Проверяем TTL
        if (savedMessage.IsExpired)
        {
            _logger.LogWarning("Packet {PacketId} expired, skipping", packet.PacketId);
            return BadRequest(new { error = "Message expired" });
        }

        // =========================
        // 4️⃣ Проверяем ClientMap - может это наш клиент?
        // =========================
        var clientGateway = _nodeRegistry.GetClientGateway(packet.DestinationNode);

        if (clientGateway == _nodeRegistry.LocalNodeId)
        {
            // Клиент подключен к этой ноде!
            _packetStorage.StorePacket(packet);
            await SendAck(packet);

            // ✅ PHASE 1.1: Обновляем статус на Forwarded
            await _database.UpdateMessageStatusAsync(
                packet.PacketId,
                MessageStatus.Forwarded,
                _nodeRegistry.LocalNodeId,
                "Forwarded to local client");

            await ClientController.NotifyClient(packet.DestinationNode, new
            {
                type = "new_message",
                messageId = packet.PacketId,
                from = packet.SourceNode,
                timestamp = DateTime.UtcNow,
                status = "forwarded"
            });

            _logger.LogInformation("Packet {PacketId} delivered to local client {ClientId}",
                packet.PacketId, packet.DestinationNode);
            return Ok();
        }

        // =========================
        // 5️⃣ Если я получатель-нода (не клиент)
        // =========================
        if (packet.DestinationNode == _nodeRegistry.LocalNodeId)
        {
            _packetStorage.StorePacket(packet);
            await SendAck(packet);

            // ✅ PHASE 1.1: Обновляем статус
            await _database.UpdateMessageStatusAsync(
                packet.PacketId,
                MessageStatus.Delivered,
                _nodeRegistry.LocalNodeId,
                "Delivered to node");

            _logger.LogInformation("Packet {PacketId} delivered locally", packet.PacketId);
            return Ok();
        }

        // =========================
        // 6️⃣ Иначе маршрутизируем дальше
        // =========================

        // ✅ PHASE 1.1: Обновляем статус на Forwarded
        await _database.UpdateMessageStatusAsync(
            packet.PacketId,
            MessageStatus.Forwarded,
            _nodeRegistry.LocalNodeId,
            "Forwarding to next hop");

        var result = await _routingEngine.RoutePacket(packet);

        if (result.Success)
            return Ok(result);

        // ✅ PHASE 1.1: Если маршрутизация не удалась
        await _database.UpdateMessageStatusAsync(
            packet.PacketId,
            MessageStatus.Failed,
            _nodeRegistry.LocalNodeId,
            $"Routing failed: {result.Message}");

        return BadRequest(result);
    }

    private async Task SendAck(PacketEnvelope original)
    {
        var ack = new PacketEnvelope
        {
            PacketId = original.PacketId,
            Type = PacketType.Ack,
            SourceNode = _nodeRegistry.LocalNodeId,
            DestinationNode = original.SourceNode,
            Ttl = 10
        };

        await _routingEngine.RoutePacket(ack);
    }
}
