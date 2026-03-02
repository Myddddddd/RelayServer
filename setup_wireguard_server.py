"""
Server setup script for WireGuard + Dashboard.
Runs all setup remotely via SSH using paramiko.
"""
import paramiko
import time
import sys

HOST = "103.126.161.38"
PORT = 22
USERNAME = "root"

# Load private key
pkey = paramiko.RSAKey.from_private_key_file(
    r"d:\thongvamProject\RelayServer\VinaHostSSH",
    password="132456"
)

def ssh_connect():
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(HOST, port=PORT, username=USERNAME, pkey=pkey, timeout=20)
    return client

def run(client, cmd, timeout=60, print_output=True):
    print(f"\n$ {cmd}")
    stdin, stdout, stderr = client.exec_command(cmd, timeout=timeout)
    out = stdout.read().decode()
    err = stderr.read().decode()
    if out and print_output:
        print(out.rstrip())
    if err and print_output:
        print(f"[err] {err.rstrip()}", file=sys.stderr)
    return out, err

def run_long(client, cmd, wait=10):
    """For commands that might take a while"""
    print(f"\n$ {cmd}")
    stdin, stdout, stderr = client.exec_command(cmd, timeout=120)
    time.sleep(wait)
    out = stdout.read().decode()
    err = stderr.read().decode()
    if out:
        print(out.rstrip())
    if err:
        print(f"[err] {err.rstrip()}", file=sys.stderr)
    return out, err

print("="*60)
print("STEP 1: Install WireGuard")
print("="*60)
client = ssh_connect()

run(client, "apt-get update -y", timeout=120)
run(client, "apt-get install -y wireguard wireguard-tools python3-pip python3-venv", timeout=180)
run(client, "wg --version")

print("\n" + "="*60)
print("STEP 2: Generate WireGuard Server Keys")
print("="*60)
run(client, "mkdir -p /etc/wireguard && chmod 700 /etc/wireguard")
run(client, "wg genkey | tee /etc/wireguard/server_private.key | wg pubkey > /etc/wireguard/server_public.key")
run(client, "chmod 600 /etc/wireguard/server_private.key")

server_privkey, _ = run(client, "cat /etc/wireguard/server_private.key")
server_pubkey, _ = run(client, "cat /etc/wireguard/server_public.key")
server_privkey = server_privkey.strip()
server_pubkey = server_pubkey.strip()
print(f"\nServer Public Key: {server_pubkey}")

print("\n" + "="*60)
print("STEP 3: Configure WireGuard (wg0)")
print("="*60)
wg_config = f"""[Interface]
Address = 10.0.0.1/24
ListenPort = 51820
PrivateKey = {server_privkey}

# Enable routing (added by PostUp/PreDown)
PostUp = iptables -A FORWARD -i wg0 -j ACCEPT; iptables -A FORWARD -o wg0 -j ACCEPT; iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE
PreDown = iptables -D FORWARD -i wg0 -j ACCEPT; iptables -D FORWARD -o wg0 -j ACCEPT; iptables -t nat -D POSTROUTING -o eth0 -j MASQUERADE
"""

run(client, f"cat > /etc/wireguard/wg0.conf << 'WGEOF'\n{wg_config}\nWGEOF")
run(client, "cat /etc/wireguard/wg0.conf")

print("\n" + "="*60)
print("STEP 4: Enable IP Forwarding")
print("="*60)
run(client, "echo 'net.ipv4.ip_forward=1' >> /etc/sysctl.conf")
run(client, "sysctl -p")

print("\n" + "="*60)
print("STEP 5: Start WireGuard Service")
print("="*60)
run(client, "systemctl enable wg-quick@wg0")
run(client, "systemctl start wg-quick@wg0")
run(client, "systemctl status wg-quick@wg0 --no-pager")
run(client, "wg show")

# Save server public key for use in clients
with open(r"d:\thongvamProject\RelayServer\server_pubkey.txt", "w") as f:
    f.write(server_pubkey)

print("\n" + "="*60)
print(f"WireGuard Server setup complete!")
print(f"Server VPN IP: 10.0.0.1")
print(f"Server Public Key: {server_pubkey}")
print(f"Saved to: d:\\thongvamProject\\RelayServer\\server_pubkey.txt")
print("="*60)

client.close()
