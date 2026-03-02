using System.Diagnostics;
using System.Net;
using System.Text;

namespace WgClient.Services;

/// <summary>
/// Manages the WireGuard tunnel on Windows.
/// Requires WireGuard for Windows installed (C:\Program Files\WireGuard\).
/// Uses wireguard.exe /installtunnelservice for the actual WG tunnel.
/// </summary>
public class WireGuardManager(ILogger<WireGuardManager> logger)
{
    private const string WgExe = @"C:\Program Files\WireGuard\wg.exe";
    private const string WgTunnelExe = @"C:\Program Files\WireGuard\wireguard.exe";
    private const string TunnelName = "wgrelay";

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WgRelayClient"
    );

    private static readonly string ConfigFile = Path.Combine(ConfigDir, $"{TunnelName}.conf");

    private bool _connected = false;

    public bool IsConnected() => _connected && IsTunnelServiceRunning();

    private bool IsTunnelServiceRunning()
    {
        var result = RunCommand("sc.exe", $"query WireGuardTunnel${TunnelName}");
        return result.Contains("RUNNING");
    }

    /// <summary>
    /// Generate WireGuard key pair using wg.exe
    /// </summary>
    public (string privateKey, string publicKey) GenerateKeyPair()
    {
        if (!File.Exists(WgExe))
        {
            // Fallback: use built-in crypto (requires additional lib) - for now throw clear error
            throw new InvalidOperationException(
                "WireGuard not found. Please install WireGuard for Windows from https://www.wireguard.com/install/");
        }

        // wg genkey
        var privKey = RunCommand(WgExe, "genkey").Trim();

        // wg pubkey < privkey
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = WgExe,
                Arguments = "pubkey",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        proc.Start();
        proc.StandardInput.WriteLine(privKey);
        proc.StandardInput.Close();
        var pubKey = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();

        logger.LogInformation("Generated WireGuard key pair. Public: {pub}", pubKey);
        return (privKey, pubKey);
    }

    /// <summary>
    /// Connect to WireGuard VPN using stored config.
    /// </summary>
    public async Task ConnectAsync(AppConfig config)
    {
        if (!File.Exists(WgTunnelExe))
            throw new InvalidOperationException(
                "WireGuard not found. Please install WireGuard for Windows from https://www.wireguard.com/install/");

        // Always do a full cleanup of any stale installation:
        // 1) Uninstall via wireguard.exe (proper WireGuard-aware uninstall)
        // 2) Hard-delete the service in case wireguard.exe uninstall left garbage
        // 3) Remove the stuck Wintun adapter (this is the main cause of exit code 2)
        var existing = RunCommand("sc.exe", $"query WireGuardTunnel${TunnelName}");
        bool serviceExists = !existing.Contains("1060") && !existing.Contains("does not exist");
        if (serviceExists)
        {
            logger.LogInformation("Cleaning up existing tunnel service...");
            RunElevated(WgTunnelExe, $"/uninstalltunnelservice {TunnelName}");
            await Task.Delay(1000);
            RunCommand("sc.exe", $"delete WireGuardTunnel${TunnelName}");
            await Task.Delay(500);
        }

        // Remove any leftover Wintun network adapter with same name
        // (Wintun exit code 2 = adapter creation failed because old one still registered)
        RunCommand("powershell.exe",
            $"-NonInteractive -Command \"Get-NetAdapter -Name '{TunnelName}' -ErrorAction SilentlyContinue | Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue\"");
        await Task.Delay(1000);

        // Write config file - MUST use UTF-8 without BOM; WireGuard rejects BOM
        Directory.CreateDirectory(ConfigDir);
        var confContent = BuildWgConfig(config);
        File.WriteAllText(ConfigFile, confContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        logger.LogInformation("Config written to: {path}", ConfigFile);

        logger.LogInformation("Installing WireGuard tunnel service...");
        var output = RunElevated(WgTunnelExe, $"/installtunnelservice \"{ConfigFile}\"");
        logger.LogInformation("Install result: {out}", output);

        // Wait for service to start (SYSTEM account reads config from ProgramData)
        await Task.Delay(3000);

        if (!IsTunnelServiceRunning())
        {
            var svcStatus = RunCommand("sc.exe", $"query WireGuardTunnel${TunnelName}");
            throw new InvalidOperationException($"Tunnel service failed to start. Status: {svcStatus.Replace("\r\n", " ").Trim()}");
        }

        _connected = true;
        logger.LogInformation("WireGuard tunnel connected. VPN IP: {ip}", config.VpnIp);

        // Add domain-specific routes
        if (config.BypassDomains.Count > 0)
            await AddDomainRoutesAsync(config.BypassDomains, config.VpnIp);
    }

    /// <summary>
    /// Disconnect WireGuard tunnel.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (File.Exists(WgTunnelExe))
        {
            var output = RunElevated(WgTunnelExe, $"/uninstalltunnelservice {TunnelName}");
            logger.LogInformation("Uninstall result: {out}", output);
        }
        await Task.Delay(1000);
        _connected = false;
        logger.LogInformation("WireGuard tunnel disconnected.");
    }

    /// <summary>
    /// Build WireGuard .conf file content.
    /// </summary>
    private static string BuildWgConfig(AppConfig config)
    {
        // AllowedIPs: route only VPN subnet → no full-tunnel, system DNS is preserved
        return $"""
            [Interface]
            PrivateKey = {config.PrivateKey}
            Address = {config.VpnIp}/24

            [Peer]
            PublicKey = {config.ServerPublicKey}
            Endpoint = {config.ServerEndpoint}
            AllowedIPs = {config.VpnSubnet}
            PersistentKeepalive = 25
            """;
    }

    /// <summary>
    /// Resolve domains and add specific routes through WireGuard.
    /// Supports wildcard syntax: *.example.com → resolves example.com
    /// </summary>
    private async Task AddDomainRoutesAsync(List<string> domains, string vpnIp)
    {
        // Get WireGuard interface gateway (VPN server IP in VPN space = .1)
        var parts = vpnIp.Split('.');
        var gateway = $"{parts[0]}.{parts[1]}.{parts[2]}.1";

        foreach (var rawDomain in domains)
        {
            // Wildcard support: *.example.com → resolve example.com
            var domain = rawDomain.TrimStart();
            if (domain.StartsWith("*."))
                domain = domain.Substring(2);
            if (string.IsNullOrWhiteSpace(domain)) continue;

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain);
                foreach (var addr in addresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    RunCommand("route.exe", $"add {addr} mask 255.255.255.255 {gateway} metric 1");
                    logger.LogInformation("Added route: {ip} ({domain}) → {gw}", addr, rawDomain.Trim(), gateway);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to resolve domain {domain}: {err}", domain, ex.Message);
            }
        }
    }

    private string RunCommand(string exe, string args)
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            proc!.WaitForExit(10000);
            return proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private string RunElevated(string exe, string args)
    {
        // App requires admin (see app.manifest), so run directly without runas
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            proc!.WaitForExit(15000);
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            logger.LogInformation("{exe} exit={code} output={out}", exe, proc.ExitCode, output);
            return output.Length > 0 ? output : "(no output)";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RunElevated failed for {exe}", exe);
            return $"ERROR: {ex.Message}";
        }
    }
}
