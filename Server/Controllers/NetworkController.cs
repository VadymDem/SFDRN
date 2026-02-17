using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Mesh;
using SFDRN.Server.Storage;

namespace SFDRN.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class NetworkController : ControllerBase
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly PacketStorage _packetStorage;
    private readonly ILogger<NetworkController> _logger;

    public NetworkController(
        NodeRegistry nodeRegistry,
        PacketStorage packetStorage,
        ILogger<NetworkController> logger)
    {
        _nodeRegistry = nodeRegistry;
        _packetStorage = packetStorage;
        _logger = logger;
    }

    [HttpGet("snapshot")]
    public IActionResult GetSnapshot()
    {
        var nodes = _nodeRegistry.GetAllNodes();
        var packets = _packetStorage.GetPacketCount();

        return Ok(new
        {
            localNodeId = _nodeRegistry.LocalNodeId,
            totalNodes = nodes.Count,
            aliveNodes = nodes.Count(n => n.Status == Models.NodeStatus.Alive),
            deadNodes = nodes.Count(n => n.Status == Models.NodeStatus.Dead),
            unknownNodes = nodes.Count(n => n.Status == Models.NodeStatus.Unknown),
            nodes = nodes.Select(n => new
            {
                n.NodeId,
                n.Region,
                n.PublicEndpoint,
                n.Transports,
                n.LastSeen,
                Status = n.Status.ToString()
            }),
            packetsStored = packets,
            timestamp = DateTime.UtcNow
        });
    }
}