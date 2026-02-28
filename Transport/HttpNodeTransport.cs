using System.Net.Http.Json;
using System.Text.Json;

namespace SFDRN.Server.Transport;

/// <summary>
/// HTTP/HTTPS транспорт для отправки сообщений между нодами
/// </summary>
public class HttpNodeTransport : Interfaces
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpNodeTransport>? _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NodeProtocol Protocol { get; }
    public bool IsAvailable => true;

    public HttpNodeTransport(HttpClient httpClient, NodeProtocol protocol = NodeProtocol.Http, ILogger<HttpNodeTransport>? logger = null)
    {
        _httpClient = httpClient;
        Protocol = protocol;
        _logger = logger;
    }

    public async Task<bool> SendAsync(NodeEndpoint node, NodeMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{node.BaseUrl}/api/node/receive";

            _logger?.LogDebug("[Transport] HTTP Send to {Url}: {MessageId}", url, message.MessageId);

            var response = await _httpClient.PostAsJsonAsync(url, message, _jsonOptions, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogWarning("[Transport] HTTP Send failed: {Status} {Body}", (int)response.StatusCode, body);
                return false;
            }

            _logger?.LogDebug("[Transport] HTTP Send OK: {MessageId}", message.MessageId);
            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "[Transport] HTTP Send error: {Message}", ex.Message);
            return false;
        }
        catch (TaskCanceledException)
        {
            _logger?.LogWarning("[Transport] HTTP Send timeout");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Transport] HTTP Send unexpected error");
            return false;
        }
    }

    public async Task<NodeResponse?> RequestAsync(NodeEndpoint node, NodeMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{node.BaseUrl}/api/node/request";

            _logger?.LogDebug("[Transport] HTTP Request to {Url}: {MessageId}", url, message.MessageId);

            var response = await _httpClient.PostAsJsonAsync(url, message, _jsonOptions, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogWarning("[Transport] HTTP Request failed: {Status} {Body}", (int)response.StatusCode, body);
                return new NodeResponse { Success = false, Error = $"HTTP {(int)response.StatusCode}" };
            }

            var result = await response.Content.ReadFromJsonAsync<NodeResponse>(_jsonOptions, cancellationToken);
            return result ?? new NodeResponse { Success = false, Error = "Empty response" };
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "[Transport] HTTP Request error: {Message}", ex.Message);
            return new NodeResponse { Success = false, Error = ex.Message };
        }
        catch (TaskCanceledException)
        {
            _logger?.LogWarning("[Transport] HTTP Request timeout");
            return new NodeResponse { Success = false, Error = "Timeout" };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Transport] HTTP Request unexpected error");
            return new NodeResponse { Success = false, Error = ex.Message };
        }
    }

    public async Task<bool> PingAsync(NodeEndpoint node, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{node.BaseUrl}/api/node/ping";

            _logger?.LogDebug("[Transport] HTTP Ping to {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("[Transport] HTTP Ping failed: {Status}", (int)response.StatusCode);
                return false;
            }

            _logger?.LogDebug("[Transport] HTTP Ping OK: {NodeId}", node.NodeId);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Transport] HTTP Ping error: {Message}", ex.Message);
            return false;
        }
    }
}

/// <summary>
/// HTTPS транспорт с TLS
/// </summary>
public class HttpsNodeTransport : HttpNodeTransport
{
    public HttpsNodeTransport(HttpClient httpClient, ILogger<HttpNodeTransport>? logger = null)
        : base(httpClient, NodeProtocol.Https, logger)
    {
        // HTTPS использует тот же HttpClient, но node.BaseUrl будет https://
    }
}