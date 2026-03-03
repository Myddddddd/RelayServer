using WgClient.Services;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;

try
{
StartupLog.Init();

// ─── Check Admin ───────────────────────────────────────────────────────
bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
    .IsInRole(WindowsBuiltInRole.Administrator);
StartupLog.Write($"IsAdmin: {isAdmin}");
StartupLog.Write($"OS: {Environment.OSVersion}");
StartupLog.Write($"Arch: {RuntimeInformation.OSArchitecture}");

var builder = WebApplication.CreateBuilder(args);

// Windows Service support — runs silently as a service
StartupLog.Write("Configuring Host...");
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "WireGuard Relay Client";
});

// Use fixed URL for local web UI  
StartupLog.Write("Binding to http://localhost:7432...");
builder.WebHost.UseUrls("http://localhost:7432");

// Register services
StartupLog.Write("Registering services...");
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<WireGuardManager>();
builder.Services.AddSingleton<ServerApiClient>();
builder.Services.AddHostedService<TunnelWorker>();
builder.Services.AddHostedService<TrayService>();  // System tray icon
builder.Services.AddHttpClient();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Handle --install / --uninstall / --startup:add / --startup:remove args
if (args.Contains("--install"))
{
    InstallService();
    return;
}
if (args.Contains("--uninstall"))
{
    UninstallService();
    return;
}
if (args.Contains("--startup:add"))
{
    AddToStartup();
    return;
}
if (args.Contains("--startup:remove"))
{
    RemoveFromStartup();
    return;
}

var app = builder.Build();
StartupLog.Write("App built. Starting pipeline...");
app.UseCors();

// ─── Local API Endpoints ──────────────────────────────────────────────

var api = app.MapGroup("/api");

// Get current status
api.MapGet("/status", (ConfigStore config, WireGuardManager wg) =>
{
    var cfg = config.Load();
    return new
    {
        connected = wg.IsConnected(),
        serverUrl = cfg.ServerUrl,
        deviceName = cfg.DeviceName,
        peerId = cfg.PeerId,
        approvalStatus = cfg.ApprovalStatus,
        vpnIp = cfg.VpnIp,
        publicKey = cfg.PublicKey,
        bypassDomains = cfg.BypassDomains,
        autoConnect = cfg.AutoConnect,
        runAtStartup = IsInStartup(),
        serverEndpoint = cfg.ServerEndpoint,
    };
});

// Save settings and trigger registration
api.MapPost("/setup", async (SetupRequest req, ConfigStore config, ServerApiClient apiClient, WireGuardManager wg) =>
{
    var cfg = config.Load();
    cfg.ServerUrl = req.ServerUrl.TrimEnd('/');
    cfg.DeviceName = req.DeviceName;
    config.Save(cfg);

    // Generate key pair if not set
    if (string.IsNullOrEmpty(cfg.PrivateKey))
    {
        var (priv, pub) = wg.GenerateKeyPair();
        cfg.PrivateKey = priv;
        cfg.PublicKey = pub;
        config.Save(cfg);
    }

    // Register with server
    try
    {
        var result = await apiClient.RegisterAsync(cfg.ServerUrl, cfg.DeviceName, cfg.PublicKey, "windows");
        cfg.PeerId = result.Id;
        cfg.ApprovalStatus = result.Status;
        config.Save(cfg);
        return Results.Ok(new { success = true, status = result.Status, id = result.Id });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = ex.Message });
    }
});

// Update bypass domains
api.MapPost("/domains", (DomainsRequest req, ConfigStore config) =>
{
    var cfg = config.Load();
    cfg.BypassDomains = req.Domains ?? [];
    config.Save(cfg);
    return new { success = true };
});

// Save general settings (autoConnect, runAtStartup)
api.MapPost("/settings", (SettingsRequest req, ConfigStore config) =>
{
    var cfg = config.Load();
    cfg.AutoConnect = req.AutoConnect;
    config.Save(cfg);
    // Sync Windows startup registry
    if (req.RunAtStartup) AddToStartup();
    else RemoveFromStartup();
    return new { saved = true };
});

// Connect/Disconnect tunnel
api.MapPost("/connect", async (ConfigStore config, WireGuardManager wg) =>
{
    var cfg = config.Load();
    if (cfg.ApprovalStatus != "approved")
        return Results.BadRequest(new { error = "Not approved by server yet" });

    try
    {
        await wg.ConnectAsync(cfg);
        return Results.Ok(new { connected = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Detailed WireGuard tunnel diagnostics
api.MapGet("/wg-test", async (ConfigStore config, WireGuardManager wg) =>
{
    var cfg = config.Load();
    var tunnelName = "wgrelay";
    var configDir = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
        "WgRelayClient"
    );
    var confFile = System.IO.Path.Combine(configDir, $"{tunnelName}.conf");

    string confContent = "(file does not exist)";
    try { confContent = System.IO.File.ReadAllText(confFile); } catch { }

    string scQuery = RunProcess("sc.exe", $"query WireGuardTunnel${tunnelName}");
    string scQueryEx = RunProcess("sc.exe", $"queryex WireGuardTunnel${tunnelName}");

    // Check Event Log for WireGuard
    string evtLog = RunProcess("wevtutil.exe", "qe System /q:\"*[System[Provider[@Name='WireGuardTunnel']]]\" /c:5 /rd:true /f:text");

    return Results.Ok(new
    {
        confFilePath = confFile,
        confFileExists = System.IO.File.Exists(confFile),
        confContent,
        scQuery,
        scQueryEx,
        evtLog,
        vpnIp = cfg.VpnIp,
        serverEndpoint = cfg.ServerEndpoint,
        serverPubKey = cfg.ServerPublicKey?.Substring(0, Math.Min(10, cfg.ServerPublicKey?.Length ?? 0)) + "...",
        allowedIps = cfg.VpnSubnet,
        hasPrivKey = !string.IsNullOrEmpty(cfg.PrivateKey)
    });
});

static string RunProcess(string exe, string args)
{
    try
    {
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        proc!.WaitForExit(5000);
        return (proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()).Trim();
    }
    catch (Exception ex) { return $"ERROR: {ex.Message}"; }
}

api.MapPost("/disconnect", async (WireGuardManager wg) =>
{
    await wg.DisconnectAsync();
    return new { connected = false };
});

// Emergency: force-cleanup all WireGuard state (stuck service/adapter)
api.MapPost("/force-cleanup", async () =>
{
    var wgTunnel = @"C:\Program Files\WireGuard\wireguard.exe";
    var tunnelName = "wgrelay";
    var results = new List<string>();

    void Run(string exe, string args)
    {
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = exe, Arguments = args, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
        proc?.WaitForExit(5000);
        results.Add($"{exe} {args} → exit:{proc?.ExitCode}");
    }

    if (File.Exists(wgTunnel)) Run(wgTunnel, $"/uninstalltunnelservice {tunnelName}");
    await Task.Delay(1000);
    Run("sc.exe", $"stop WireGuardTunnel${tunnelName}");
    Run("sc.exe", $"delete WireGuardTunnel${tunnelName}");
    Run("powershell.exe", $"-NonInteractive -Command \"Get-NetAdapter -Name '{tunnelName}' -ErrorAction SilentlyContinue | Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue\"");
    await Task.Delay(1500);

    return Results.Ok(new { steps = results, message = "Cleanup done. Try Connect again." });
});

// Remove all WireGuard tunnel services except 'wgrelay' (fixes routing conflicts)
api.MapPost("/clear-stale-tunnels", async () =>
{
    var wgTunnel = @"C:\Program Files\WireGuard\wireguard.exe";
    const string keepName = "wgrelay";
    var removed = new List<string>();

    string RunAndCapture(string exe, string args)
    {
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = exe, Arguments = args, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
        proc?.WaitForExit(5000);
        return proc?.StandardOutput.ReadToEnd() + proc?.StandardError.ReadToEnd() ?? "";
    }

    var allSvcs = RunAndCapture("sc.exe", "query type=all");
    var staleNames = allSvcs.Split('\n')
        .Where(l => l.TrimStart().StartsWith("SERVICE_NAME: WireGuardTunnel$"))
        .Select(l => l.Trim().Replace("SERVICE_NAME: WireGuardTunnel$", "").Trim())
        .Where(n => n != keepName)
        .ToList();

    foreach (var name in staleNames)
    {
        if (File.Exists(wgTunnel)) RunAndCapture(wgTunnel, $"/uninstalltunnelservice {name}");
        await Task.Delay(500);
        RunAndCapture("sc.exe", $"delete WireGuardTunnel${name}");
        RunAndCapture("powershell.exe",
            $"-NonInteractive -Command \"Get-NetAdapter -Name '{name}' -EA SilentlyContinue | Remove-NetAdapter -Confirm:$false -EA SilentlyContinue\"");
        removed.Add(name);
    }
    await Task.Delay(500);

    return Results.Ok(new { removed, message = removed.Count > 0 ? $"Removed {removed.Count} stale tunnel(s). Routing should be clean now." : "No stale tunnels found." });
});

// Get server info
api.MapGet("/server-info", async (ConfigStore config, ServerApiClient apiClient) =>
{
    var cfg = config.Load();
    if (string.IsNullOrEmpty(cfg.ServerUrl))
        return Results.BadRequest(new { error = "Server URL not set" });
    var info = await apiClient.GetServerInfoAsync(cfg.ServerUrl);
    return Results.Ok(info);
});

// Get all approved peers from server (for peer list / network map)
api.MapGet("/peers", async (ConfigStore config, HttpClient httpClient) =>
{
    var cfg = config.Load();
    if (string.IsNullOrEmpty(cfg.ServerUrl))
        return Results.BadRequest(new { error = "Server URL not set" });
    try
    {
        var url = $"{cfg.ServerUrl}/api/network/peers";
        var peers = await httpClient.GetFromJsonAsync<object>(url);
        return Results.Ok(peers);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Ping a VPN peer by IP
api.MapPost("/ping/{ip}", async (string ip) =>
{
    try
    {
        using var ping = new System.Net.NetworkInformation.Ping();
        var results = new List<object>();
        for (int i = 0; i < 4; i++)
        {
            var reply = await ping.SendPingAsync(ip, 2000);
            results.Add(new
            {
                seq = i + 1,
                status = reply.Status.ToString(),
                latency = reply.Status == System.Net.NetworkInformation.IPStatus.Success ? reply.RoundtripTime : (long)-1
            });
            if (i < 3) await Task.Delay(300);
        }
        var success = results.Count(r => ((dynamic)r).status == "Success");
        return Results.Ok(new { ip, results, success, loss = 4 - success });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Diagnostics: check WireGuard install and tunnel service status
api.MapGet("/diagnostics", (WireGuardManager wg) =>
{
    var wgExe = @"C:\Program Files\WireGuard\wg.exe";
    var wgTunnel = @"C:\Program Files\WireGuard\wireguard.exe";
    bool wgInstalled = File.Exists(wgExe) && File.Exists(wgTunnel);
    bool isAdmin = false;
    try
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    } catch { }
    return new
    {
        wireGuardInstalled = wgInstalled,
        wgExePath = wgExe,
        wgTunnelPath = wgTunnel,
        runningAsAdmin = isAdmin,
        tunnelConnected = wg.IsConnected()
    };
});

// Serve web UI for all non-API routes
app.MapFallback(async context =>
{
    var html = GetEmbeddedHtml();
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});

StartupLog.Write("Starting web server on http://localhost:7432 ...");
app.Run();

} // end try
catch (Exception ex)
{
    var msg = $"STARTUP CRASH:\n{ex.GetType().Name}: {ex.Message}\n\nStack:\n{ex.StackTrace}";
    StartupLog.Write(msg);
    System.Windows.Forms.MessageBox.Show(msg, "WgClient Startup Error",
        System.Windows.Forms.MessageBoxButtons.OK,
        System.Windows.Forms.MessageBoxIcon.Error);
}

static string GetEmbeddedHtml()
{
    var asm = typeof(Program).Assembly;
    var resourceName = asm.GetManifestResourceNames()
        .FirstOrDefault(n => n.EndsWith("index.html"));
    if (resourceName == null) return "<h1>UI Not Found</h1>";
    using var stream = asm.GetManifestResourceStream(resourceName)!;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static void InstallService()
{
    var exe = Environment.ProcessPath!;
    var result = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "sc.exe",
        Arguments = $"create \"WgRelayClient\" binPath=\"{exe}\" start=auto DisplayName=\"WireGuard Relay Client\"",
        Verb = "runas", UseShellExecute = true, CreateNoWindow = true
    });
    result?.WaitForExit();
    Console.WriteLine("Service installed. Run: sc start WgRelayClient");
}

static void UninstallService()
{
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "sc.exe", Arguments = "stop WgRelayClient",
        Verb = "runas", UseShellExecute = true
    })?.WaitForExit();
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "sc.exe", Arguments = "delete WgRelayClient",
        Verb = "runas", UseShellExecute = true
    })?.WaitForExit();
    Console.WriteLine("Service removed.");
}

static void AddToStartup()
{
    var exe = Environment.ProcessPath!;
    using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!;
    key.SetValue("WgRelayClient", $"\"{exe}\"");
    Console.WriteLine("Added to Windows startup.");
}

static void RemoveFromStartup()
{
    using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!;
    key.DeleteValue("WgRelayClient", false);
    Console.WriteLine("Removed from Windows startup.");
}

static bool IsInStartup()
{
    using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
    return key?.GetValue("WgRelayClient") != null;
}

// ─── Request/Response Models ──────────────────────────────────────────
record SetupRequest(string ServerUrl, string DeviceName);
record DomainsRequest(List<string>? Domains);
record SettingsRequest(bool AutoConnect, bool RunAtStartup);
// ─── Startup Diagnostics Logger ─────────────────────────────
static class StartupLog
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "WgClient-startup.log");

    public static void Init()
    {
        File.WriteAllText(LogPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WgClient starting...\r\n");
    }

    public static void Write(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { /* don't crash trying to log */ }
    }
}