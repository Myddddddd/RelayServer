using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace WgClient.Services;

/// <summary>
/// Runs the app as a system tray icon.
/// Provides right-click menu to open web UI, and shows connection status.
/// </summary>
public class TrayService : BackgroundService
{
    private NotifyIcon? _trayIcon;
    private readonly ConfigStore _config;
    private readonly WireGuardManager _wg;

    public TrayService(ConfigStore config, WireGuardManager wg)
    {
        _config = config;
        _wg = wg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // System tray must run on STA thread
        var trayThread = new Thread(() => RunTray(stoppingToken));
        trayThread.SetApartmentState(ApartmentState.STA);
        trayThread.IsBackground = true;
        trayThread.Start();

        // Keep alive until cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });

        _trayIcon?.Dispose();
        Application.Exit();
    }

    private void RunTray(CancellationToken ct)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Create tray icon with a simple VPN icon
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "WireGuard Relay Client",
            Visible = true,
        };

        // Context menu with quick Connect/Disconnect
        var menu = new ContextMenuStrip();
        var connectItem = new ToolStripMenuItem("▶ Connect") { Enabled = false };
        var disconnectItem = new ToolStripMenuItem("■ Disconnect") { Enabled = false };

        connectItem.Click += (_, _) =>
        {
            var cfg = _config.Load();
            if (cfg.ApprovalStatus == "approved" && !_wg.IsConnected())
                Task.Run(async () =>
                {
                    try { await _wg.ConnectAsync(cfg); }
                    catch (Exception ex)
                    {
                        _trayIcon?.ShowBalloonTip(4000, "Connect Failed", ex.Message, ToolTipIcon.Error);
                    }
                });
        };

        disconnectItem.Click += (_, _) =>
        {
            if (_wg.IsConnected())
                Task.Run(() => _wg.DisconnectAsync());
        };

        menu.Items.Add("🌐 Open Dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(connectItem);
        menu.Items.Add(disconnectItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ℹ️ Status", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("❌ Exit", null, (_, _) =>
        {
            _trayIcon!.Visible = false;
            Application.ExitThread();
            Environment.Exit(0);
        });

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => OpenDashboard();

        // Update tooltip/icon + menu item states based on connection state
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) =>
        {
            UpdateTrayIcon();
            var cfg = _config.Load();
            var connected = _wg.IsConnected();
            connectItem.Enabled = cfg.ApprovalStatus == "approved" && !connected;
            disconnectItem.Enabled = connected;
        };
        timer.Start();

        Application.Run();
    }

    private void OpenDashboard()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "http://localhost:7432",
            UseShellExecute = true,
        });
    }

    private void ShowStatus()
    {
        var cfg = _config.Load();
        var status = _wg.IsConnected() ? $"Connected • {cfg.VpnIp}" : cfg.ApprovalStatus switch
        {
            "pending" => "Waiting for approval...",
            "approved" => $"Approved • VPN IP: {cfg.VpnIp}",
            "rejected" => "Rejected by admin",
            _ => "Not configured"
        };

        _trayIcon?.ShowBalloonTip(3000, "WireGuard Relay", status, ToolTipIcon.Info);
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null) return;
        var cfg = _config.Load();
        var connected = _wg.IsConnected();
        _trayIcon.Text = connected ? $"WG Relay • Connected ({cfg.VpnIp})" : "WG Relay • Disconnected";
    }
}
