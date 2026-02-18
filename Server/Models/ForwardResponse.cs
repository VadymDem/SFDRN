namespace SFDRN.Server.Models;

public class ForwardResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? NextHop { get; set; }
}
