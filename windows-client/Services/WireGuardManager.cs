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
    private const string FallbackTunnelName = "wgrelay2";

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WgRelayClient"
    );

    private bool _connected = false;
    private string _activeTunnelName = TunnelName;

    private sealed record ConnectAttempt(string TunnelName, string Endpoint, int Mtu, string Reason);

    public bool IsConnected() => _connected && IsTunnelServiceRunning();

    private bool IsTunnelServiceRunning()
    {
        var result = RunCommand("sc.exe", $"query WireGuardTunnel${_activeTunnelName}");
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

        var failures = new List<string>();
        foreach (var attempt in BuildConnectAttempts(config))
        {
            logger.LogInformation(
                "Connecting WireGuard tunnel using {reason}. Tunnel: {tunnel}; Endpoint: {endpoint}; MTU: {mtu}",
                attempt.Reason,
                attempt.TunnelName,
                attempt.Endpoint,
                attempt.Mtu > 0 ? attempt.Mtu : 1420
            );

            await EnsureCleanStateAsync(attempt.TunnelName);

            Directory.CreateDirectory(ConfigDir);
            var configFile = GetConfigFile(attempt.TunnelName);
            var confContent = BuildWgConfig(config, attempt.Endpoint, attempt.Mtu);
            File.WriteAllText(configFile, confContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            logger.LogInformation("Config written to: {path}", configFile);

            logger.LogInformation("Installing WireGuard tunnel service...");
            var output = RunElevated(WgTunnelExe, $"/installtunnelservice \"{configFile}\"");
            logger.LogInformation("Install result: {out}", output);

            await Task.Delay(3000);

            _activeTunnelName = attempt.TunnelName;

            if (IsTunnelServiceRunning())
            {
                _connected = true;
                logger.LogInformation("WireGuard tunnel connected. VPN IP: {ip}", config.VpnIp);

                if (config.BypassDomains.Count > 0)
                    await AddDomainRoutesAsync(config.BypassDomains, config.VpnIp);

                return;
            }

            var diagnostics = GetTunnelDiagnostics(attempt.TunnelName);
            failures.Add($"{attempt.Reason}: {diagnostics}");
            logger.LogWarning("WireGuard tunnel attempt failed: {details}", diagnostics);
            await CleanupCurrentTunnelAsync(attempt.TunnelName);
        }

        throw new InvalidOperationException(
            "Tunnel service failed to start after automatic fallback attempts. " + string.Join(" | ", failures));
    }

    private IEnumerable<ConnectAttempt> BuildConnectAttempts(AppConfig config)
    {
        var tunnelNames = new[] { TunnelName, FallbackTunnelName };
        var attempts = new List<ConnectAttempt>
        {
            new(TunnelName, config.ServerEndpoint, config.Mtu, "primary endpoint")
        };

        attempts.Add(new ConnectAttempt(FallbackTunnelName, config.ServerEndpoint, config.Mtu, "alternate tunnel name fallback"));

        if (!string.IsNullOrWhiteSpace(config.ServerEndpointFallback)
            && !string.Equals(config.ServerEndpointFallback, config.ServerEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var tunnelName in tunnelNames)
            {
                attempts.Add(new ConnectAttempt(tunnelName, config.ServerEndpointFallback, config.Mtu, tunnelName == TunnelName ? "alternate endpoint fallback" : "alternate endpoint + tunnel name fallback"));
            }
        }

        var safeMtu = GetFallbackMtu(config.Mtu);
        if (safeMtu != config.Mtu)
        {
            foreach (var tunnelName in tunnelNames)
            {
                attempts.Add(new ConnectAttempt(tunnelName, config.ServerEndpoint, safeMtu, tunnelName == TunnelName ? "safe MTU fallback" : "safe MTU + tunnel name fallback"));
            }

            if (!string.IsNullOrWhiteSpace(config.ServerEndpointFallback)
                && !string.Equals(config.ServerEndpointFallback, config.ServerEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var tunnelName in tunnelNames)
                {
                    attempts.Add(new ConnectAttempt(tunnelName, config.ServerEndpointFallback, safeMtu, tunnelName == TunnelName ? "alternate endpoint + safe MTU fallback" : "alternate endpoint + safe MTU + tunnel name fallback"));
                }
            }
        }

        return attempts
            .Where(a => !string.IsNullOrWhiteSpace(a.Endpoint))
            .DistinctBy(a => (a.TunnelName, a.Endpoint, a.Mtu));
    }

    private static int GetFallbackMtu(int currentMtu)
    {
        if (currentMtu is > 0 and <= 1280)
            return currentMtu;

        return 1280;
    }

    private async Task EnsureCleanStateAsync(string targetTunnelName)
    {
        await CleanupCurrentTunnelAsync(targetTunnelName);

        var allSvcs = RunCommand("sc.exe", "query type=all");
        var staleNames = allSvcs.Split('\n')
            .Where(l => l.TrimStart().StartsWith("SERVICE_NAME: WireGuardTunnel$"))
            .Select(l => l.Trim().Replace("SERVICE_NAME: WireGuardTunnel$", ""))
            .Where(n => !string.Equals(n.Trim(), targetTunnelName, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Trim())
            .ToList();
        foreach (var staleName in staleNames)
        {
            logger.LogWarning("Removing stale tunnel: {name}", staleName);
            RunElevated(WgTunnelExe, $"/uninstalltunnelservice {staleName}");
            await Task.Delay(500);
            RunCommand("sc.exe", $"delete WireGuardTunnel${staleName}");
        }
        if (staleNames.Count > 0)
        {
            var adapterCleanup = string.Join(";", staleNames.Select(n =>
                $"Get-NetAdapter -Name '{n}' -EA SilentlyContinue | Remove-NetAdapter -Confirm:$false -EA SilentlyContinue"));
            RunCommand("powershell.exe", $"-NonInteractive -Command \"{adapterCleanup}\"");
            await Task.Delay(500);
        }
    }

    private async Task CleanupCurrentTunnelAsync(string tunnelName)
    {
        var existing = RunCommand("sc.exe", $"query WireGuardTunnel${tunnelName}");
        bool serviceExists = !existing.Contains("1060") && !existing.Contains("does not exist");
        if (serviceExists)
        {
            logger.LogInformation("Cleaning up existing tunnel service...");
            RunElevated(WgTunnelExe, $"/uninstalltunnelservice {tunnelName}");
            await Task.Delay(1000);
            RunCommand("sc.exe", $"delete WireGuardTunnel${tunnelName}");
            await Task.Delay(500);
        }

        RunCommand("powershell.exe",
            $"-NonInteractive -Command \"Get-NetAdapter -Name '{tunnelName}' -ErrorAction SilentlyContinue | Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue\"");
        await Task.Delay(1000);
    }

    private string GetTunnelDiagnostics(string tunnelName)
    {
        var svcStatus = RunCommand("sc.exe", $"query WireGuardTunnel${tunnelName}").Replace("\r\n", " ").Trim();
        var svcStatusEx = RunCommand("sc.exe", $"queryex WireGuardTunnel${tunnelName}").Replace("\r\n", " ").Trim();
        var evtLog = RunCommand(
            "wevtutil.exe",
            "qe System /q:\"*[System[Provider[@Name='WireGuardTunnel']]]\" /c:3 /rd:true /f:text"
        ).Replace("\r\n", " ").Trim();
        return $"Status: {svcStatus}; QueryEx: {svcStatusEx}; Events: {evtLog}";
    }

    private static string GetConfigFile(string tunnelName) => Path.Combine(ConfigDir, $"{tunnelName}.conf");

    /// <summary>
    /// Disconnect WireGuard tunnel.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (File.Exists(WgTunnelExe))
        {
            var output = RunElevated(WgTunnelExe, $"/uninstalltunnelservice {_activeTunnelName}");
            logger.LogInformation("Uninstall result: {out}", output);
        }
        await Task.Delay(1000);
        _connected = false;
        _activeTunnelName = TunnelName;
        logger.LogInformation("WireGuard tunnel disconnected.");
    }

    /// <summary>
    /// Build WireGuard .conf file content.
    /// </summary>
    private static string BuildWgConfig(AppConfig config, string endpoint, int mtu)
    {
        // AllowedIPs: route only VPN subnet → no full-tunnel, system DNS is preserved
        var mtuLine = mtu > 0 ? $"\nMTU = {mtu}" : "";
        return $"""
            [Interface]
            PrivateKey = {config.PrivateKey}
            Address = {config.VpnIp}/24{mtuLine}

            [Peer]
            PublicKey = {config.ServerPublicKey}
            Endpoint = {endpoint}
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
