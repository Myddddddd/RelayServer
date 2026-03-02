namespace WgClient.Services;

/// <summary>
/// Background worker that:
/// 1. Polls the server for approval status when in "pending" state
/// 2. Receives WireGuard config when approved
/// 3. Auto-connects if AutoConnect is enabled
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
                cfg.ServerEndpoint = poll.Config.ServerEndpoint;
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
}
