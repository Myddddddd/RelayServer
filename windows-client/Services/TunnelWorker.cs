namespace WgClient.Services;

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
    IHttpClientFactory httpFactory,
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
                if (!string.IsNullOrEmpty(poll.Config.ServerEndpointIpv6))
                {
                    logger.LogInformation("Server supports IPv6 ({ep6}), testing client IPv6...", poll.Config.ServerEndpointIpv6);
                    if (await CheckIpv6ConnectivityAsync())
                    {
                        endpoint = poll.Config.ServerEndpointIpv6;
                        logger.LogInformation("IPv6 available — using IPv6 endpoint: {ep}", endpoint);
                    }
                    else
                    {
                        logger.LogInformation("Client has no IPv6 — using IPv4 endpoint.");
                    }
                }

                cfg.ServerEndpoint = endpoint;
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
    /// Test IPv6 connectivity by trying to reach an IPv6-only endpoint.
    /// Uses ipv6.icanhazip.com which only responds over IPv6.
    /// </summary>
    private async Task<bool> CheckIpv6ConnectivityAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var http = httpFactory.CreateClient();
            var resp = await http.GetAsync("https://ipv6.icanhazip.com", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
