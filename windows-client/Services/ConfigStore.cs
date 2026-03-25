using System.Text.Json;
using System.Text.Json.Serialization;

namespace WgClient.Services;

public class AppConfig
{
    public string ServerUrl { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public string PrivateKey { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string ApprovalStatus { get; set; } = ""; // pending, approved, rejected
    public string VpnIp { get; set; } = "";
    public string ServerPublicKey { get; set; } = "";
    public string ServerEndpoint { get; set; } = "";
    public string ServerEndpointFallback { get; set; } = "";
    public string LastError { get; set; } = "";
    public string VpnSubnet { get; set; } = "10.0.0.0/24";
    public List<string> BypassDomains { get; set; } = [];
    public bool AutoConnect { get; set; } = false;
    public int Mtu { get; set; } = 0; // 0 = use WireGuard default (1420)
    public bool UseIPv6 { get; set; } = false; // force IPv6 endpoint even if auto-detect fails
}

public class ConfigStore
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WgRelayClient", "config.json"
    );

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private AppConfig _cache = new();

    public ConfigStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        _cache = Load();
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                _cache = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new();
        }
        catch { _cache = new(); }
        return _cache;
    }

    public void Save(AppConfig config)
    {
        _cache = config;
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOpts));
    }
}
