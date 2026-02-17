using Microsoft.AspNetCore.Mvc;
using SFDRN.Server.Mesh;

namespace SFDRN.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class HealthController : ControllerBase
{
    private static readonly DateTime _startTime = DateTime.UtcNow;
    private readonly NodeRegistry _nodeRegistry;

    public HealthController(NodeRegistry nodeRegistry)
    {
        _nodeRegistry = nodeRegistry;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var uptime = DateTime.UtcNow - _startTime;
        var localNode = _nodeRegistry.GetNode(_nodeRegistry.LocalNodeId);

        return Ok(new
        {
            status = "healthy",
            nodeId = _nodeRegistry.LocalNodeId,
            uptime = uptime.ToString(@"hh\:mm\:ss"),
            activeTransports = localNode?.Transports ?? new List<string>(),
            knownNodes = _nodeRegistry.GetAllNodes().Count
        });
    }
}