using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Models;
using SFDRN.Server.Routing;

namespace SFDRN.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class RoutingController : ControllerBase
{
    private readonly RoutingEngine _routingEngine;
    private readonly ILogger<RoutingController> _logger;

    public RoutingController(RoutingEngine routingEngine, ILogger<RoutingController> logger)
    {
        _routingEngine = routingEngine;
        _logger = logger;
    }

    [HttpPost("forward")]
    public async Task<IActionResult> Forward([FromBody] PacketEnvelope packet)
    {
        _logger.LogInformation("Received packet {PacketId} from {SourceNode} to {DestinationNode}",
            packet.PacketId, packet.SourceNode, packet.DestinationNode);

        var result = await _routingEngine.RoutePacket(packet);

        if (result.Success)
        {
            return Ok(result);
        }
        else
        {
            return BadRequest(result);
        }
    }
}