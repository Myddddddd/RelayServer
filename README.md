# WireGuard Relay — Fake P2P Network

A self-hosted WireGuard relay system that makes all devices appear to communicate peer-to-peer through a central VPS. Includes a server dashboard, Windows client, and Android client.

---

## Architecture

```
[Android App] ──┐
                ├──→ [WireGuard VPS (10.0.0.1)] ← hub-and-spoke relay
[Windows App] ──┘         │
                     [Dashboard :8080]
                     (admin approve devices)
```

All client traffic passes through the VPS. Clients can selectively route specific domains through the VPN (split tunneling).

---

## Server

**Location:** Ubuntu VPS at `103.126.162.46`

### Components
- **WireGuard** — `wg0` interface at `10.0.0.1/24`, UDP port `51820`
- **Dashboard API** — Python FastAPI at `:8080`
- **Database** — SQLite at `/opt/wg-dashboard/peers.db`

### Server Info
| | |
|---|---|
| VPN Subnet | `10.0.0.0/24` |
| Server VPN IP | `10.0.0.1` |
| WireGuard Port | `51820/UDP` |
| Dashboard URL | `http://103.126.162.46:8080` |
| Admin Token | `wg-relay-2026` |
| Server Public Key | `5egwDvIu4h5eawsFpoYjrrK0fQ4sWJpgWUG2EegOM0E=` |

### Dashboard API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/server-info` | Get server public key, endpoint, subnet |
| POST | `/api/register` | Client registers with public key |
| GET | `/api/poll/{id}` | Client polls for approval status |
| GET | `/api/config/{id}` | Client downloads WireGuard config |
| GET | `/api/admin/peers` | Admin: list all peers |
| POST | `/api/admin/approve/{id}` | Admin: approve a peer |
| POST | `/api/admin/reject/{id}` | Admin: reject a peer |
| DELETE | `/api/admin/peer/{id}` | Admin: remove a peer |

### Service Management (on VPS)
```bash
# WireGuard
systemctl status wg-quick@wg0
wg show

# Dashboard
systemctl status wg-dashboard
journalctl -u wg-dashboard -f

# Restart both
systemctl restart wg-quick@wg0 wg-dashboard
```

### Redeploy Dashboard
```bash
python deploy_dashboard.py
```

### Update Server After Pull
```bash
git pull
sudo bash update-server.sh
```

This script:
- repairs `/etc/wireguard/server_public.key` from `wg0.conf` if it is missing,
- syncs dashboard source into `/opt/wg-dashboard`,
- installs Python dependencies into the server venv,
- refreshes `wg-dashboard.service`,
- restarts `wg-quick@wg0` and `wg-dashboard`,
- prints the local `/api/server-info` payload for verification.

---

## Windows Client

**Location:** `windows-client/`

### Prerequisites
- [WireGuard for Windows](https://www.wireguard.com/install/) — required for tunnel management
- .NET 8 Runtime (or publish self-contained)

### Build & Run
```powershell
cd windows-client
dotnet build
dotnet run          # Development mode
```

### Publish (single .exe)
```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish
```

### Install as Windows Service
```powershell
# Run once as Administrator
sc create "WgRelayClient" binPath="C:\path\to\WgClient.exe" start=auto
sc start WgRelayClient
```

### Usage
1. Run `WgClient.exe` (admin rights needed for WireGuard tunnel)
2. Open `http://localhost:7432` in browser
3. Enter Server URL (`http://103.126.162.46:8080`) and device name
4. Click **Register Device**
5. Wait for admin approval in dashboard at `http://103.126.162.46:8080`
6. Click **Connect** when approved

### Config Storage
Config stored at: `%LOCALAPPDATA%\WgRelayClient\config.json`

### Domain Routing
Enter domains in the UI — those domains' IPs will be routed through VPN. Leave empty to route all traffic.

---

## Android Client

**Location:** `android-client/`

### Prerequisites
- Android Studio
- Android SDK (min API 21)

### Setup
1. Open `android-client/` in Android Studio
2. Build and install on device

### Usage
1. Open app → Enter server URL → Tap **Register**
2. Wait for admin approval
3. Tap **Connect VPN** when approved
4. Add domains to selective routing list (optional)

---

## Flow Diagram

```
Client                          Server Dashboard
  │                                    │
  ├── GET /api/server-info ──────────→ │  (get public key)
  │                                    │
  ├── POST /api/register  ──────────→ │  (send public key)
  │ ← {status:"pending", id:"..."}     │
  │                                    │
  │  [Admin approves in dashboard]     │
  │                            POST /api/admin/approve/{id}
  │                         WireGuard adds peer
  │                                    │
  ├── GET /api/poll/{id} ───────────→ │
  │ ← {status:"approved",             │
  │     config:{vpn_ip, endpoint}}     │
  │                                    │
  ├── [Create WireGuard tunnel]        │
  │                                    │
  └── UDP :51820 ────────────────────→ WireGuard server
```

---

## Security Notes

1. Change `ADMIN_TOKEN` in `/etc/systemd/system/wg-dashboard.service` before production
2. Consider restricting dashboard port with firewall: `ufw allow from <your-ip> to any port 8080`
3. Private keys are stored locally, never sent to server
4. Dashboard only stores public keys
5. Consider adding HTTPS with nginx reverse proxy for production

---

## Files

```
RelayServer/
├── AGENTS.md                     - Copilot agent rules
├── push_ssh_key.py               - Script to push SSH key to VPS
├── setup_wireguard_server.py     - WireGuard server setup script
├── deploy_dashboard.py           - Dashboard deployment script
├── server_pubkey.txt             - Server WireGuard public key
├── vinahostVPSPublic             - SSH public key (RFC 4716)
├── VinaHostSSH                   - SSH private key (OpenSSH)
├── vinaHostVPSPrivate.ppk        - SSH private key (PuTTY)
├── dashboard/                    - FastAPI dashboard source
│   ├── main.py                   - API application
│   ├── index.html                - Dashboard web UI
│   ├── requirements.txt          - Python dependencies
│   └── wg-dashboard.service      - Systemd service file
├── windows-client/               - C# Windows Service
│   ├── Program.cs                - Entry + ASP.NET Core API
│   ├── Services/
│   │   ├── ConfigStore.cs        - Local config persistence
│   │   ├── ServerApiClient.cs    - Dashboard REST client
│   │   ├── WireGuardManager.cs   - WireGuard tunnel management
│   │   └── TunnelWorker.cs       - Background polling worker
│   └── wwwroot/index.html        - Local web UI (localhost:7432)
└── android-client/               - Kotlin Android app
    ├── build.gradle.kts          - Root Gradle config
    ├── settings.gradle.kts       - Module + repo config
    ├── gradle.properties
    ├── gradle/libs.versions.toml - Version catalog
    └── app/
        ├── build.gradle.kts      - App dependencies + WG library
        ├── proguard-rules.pro
        └── src/main/
            ├── AndroidManifest.xml
            ├── java/com/wgrelay/client/
            │   ├── MainActivity.kt          - Main UI
            │   ├── MainViewModel.kt         - State management
            │   ├── ConfigStore.kt           - DataStore persistence
            │   ├── ServerApiClient.kt       - Dashboard REST client
            │   └── WireGuardVpnService.kt   - WireGuard VPN tunnel
            └── res/
                ├── layout/activity_main.xml
                ├── values/themes.xml
                ├── values/strings.xml
                └── drawable/
```
