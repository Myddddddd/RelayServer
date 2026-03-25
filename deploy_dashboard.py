"""
Deploy WireGuard Dashboard to VPS via SSH.
Uploads all files, installs dependencies, sets up systemd service.
"""
import paramiko
from pathlib import Path
import sys

HOST = "103.126.162.46"
PORT = 22
USERNAME = "root"
ADMIN_TOKEN = "wg-relay-2026"  # Change this!

pkey = paramiko.RSAKey.from_private_key_file(
    r"d:\thongvamProject\RelayServer\VinaHostSSH",
    password="132456"
)

def ssh_connect():
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(HOST, port=PORT, username=USERNAME, pkey=pkey, timeout=20)
    return client

def run(client, cmd, timeout=60):
    print(f"\n$ {cmd}")
    stdin, stdout, stderr = client.exec_command(cmd, timeout=timeout, get_pty=False)
    out = stdout.read().decode()
    err = stderr.read().decode()
    if out:
        print(out.rstrip())
    if err and "warning" not in err.lower():
        print(f"[err] {err.rstrip()}", file=sys.stderr)
    return out, err

def upload_file(sftp, local_path: str, remote_path: str):
    print(f"  Uploading {Path(local_path).name} → {remote_path}")
    sftp.put(local_path, remote_path)

print("="*60)
print("Deploying WireGuard Dashboard")
print("="*60)

client = ssh_connect()
sftp = client.open_sftp()

print("\n[1] Creating deployment directory...")
run(client, "mkdir -p /opt/wg-dashboard")

print("\n[2] Uploading dashboard files...")
local_dir = Path(r"d:\thongvamProject\RelayServer\dashboard")
upload_file(sftp, str(local_dir / "main.py"), "/opt/wg-dashboard/main.py")
upload_file(sftp, str(local_dir / "requirements.txt"), "/opt/wg-dashboard/requirements.txt")
upload_file(sftp, str(local_dir / "index.html"), "/opt/wg-dashboard/index.html")

print("\n[3] Setting up Python venv and installing dependencies...")
run(client, "python3 -m venv /opt/wg-dashboard/venv", timeout=30)
run(client, "/opt/wg-dashboard/venv/bin/pip install -q --upgrade pip", timeout=60)
run(client, f"/opt/wg-dashboard/venv/bin/pip install -q -r /opt/wg-dashboard/requirements.txt", timeout=120)

print("\n[4] Installing systemd service...")
upload_file(sftp, str(local_dir / "wg-dashboard.service"), "/etc/systemd/system/wg-dashboard.service")

# Update token and server IP in service file
run(client, f"sed -i 's/changeme-admin-token/{ADMIN_TOKEN}/g' /etc/systemd/system/wg-dashboard.service")

print("\n[5] Enable and start service...")
run(client, "systemctl daemon-reload")
run(client, "systemctl enable wg-dashboard")
run(client, "systemctl restart wg-dashboard")
run(client, "systemctl status wg-dashboard --no-pager")

print("\n[6] Open firewall port 8080...")
run(client, "ufw allow 8080/tcp comment 'WG Dashboard' 2>/dev/null || true")
run(client, "ufw allow 51820/udp comment 'WireGuard' 2>/dev/null || true")

print("\n[7] Verify service is running...")
out, _ = run(client, "curl -s http://localhost:8080/health")
print(f"Health check: {out}")

sftp.close()
client.close()

print("\n" + "="*60)
print("Dashboard deployed successfully!")
print(f"URL:         http://{HOST}:8080")
print(f"Admin Token: {ADMIN_TOKEN}")
print(f"API Info:    http://{HOST}:8080/api/server-info")
print("="*60)
print(f"\nSave this token! Used to access admin panel and in apps.")
print(f"ADMIN_TOKEN = {ADMIN_TOKEN}")
