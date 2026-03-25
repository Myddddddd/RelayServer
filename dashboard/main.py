"""
WireGuard Dashboard - Main Application
Lightweight server for managing WireGuard peers.
"""
import os
import shlex
import subprocess
import sqlite3
import secrets
import hashlib
import ipaddress
from datetime import datetime, timedelta
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, HTTPException, Depends, status
from fastapi.responses import HTMLResponse, JSONResponse
from fastapi.middleware.cors import CORSMiddleware
from fastapi.security import HTTPBearer, HTTPAuthorizationCredentials
from pydantic import BaseModel

# ─── Config ───────────────────────────────────────────────────
WG_INTERFACE = "wg0"
WG_CONFIG = "/etc/wireguard/wg0.conf"
SERVER_PRIVKEY_FILE = "/etc/wireguard/server_private.key"
SERVER_PUBKEY_FILE = "/etc/wireguard/server_public.key"
VPN_SUBNET = "10.0.0.0/24"
SERVER_VPN_IP = "10.0.0.1"
LISTEN_PORT = 51820
DB_PATH = "/opt/wg-dashboard/peers.db"
SESSION_EXPIRE_HOURS = 24
REPO_PATH_FILE = "/opt/wg-dashboard/.repo-path"
SELF_UPDATE_LOG = "/opt/wg-dashboard/self-update.log"

app = FastAPI(title="WireGuard Dashboard", version="1.0.0")
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])
security = HTTPBearer(auto_error=False)

# ─── Public IP Detection (cached at first call) ──────────────────────
_server_ipv6: str | None = None
_ipv6_detected: bool = False
_server_ip_cache: str | None = None
_ip_detected: bool = False

def get_server_ip() -> str:
    """Return this server's public IPv4. Uses SERVER_IP env var if set, else auto-detects."""
    global _server_ip_cache, _ip_detected
    if _ip_detected and _server_ip_cache:
        return _server_ip_cache
    env_ip = os.environ.get("SERVER_IP", "").strip()
    if env_ip:
        _server_ip_cache = env_ip
        _ip_detected = True
        return _server_ip_cache
    try:
        result = subprocess.run(
            ["curl", "-4", "-s", "--max-time", "5", "ifconfig.me"],
            capture_output=True, text=True, timeout=7
        )
        addr = result.stdout.strip()
        _server_ip_cache = addr if addr else "127.0.0.1"
    except Exception:
        _server_ip_cache = "127.0.0.1"
    finally:
        _ip_detected = True
    return _server_ip_cache

def get_server_ipv6() -> str | None:
    """Detect and cache the server's public IPv6 address.
    Strategy: use local 'ip -6 addr' first (instant, no network), curl as fallback.
    """
    global _server_ipv6, _ipv6_detected
    if _ipv6_detected:
        return _server_ipv6
    # Strategy 1: read IPv6 from network interfaces directly (no external call)
    try:
        result = subprocess.run(
            ["ip", "-6", "addr", "show", "scope", "global"],
            capture_output=True, text=True, timeout=3
        )
        import re
        addrs = re.findall(r"inet6\s+([0-9a-f:]+)/", result.stdout)
        # Filter out private/ULA (fc/fd) and link-local (fe80)
        public = [a for a in addrs
                  if not a.startswith("fe80") and not a.startswith("fc")
                  and not a.startswith("fd") and a != "::1"]
        if public:
            _server_ipv6 = public[0]
            _ipv6_detected = True
            return _server_ipv6
    except Exception:
        pass
    # Strategy 2: curl fallback
    try:
        result = subprocess.run(
            ["curl", "-6", "-s", "--max-time", "5", "ifconfig.me"],
            capture_output=True, text=True, timeout=7
        )
        addr = result.stdout.strip()
        _server_ipv6 = addr if addr and ":" in addr else None
    except Exception:
        _server_ipv6 = None
    finally:
        _ipv6_detected = True
    return _server_ipv6

# ─── Database ─────────────────────────────────────────────────
def get_db():
    Path(DB_PATH).parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn

def hash_password(password: str) -> str:
    """Return 'salt:hash' string using SHA-256"""
    salt = secrets.token_hex(16)
    h = hashlib.sha256((salt + password).encode()).hexdigest()
    return f"{salt}:{h}"

def verify_password(password: str, stored: str) -> bool:
    parts = stored.split(":", 1)
    if len(parts) != 2:
        return False
    salt, h = parts
    return hashlib.sha256((salt + password).encode()).hexdigest() == h

def init_db():
    db = get_db()
    db.execute("""
        CREATE TABLE IF NOT EXISTS peers (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            public_key TEXT UNIQUE NOT NULL,
            vpn_ip TEXT UNIQUE,
            status TEXT DEFAULT 'pending',
            created_at TEXT DEFAULT (datetime('now')),
            approved_at TEXT,
            last_seen TEXT,
            platform TEXT DEFAULT 'unknown'
        )
    """)
    db.execute("""
        CREATE TABLE IF NOT EXISTS admin_users (
            username TEXT PRIMARY KEY,
            password_hash TEXT NOT NULL,
            created_at TEXT DEFAULT (datetime('now'))
        )
    """)
    db.execute("""
        CREATE TABLE IF NOT EXISTS sessions (
            token TEXT PRIMARY KEY,
            username TEXT NOT NULL,
            expires_at TEXT NOT NULL
        )
    """)
    db.commit()

    # Create default admin if no users exist
    count = db.execute("SELECT COUNT(*) FROM admin_users").fetchone()[0]
    if count == 0:
        default_pass = os.environ.get("ADMIN_PASSWORD", "admin123")
        db.execute(
            "INSERT INTO admin_users (username, password_hash) VALUES (?, ?)",
            ("admin", hash_password(default_pass))
        )
        db.commit()
        print(f"[INIT] Default admin created: username=admin  password={default_pass}")
        # Also write to file for reference
        try:
            Path("/opt/wg-dashboard/ADMIN_CREDENTIALS.txt").write_text(
                f"username: admin\npassword: {default_pass}\n"
                f"IMPORTANT: Change this password after first login!\n"
            )
        except:
            pass

    db.close()

init_db()

# ─── Auth ─────────────────────────────────────────────────────
def require_admin(credentials: Optional[HTTPAuthorizationCredentials] = Depends(security)):
    if not credentials:
        raise HTTPException(status_code=401, detail="Not authenticated")
    token = credentials.credentials
    db = get_db()
    session = db.execute(
        "SELECT * FROM sessions WHERE token = ? AND expires_at > datetime('now')",
        (token,)
    ).fetchone()
    db.close()
    if not session:
        raise HTTPException(status_code=401, detail="Session expired or invalid")
    return session["username"]

# ─── WireGuard helpers ─────────────────────────────────────────
def get_server_private_key() -> str:
    try:
        key = Path(SERVER_PRIVKEY_FILE).read_text().strip().replace("\r", "")
        if key:
            return "".join(key.split())
    except Exception:
        pass

    try:
        for line in Path(WG_CONFIG).read_text().splitlines():
            stripped = line.strip()
            if stripped.startswith("PrivateKey") and "=" in stripped:
                value = stripped.split("=", 1)[1].split("#", 1)[0]
                value = "".join(value.split()).replace("\r", "")
                if value:
                    return value
    except Exception:
        pass

    return ""

def get_server_pubkey() -> str:
    try:
        key = Path(SERVER_PUBKEY_FILE).read_text().strip()
        if key:
            return key
    except:
        pass

    try:
        result = subprocess.run(
            ["wg", "show", WG_INTERFACE, "public-key"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        key = result.stdout.strip()
        if key:
            try:
                Path(SERVER_PUBKEY_FILE).write_text(key + "\n")
            except:
                pass
            return key
    except:
        pass

    try:
        private_key = get_server_private_key()
        if private_key:
            result = subprocess.run(
                ["wg", "pubkey"],
                input=private_key,
                capture_output=True,
                text=True,
                timeout=5,
            )
            key = result.stdout.strip()
            if key:
                try:
                    Path(SERVER_PUBKEY_FILE).write_text(key + "\n")
                except:
                    pass
                return key
    except:
        pass

    return ""

def regenerate_server_pubkey() -> tuple[str, str]:
    try:
        result = subprocess.run(
            ["wg", "show", WG_INTERFACE, "public-key"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        key = result.stdout.strip()
        if key:
            Path(SERVER_PUBKEY_FILE).write_text(key + "\n")
            return key, "wg-show"
    except Exception:
        pass

    try:
        private_key = get_server_private_key()
        if private_key:
            result = subprocess.run(
                ["wg", "pubkey"],
                input=private_key,
                capture_output=True,
                text=True,
                timeout=5,
            )
            key = result.stdout.strip()
            if key:
                Path(SERVER_PUBKEY_FILE).write_text(key + "\n")
                source = "server-private-key-file" if Path(SERVER_PRIVKEY_FILE).exists() else "wg-config-private-key"
                return key, source
    except Exception:
        pass

    raise RuntimeError("Unable to regenerate server public key from wg interface or wg0.conf")

def get_repo_dir() -> str:
    env_repo = os.environ.get("WG_RELAY_REPO", "").strip()
    if env_repo:
        return env_repo

    try:
        repo_dir = Path(REPO_PATH_FILE).read_text().strip()
        if repo_dir:
            return repo_dir
    except Exception:
        pass

    raise RuntimeError("Repository path is unknown. Run update-server.sh once from the repo checkout first.")

def tail_file(path: str, line_count: int = 40) -> str:
    try:
        lines = Path(path).read_text().splitlines()
        return "\n".join(lines[-line_count:])
    except Exception:
        return ""

def allocate_vpn_ip() -> str:
    """Find next available IP in VPN subnet"""
    db = get_db()
    used_ips = {row["vpn_ip"] for row in db.execute("SELECT vpn_ip FROM peers WHERE vpn_ip IS NOT NULL")}
    db.close()
    
    network = ipaddress.IPv4Network(VPN_SUBNET)
    for host in network.hosts():
        ip = str(host)
        if ip == SERVER_VPN_IP:
            continue
        if ip not in used_ips:
            return ip
    raise RuntimeError("No available IPs in VPN subnet")

def wg_add_peer(public_key: str, vpn_ip: str):
    """Add peer to running WireGuard interface and persist to config"""
    # Add to running interface
    subprocess.run([
        "wg", "set", WG_INTERFACE,
        "peer", public_key,
        "allowed-ips", f"{vpn_ip}/32"
    ], check=True)
    
    # Persist to config file
    peer_block = f"\n[Peer]\nPublicKey = {public_key}\nAllowedIPs = {vpn_ip}/32\n"
    with open(WG_CONFIG, "a") as f:
        f.write(peer_block)

def wg_remove_peer(public_key: str):
    """Remove peer from WireGuard"""
    subprocess.run([
        "wg", "set", WG_INTERFACE, "peer", public_key, "remove"
    ], check=False)
    
    # Reload config to persist removal
    subprocess.run(["wg-quick", "save", WG_INTERFACE], check=False)

def wg_get_peer_stats() -> dict:
    """Get peer connection stats from wg show"""
    result = subprocess.run(
        ["wg", "show", WG_INTERFACE, "dump"],
        capture_output=True, text=True
    )
    stats = {}
    for line in result.stdout.strip().split("\n")[1:]:  # Skip interface line
        parts = line.split("\t")
        if len(parts) >= 5:
            pubkey = parts[0]
            endpoint = parts[2]
            last_handshake = parts[4]
            stats[pubkey] = {
                "endpoint": endpoint if endpoint != "(none)" else None,
                "last_handshake": int(last_handshake) if last_handshake.isdigit() else 0,
            }
    return stats

# ─── Models ───────────────────────────────────────────────────
class RegisterRequest(BaseModel):
    name: str
    public_key: str
    platform: str = "unknown"

class PollRequest(BaseModel):
    public_key: str

class LoginRequest(BaseModel):
    username: str
    password: str

class CreateUserRequest(BaseModel):
    username: str
    password: str

class ChangePasswordRequest(BaseModel):
    new_password: str

# ─── Auth Endpoints ─────────────────────────────────────────────

@app.post("/api/admin/login")
def login(req: LoginRequest):
    db = get_db()
    user = db.execute("SELECT * FROM admin_users WHERE username = ?", (req.username,)).fetchone()
    if not user or not verify_password(req.password, user["password_hash"]):
        db.close()
        raise HTTPException(status_code=401, detail="Invalid username or password")
    
    token = secrets.token_hex(32)
    expires = (datetime.utcnow() + timedelta(hours=SESSION_EXPIRE_HOURS)).strftime("%Y-%m-%d %H:%M:%S")
    db.execute("INSERT INTO sessions (token, username, expires_at) VALUES (?, ?, ?)", (token, req.username, expires))
    db.commit()
    db.close()
    return {"token": token, "username": req.username, "expires_at": expires}

@app.post("/api/admin/logout")
def logout(username: str = Depends(require_admin), credentials: Optional[HTTPAuthorizationCredentials] = Depends(security)):
    if credentials:
        db = get_db()
        db.execute("DELETE FROM sessions WHERE token = ?", (credentials.credentials,))
        db.commit()
        db.close()
    return {"message": "Logged out"}

@app.get("/api/admin/me")
def me(username: str = Depends(require_admin)):
    return {"username": username}

@app.get("/api/admin/users")
def list_users(username: str = Depends(require_admin)):
    db = get_db()
    users = db.execute("SELECT username, created_at FROM admin_users ORDER BY created_at").fetchall()
    db.close()
    return [{"username": u["username"], "created_at": u["created_at"]} for u in users]

@app.post("/api/admin/users")
def create_user(req: CreateUserRequest, username: str = Depends(require_admin)):
    if len(req.username) < 3:
        raise HTTPException(status_code=400, detail="Username must be at least 3 characters")
    if len(req.password) < 4:
        raise HTTPException(status_code=400, detail="Password must be at least 4 characters")
    db = get_db()
    existing = db.execute("SELECT 1 FROM admin_users WHERE username = ?", (req.username,)).fetchone()
    if existing:
        db.close()
        raise HTTPException(status_code=409, detail="Username already exists")
    db.execute("INSERT INTO admin_users (username, password_hash) VALUES (?, ?)",
               (req.username, hash_password(req.password)))
    db.commit()
    db.close()
    return {"message": f"User '{req.username}' created"}

@app.delete("/api/admin/users/{target_username}")
def delete_user(target_username: str, username: str = Depends(require_admin)):
    if target_username == username:
        raise HTTPException(status_code=400, detail="Cannot delete your own account")
    db = get_db()
    count = db.execute("SELECT COUNT(*) FROM admin_users").fetchone()[0]
    if count <= 1:
        db.close()
        raise HTTPException(status_code=400, detail="Cannot delete the last admin user")
    result = db.execute("DELETE FROM admin_users WHERE username = ?", (target_username,))
    # Also invalidate sessions for that user
    db.execute("DELETE FROM sessions WHERE username = ?", (target_username,))
    db.commit()
    db.close()
    if result.rowcount == 0:
        raise HTTPException(status_code=404, detail="User not found")
    return {"message": f"User '{target_username}' deleted"}

@app.put("/api/admin/users/{target_username}/password")
def change_password(target_username: str, req: ChangePasswordRequest, username: str = Depends(require_admin)):
    if len(req.new_password) < 4:
        raise HTTPException(status_code=400, detail="Password must be at least 4 characters")
    db = get_db()
    result = db.execute("UPDATE admin_users SET password_hash = ? WHERE username = ?",
                        (hash_password(req.new_password), target_username))
    db.execute("DELETE FROM sessions WHERE username = ?", (target_username,))
    db.commit()
    db.close()
    if result.rowcount == 0:
        raise HTTPException(status_code=404, detail="User not found")
    return {"message": "Password changed"}

# ─── API Endpoints ─────────────────────────────────────────────

@app.get("/api/server-info")
def server_info():
    """Get server public key and info - used by clients during setup"""
    server_ip = get_server_ip()
    ipv6 = get_server_ipv6()
    result = {
        "server_public_key": get_server_pubkey(),
        "endpoint": f"{server_ip}:{LISTEN_PORT}",
        "vpn_subnet": VPN_SUBNET,
        "dns": SERVER_VPN_IP,
    }
    if ipv6:
        result["endpoint_ipv6"] = f"[{ipv6}]:{LISTEN_PORT}"
    return result

@app.post("/api/admin/refresh-server-ip")
def refresh_server_ip(_: str = Depends(require_admin)):
    """Admin: reset IP detection cache and re-detect."""
    global _server_ip_cache, _ip_detected, _server_ipv6, _ipv6_detected
    _ip_detected = False
    _server_ip_cache = None
    _ipv6_detected = False
    _server_ipv6 = None
    ipv4 = get_server_ip()
    ipv6 = get_server_ipv6()
    return {"ipv4": ipv4, "ipv6": ipv6}

@app.post("/api/admin/regenerate-server-public-key")
def regenerate_server_public_key(_: str = Depends(require_admin)):
    try:
        key, source = regenerate_server_pubkey()
        return {"server_public_key": key, "source": source}
    except RuntimeError as exc:
        raise HTTPException(status_code=500, detail=str(exc))

@app.post("/api/admin/self-update")
def self_update(_: str = Depends(require_admin)):
    try:
        repo_dir = get_repo_dir()
    except RuntimeError as exc:
        raise HTTPException(status_code=400, detail=str(exc))

    update_script = Path(repo_dir) / "update-server.sh"
    if not update_script.exists():
        raise HTTPException(status_code=404, detail=f"update-server.sh not found in repo: {repo_dir}")

    try:
        with open(SELF_UPDATE_LOG, "a", encoding="utf-8") as log_file:
            log_file.write(f"\n[{datetime.utcnow().isoformat()}Z] Starting self-update from dashboard\n")

        command = (
            f"cd {shlex.quote(repo_dir)} && "
            f"git pull && "
            f"bash {shlex.quote(str(update_script))} >> {shlex.quote(SELF_UPDATE_LOG)} 2>&1"
        )
        subprocess.Popen(
            ["nohup", "bash", "-lc", command],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
        )
        return {"started": True, "repo_dir": repo_dir, "log_path": SELF_UPDATE_LOG}
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"Failed to start self-update: {exc}")

@app.get("/api/admin/self-update-status")
def self_update_status(_: str = Depends(require_admin)):
    repo_dir = ""
    try:
        repo_dir = get_repo_dir()
    except RuntimeError:
        pass
    return {
        "repo_dir": repo_dir,
        "log_path": SELF_UPDATE_LOG,
        "log_tail": tail_file(SELF_UPDATE_LOG),
    }

@app.get("/api/network/peers")
def network_peers():
    """Public endpoint: returns approved peers list (name, vpnIp, platform).
    No auth required — accessible by any connected peer."""
    db = get_db()
    peers = db.execute(
        "SELECT name, vpn_ip, platform FROM peers WHERE status = 'approved' AND vpn_ip IS NOT NULL"
    ).fetchall()
    db.close()
    return [{"name": p["name"], "vpn_ip": p["vpn_ip"], "platform": p["platform"]} for p in peers]

@app.post("/api/register")
def register_peer(req: RegisterRequest):
    """Client registers with its public key - creates pending request"""
    db = get_db()
    
    # Check if already registered
    existing = db.execute("SELECT * FROM peers WHERE public_key = ?", (req.public_key,)).fetchone()
    if existing:
        db.close()
        status_val = existing["status"]
        if status_val == "approved":
            return {"status": "approved", "id": existing["id"]}
        elif status_val == "rejected":
            raise HTTPException(status_code=403, detail="Registration rejected by admin")
        return {"status": "pending", "id": existing["id"]}
    
    peer_id = secrets.token_hex(8)
    db.execute(
        "INSERT INTO peers (id, name, public_key, platform, status) VALUES (?, ?, ?, ?, 'pending')",
        (peer_id, req.name, req.public_key, req.platform)
    )
    db.commit()
    db.close()
    return {"status": "pending", "id": peer_id, "message": "Registration submitted, waiting for admin approval"}

@app.get("/api/poll/{peer_id}")
def poll_status(peer_id: str):
    """Client polls to check if approved and get config"""
    db = get_db()
    peer = db.execute("SELECT * FROM peers WHERE id = ?", (peer_id,)).fetchone()
    db.close()
    
    if not peer:
        raise HTTPException(status_code=404, detail="Peer not found")
    
    if peer["status"] == "pending":
        return {"status": "pending"}
    
    if peer["status"] == "rejected":
        raise HTTPException(status_code=403, detail="Registration rejected")
    
    if peer["status"] == "approved":
        server_pubkey = get_server_pubkey()
        server_ip = get_server_ip()
        ipv6 = get_server_ipv6()
        config_data = {
            "vpn_ip": peer["vpn_ip"],
            "server_public_key": server_pubkey,
            "server_endpoint": f"{server_ip}:{LISTEN_PORT}",
            "dns": SERVER_VPN_IP,
            "allowed_ips": VPN_SUBNET,
        }
        if ipv6:
            config_data["server_endpoint_ipv6"] = f"[{ipv6}]:{LISTEN_PORT}"
        return {"status": "approved", "config": config_data}

@app.get("/api/config/{peer_id}")
def get_config_file(peer_id: str):
    """Returns WireGuard config file content"""
    db = get_db()
    peer = db.execute("SELECT * FROM peers WHERE id = ?", (peer_id,)).fetchone()
    db.close()
    
    if not peer or peer["status"] != "approved":
        raise HTTPException(status_code=403, detail="Not approved")
    
    server_pubkey = get_server_pubkey()
    server_ip = get_server_ip()
    
    config = f"""[Interface]
Address = {peer["vpn_ip"]}/24
DNS = {SERVER_VPN_IP}
# PrivateKey = <INSERT YOUR PRIVATE KEY HERE>

[Peer]
PublicKey = {server_pubkey}
Endpoint = {server_ip}:{LISTEN_PORT}
AllowedIPs = {VPN_SUBNET}
PersistentKeepalive = 25
"""
    from fastapi.responses import PlainTextResponse
    return PlainTextResponse(content=config, media_type="text/plain")

# ─── Admin Endpoints ───────────────────────────────────────────

@app.get("/api/admin/peers")
def list_peers(_: bool = Depends(require_admin)):
    """Admin: list all peers"""
    db = get_db()
    peers = db.execute("SELECT * FROM peers ORDER BY created_at DESC").fetchall()
    db.close()
    
    stats = {}
    try:
        stats = wg_get_peer_stats()
    except:
        pass
    
    result = []
    for p in peers:
        peer_stats = stats.get(p["public_key"], {})
        result.append({
            "id": p["id"],
            "name": p["name"],
            "public_key": p["public_key"],
            "vpn_ip": p["vpn_ip"],
            "status": p["status"],
            "platform": p["platform"],
            "created_at": p["created_at"],
            "approved_at": p["approved_at"],
            "online": peer_stats.get("last_handshake", 0) > 0,
            "endpoint": peer_stats.get("endpoint"),
        })
    return result

@app.post("/api/admin/approve/{peer_id}")
def approve_peer(peer_id: str, _: bool = Depends(require_admin)):
    """Admin: approve a pending peer"""
    db = get_db()
    peer = db.execute("SELECT * FROM peers WHERE id = ?", (peer_id,)).fetchone()
    
    if not peer:
        db.close()
        raise HTTPException(status_code=404, detail="Peer not found")
    if peer["status"] == "approved":
        db.close()
        return {"message": "Already approved", "vpn_ip": peer["vpn_ip"]}
    
    vpn_ip = allocate_vpn_ip()
    
    try:
        wg_add_peer(peer["public_key"], vpn_ip)
    except Exception as e:
        db.close()
        raise HTTPException(status_code=500, detail=f"WireGuard error: {e}")
    
    db.execute(
        "UPDATE peers SET status='approved', vpn_ip=?, approved_at=datetime('now') WHERE id=?",
        (vpn_ip, peer_id)
    )
    db.commit()
    db.close()
    return {"message": "Approved", "vpn_ip": vpn_ip}

@app.post("/api/admin/reject/{peer_id}")
def reject_peer(peer_id: str, _: bool = Depends(require_admin)):
    """Admin: reject a peer"""
    db = get_db()
    peer = db.execute("SELECT * FROM peers WHERE id = ?", (peer_id,)).fetchone()
    if not peer:
        db.close()
        raise HTTPException(status_code=404, detail="Peer not found")
    
    if peer["status"] == "approved" and peer["vpn_ip"]:
        try:
            wg_remove_peer(peer["public_key"])
        except:
            pass
    
    db.execute("UPDATE peers SET status='rejected', vpn_ip=NULL WHERE id=?", (peer_id,))
    db.commit()
    db.close()
    return {"message": "Rejected"}

@app.delete("/api/admin/peer/{peer_id}")
def delete_peer(peer_id: str, _: bool = Depends(require_admin)):
    """Admin: delete a peer entirely"""
    db = get_db()
    peer = db.execute("SELECT * FROM peers WHERE id = ?", (peer_id,)).fetchone()
    if not peer:
        db.close()
        raise HTTPException(status_code=404, detail="Peer not found")
    
    if peer["public_key"]:
        try:
            wg_remove_peer(peer["public_key"])
        except:
            pass
    
    db.execute("DELETE FROM peers WHERE id=?", (peer_id,))
    db.commit()
    db.close()
    return {"message": "Deleted"}

@app.get("/api/admin/wg-status")
def wg_status(_: bool = Depends(require_admin)):
    """Admin: get raw WireGuard status"""
    result = subprocess.run(["wg", "show", WG_INTERFACE], capture_output=True, text=True)
    return {"output": result.stdout}

# ─── Admin Dashboard UI ────────────────────────────────────────
@app.get("/", response_class=HTMLResponse)
def dashboard():
    html_path = Path("/opt/wg-dashboard/index.html")
    if html_path.exists():
        return HTMLResponse(content=html_path.read_text())
    return HTMLResponse(content="<h1>WireGuard Dashboard</h1><p>UI not found. Place index.html in /opt/wg-dashboard/</p>")

@app.get("/health")
def health():
    return {"status": "ok", "time": datetime.now().isoformat()}
