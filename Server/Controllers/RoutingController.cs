using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Mesh;
using SFDRN.Server.Models;
using SFDRN.Server.Routing;
using SFDRN.Server.Storage;

namespace SFDRN.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class RoutingController : ControllerBase
{
    private readonly RoutingEngine _routingEngine;
    private readonly PacketStorage _packetStorage;
    private readonly NodeRegistry _nodeRegistry;
    private readonly ILogger<RoutingController> _logger;

    public RoutingController(
        RoutingEngine routingEngine,
        PacketStorage packetStorage,
        NodeRegistry nodeRegistry,
        ILogger<RoutingController> logger)
    {
        _routingEngine = routingEngine;
        _packetStorage = packetStorage;
        _nodeRegistry = nodeRegistry;
        _logger = logger;
    }

    [HttpPost("forward")]
    public async Task<IActionResult> Forward([FromBody] PacketEnvelope packet)
    {
        _logger.LogInformation(
            "Packet {PacketId} received. Type={Type} {Source} → {Destination}",
            packet.PacketId, packet.Type, packet.SourceNode, packet.DestinationNode);

        // =========================
        // 1️ Дедупликация для ВСЕХ пакетов (Data и Ack)
        // =========================
        var firstSeen = _packetStorage.TryMarkAsSeen(packet.PacketId);
        if (!firstSeen)
        {
            _logger.LogDebug("Duplicate {Type} packet {PacketId}", packet.Type, packet.PacketId);

            // Только Data пакеты отправляют ACK при дубле
            if (packet.Type == PacketType.Data &&
                packet.DestinationNode == _nodeRegistry.LocalNodeId)
            {
                await SendAck(packet);
            }

            return Ok();
        }

        // =========================
        // 2️ ACK обработка
        // =========================
        if (packet.Type == PacketType.Ack)
        {
            _packetStorage.CompleteAck(packet.PacketId);
            _logger.LogInformation("ACK received for {PacketId}", packet.PacketId);
            return Ok();
        }

        // =========================
        // 3️ Если я получатель (Data packet)
        // =========================
        if (packet.DestinationNode == _nodeRegistry.LocalNodeId)
        {
            _packetStorage.StorePacket(packet);
            await SendAck(packet);

            // ✅ Notify client via WebSocket if connected
            await ClientController.NotifyClient(packet.DestinationNode, new
            {
                type = "new_message",
                messageId = packet.PacketId,
                from = packet.SourceNode,
                timestamp = DateTime.UtcNow
            });

            _logger.LogInformation("Packet {PacketId} delivered locally", packet.PacketId);
            return Ok();
        }

        // =========================
        // 4️ Иначе маршрутизируем
        // =========================
        var result = await _routingEngine.RoutePacket(packet);

        if (result.Success)
            return Ok(result);

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