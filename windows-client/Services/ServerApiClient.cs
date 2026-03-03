using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WgClient.Services;

public record RegisterResponse(string Id, string Status, string? Message);
public record PollResponse(string Status, PollConfig? Config);
public record PollConfig(
    [property: JsonPropertyName("vpn_ip")] string VpnIp,
    [property: JsonPropertyName("server_public_key")] string ServerPublicKey,
    [property: JsonPropertyName("server_endpoint")] string ServerEndpoint,
    [property: JsonPropertyName("server_endpoint_ipv6")] string? ServerEndpointIpv6,
    [property: JsonPropertyName("dns")] string Dns,
    [property: JsonPropertyName("allowed_ips")] string AllowedIps
);
public record ServerInfo(
    [property: JsonPropertyName("server_public_key")] string ServerPublicKey,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("endpoint_ipv6")] string? EndpointIpv6,
    [property: JsonPropertyName("vpn_subnet")] string VpnSubnet,
    [property: JsonPropertyName("dns")] string Dns
);

public class ServerApiClient(IHttpClientFactory httpFactory, ILogger<ServerApiClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private HttpClient GetClient() => httpFactory.CreateClient();

    public async Task<RegisterResponse> RegisterAsync(string serverUrl, string name, string publicKey, string platform)
    {
        var payload = JsonSerializer.Serialize(new { name, public_key = publicKey, platform });
        var response = await GetClient().PostAsync(
            $"{serverUrl}/api/register",
            new StringContent(payload, Encoding.UTF8, "application/json")
        );
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<RegisterResponse>(json, JsonOpts)!;
    }

    public async Task<PollResponse> PollAsync(string serverUrl, string peerId)
    {
        var response = await GetClient().GetAsync($"{serverUrl}/api/poll/{peerId}");
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Poll failed {status}: {body}", response.StatusCode, json);
            return new PollResponse("error", null);
        }
        return JsonSerializer.Deserialize<PollResponse>(json, JsonOpts)!;
    }

    public async Task<ServerInfo> GetServerInfoAsync(string serverUrl)
    {
        var response = await GetClient().GetAsync($"{serverUrl}/api/server-info");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ServerInfo>(json, JsonOpts)!;
    }
}
