namespace WgClient.Services;

using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>
/// Background worker that:
/// 1. Polls the server for approval status when in "pending" state
/// 2. Receives WireGuard config when approved
/// 3. Auto-connects if AutoConnect is enabled
/// 4. Automatically prefers IPv6 endpoint if both server and client support it
/// </summary>
public class TunnelWorker(
    ConfigStore config,
    ServerApiClient apiClient,
    WireGuardManager wg,
    ILogger<TunnelWorker> logger) : BackgroundService
{
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TunnelWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DoWorkAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TunnelWorker error");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task DoWorkAsync(CancellationToken ct)
    {
        var cfg = config.Load();

        // Nothing to do if not configured
        if (string.IsNullOrEmpty(cfg.ServerUrl) || string.IsNullOrEmpty(cfg.PeerId))
        {
            _pollInterval = TimeSpan.FromSeconds(10);
            return;
        }

        // If pending, poll for approval
        if (cfg.ApprovalStatus == "pending")
        {
            _pollInterval = TimeSpan.FromSeconds(5);
            logger.LogDebug("Polling server for approval...");

            var poll = await apiClient.PollAsync(cfg.ServerUrl, cfg.PeerId);

            if (poll.Status == "approved" && poll.Config != null)
            {
                logger.LogInformation("Device approved! VPN IP: {ip}", poll.Config.VpnIp);
                cfg.ApprovalStatus = "approved";
                cfg.VpnIp = poll.Config.VpnIp;
                cfg.ServerPublicKey = poll.Config.ServerPublicKey;

                // Auto-prefer IPv6 if server exposes it and client has IPv6 connectivity
                string endpoint = poll.Config.ServerEndpoint;
                string fallbackEndpoint = string.Empty;
                if (!string.IsNullOrEmpty(poll.Config.ServerEndpointIpv6))
                {
                    logger.LogInformation("Server supports IPv6 ({ep6}), testing client IPv6...", poll.Config.ServerEndpointIpv6);
                    if (await CheckIpv6ConnectivityAsync())
                    {
                        endpoint = poll.Config.ServerEndpointIpv6;
                        fallbackEndpoint = poll.Config.ServerEndpoint;
                        logger.LogInformation("IPv6 available — using IPv6 endpoint: {ep}", endpoint);
                    }
                    else
                    {
                        fallbackEndpoint = poll.Config.ServerEndpointIpv6;
                        logger.LogInformation("Client has no IPv6 — using IPv4 endpoint.");
                    }
                }

                cfg.ServerEndpoint = endpoint;
                cfg.ServerEndpointFallback = fallbackEndpoint;
                cfg.VpnSubnet = poll.Config.AllowedIps;
                config.Save(cfg);
            }
            else if (poll.Status == "rejected")
            {
                logger.LogWarning("Device rejected by server.");
                cfg.ApprovalStatus = "rejected";
                config.Save(cfg);
            }
            return;
        }

        // If approved, slow down polling
        if (cfg.ApprovalStatus == "approved")
        {
            _pollInterval = TimeSpan.FromSeconds(30);

            // Refresh endpoint from server (keeps endpoint current if server IP changes)
            try
            {
                var refreshPoll = await apiClient.PollAsync(cfg.ServerUrl, cfg.PeerId);
                if (refreshPoll.Status == "approved" && refreshPoll.Config != null)
                {
                    string freshEndpoint = refreshPoll.Config.ServerEndpoint;
                    string fallbackEndpoint = string.Empty;
                    if (!string.IsNullOrEmpty(refreshPoll.Config.ServerEndpointIpv6))
                    {
                        bool useIpv6 = cfg.UseIPv6 || await CheckIpv6ConnectivityAsync();
                        if (useIpv6)
                        {
                            freshEndpoint = refreshPoll.Config.ServerEndpointIpv6;
                            fallbackEndpoint = refreshPoll.Config.ServerEndpoint;
                        }
                        else
                        {
                            fallbackEndpoint = refreshPoll.Config.ServerEndpointIpv6;
                        }
                    }
                    if (cfg.ServerEndpoint != freshEndpoint
                        || cfg.ServerEndpointFallback != fallbackEndpoint
                        || cfg.ServerPublicKey != refreshPoll.Config.ServerPublicKey)
                    {
                        logger.LogInformation("Endpoint refreshed: {old} → {new}", cfg.ServerEndpoint, freshEndpoint);
                        cfg.ServerEndpoint = freshEndpoint;
                        cfg.ServerEndpointFallback = fallbackEndpoint;
                        cfg.ServerPublicKey = refreshPoll.Config.ServerPublicKey;
                        cfg.VpnSubnet = refreshPoll.Config.AllowedIps;
                        config.Save(cfg);
                    }
                }
            }
            catch { /* Ignore if server unreachable during refresh */ }

            // Auto-connect if enabled and not yet connected
            if (cfg.AutoConnect && !wg.IsConnected())
            {
                logger.LogInformation("AutoConnect enabled — connecting tunnel...");
                try { await wg.ConnectAsync(cfg); }
                catch (Exception ex) { logger.LogWarning("AutoConnect failed: {err}", ex.Message); }
            }
        }
    }

    /// <summary>
    /// Test IPv6 connectivity by checking if any network interface has
    /// a global-scope (non-link-local, non-loopback) IPv6 address.
    /// Instant local check — no HTTP required.
    /// </summary>
    private Task<bool> CheckIpv6ConnectivityAsync()
    {
        try
        {
            var hasIPv6 = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                           && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetworkV6)
                .Any(ua =>
                {
                    var bytes = ua.Address.GetAddressBytes();
                    // Exclude loopback (::1) and link-local (fe80::)
                    return bytes[0] != 0xfe || (bytes[1] & 0xc0) != 0x80; // not fe80::/10
                });
            return Task.FromResult(hasIPv6);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
