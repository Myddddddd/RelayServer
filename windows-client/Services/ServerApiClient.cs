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

    public async Task<PollResponse> PollWithFallbackAsync(string serverUrl, string peerId)
    {
        var poll = await PollAsync(serverUrl, peerId);
        if (poll.Status != "approved" || poll.Config is null || !NeedsServerInfoFallback(poll.Config))
            return poll;

        try
        {
            var info = await GetServerInfoAsync(serverUrl);
            return poll with
            {
                Config = poll.Config with
                {
                    ServerPublicKey = string.IsNullOrWhiteSpace(poll.Config.ServerPublicKey) ? info.ServerPublicKey : poll.Config.ServerPublicKey,
                    ServerEndpoint = string.IsNullOrWhiteSpace(poll.Config.ServerEndpoint) ? info.Endpoint : poll.Config.ServerEndpoint,
                    ServerEndpointIpv6 = string.IsNullOrWhiteSpace(poll.Config.ServerEndpointIpv6) ? info.EndpointIpv6 : poll.Config.ServerEndpointIpv6,
                    Dns = string.IsNullOrWhiteSpace(poll.Config.Dns) ? info.Dns : poll.Config.Dns,
                    AllowedIps = string.IsNullOrWhiteSpace(poll.Config.AllowedIps) ? info.VpnSubnet : poll.Config.AllowedIps,
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to hydrate approved peer config from /api/server-info for {serverUrl}", serverUrl);
            return poll;
        }
    }

    public async Task<ServerInfo> GetServerInfoAsync(string serverUrl)
    {
        var response = await GetClient().GetAsync($"{serverUrl}/api/server-info");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ServerInfo>(json, JsonOpts)!;
    }

    private static bool NeedsServerInfoFallback(PollConfig config)
        => string.IsNullOrWhiteSpace(config.ServerPublicKey)
        || string.IsNullOrWhiteSpace(config.ServerEndpoint)
        || string.IsNullOrWhiteSpace(config.AllowedIps)
        || string.IsNullOrWhiteSpace(config.Dns);
}
