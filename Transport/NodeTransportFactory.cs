namespace SFDRN.Server.Transport;

/// <summary>
/// Фабрика для создания транспортов
/// </summary>
public class NodeTransportFactory : INodeTransportFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<NodeProtocol, Interfaces> _cache = new();

    public NodeTransportFactory(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
    }

    public Interfaces Create(NodeEndpoint node)
    {
        return Create(node.Protocol);
    }

    public Interfaces Create(NodeProtocol protocol)
    {
        // Кэшируем транспорты для переиспользования HttpClient
        if (_cache.TryGetValue(protocol, out var cached))
            return cached;

        var transport = protocol switch
        {
            NodeProtocol.Http => CreateHttpTransport(),
            NodeProtocol.Https => CreateHttpsTransport(),
            NodeProtocol.WebSocket => CreateWebSocketTransport(),
            NodeProtocol.WebSocketSecure => CreateWebSocketSecureTransport(),
            NodeProtocol.Tcp => CreateTcpTransport(),
            NodeProtocol.Quic => CreateQuicTransport(),
            _ => throw new NotSupportedException($"Protocol {protocol} is not supported")
        };

        _cache[protocol] = transport;
        return transport;
    }

    public Interfaces GetDefault()
    {
        return Create(NodeProtocol.Http);
    }

    // ═════════════════════════════════════════════════════════
    // Factory Methods
    // ═════════════════════════════════════════════════════════

    private Interfaces CreateHttpTransport()
    {
        var client = _httpFactory.CreateClient("NodeHttp");
        client.Timeout = TimeSpan.FromSeconds(10);

        var logger = _loggerFactory.CreateLogger<HttpNodeTransport>();
        return new HttpNodeTransport(client, NodeProtocol.Http, logger);
    }

    private Interfaces CreateHttpsTransport()
    {
        var client = _httpFactory.CreateClient("NodeHttps");
        client.Timeout = TimeSpan.FromSeconds(10);

        var logger = _loggerFactory.CreateLogger<HttpNodeTransport>();
        return new HttpsNodeTransport(client, logger);
    }

    private Interfaces CreateWebSocketTransport()
    {
        // TODO: Реализовать WebSocketNodeTransport
        throw new NotImplementedException("WebSocket transport not yet implemented");
    }

    private Interfaces CreateWebSocketSecureTransport()
    {
        // TODO: Реализовать WebSocketSecureNodeTransport
        throw new NotImplementedException("WebSocket Secure transport not yet implemented");
    }

    private Interfaces CreateTcpTransport()
    {
        // TODO: Реализовать TcpNodeTransport
        throw new NotImplementedException("TCP transport not yet implemented");
    }

    private Interfaces CreateQuicTransport()
    {
        // TODO: Реализовать QuicNodeTransport
        throw new NotImplementedException("QUIC transport not yet implemented");
    }
}