"""
Push SSH public key to VinaHost VPS server.
Reads the RFC 4716 public key, converts to OpenSSH format,
then adds it to ~/.ssh/authorized_keys on the remote server.
"""
import paramiko
import re
import sys

# --- Config ---
HOST = "103.126.162.46"
PORT = 22
USERNAME = "root"
PASSWORD = "TEm02Rw2e6Uf"
PUBKEY_FILE = r"d:\thongvamProject\RelayServer\vinahostVPSPublic"

# --- Read and convert public key from RFC 4716 to OpenSSH format ---
def read_ssh2_pubkey(filepath):
    with open(filepath, "r") as f:
        content = f.read()

    # Extract base64 body between BEGIN/END markers, skipping Comment lines
    in_key = False
    b64_lines = []
    for line in content.splitlines():
        if line.startswith("---- BEGIN SSH2 PUBLIC KEY ----"):
            in_key = True
            continue
        if line.startswith("---- END SSH2 PUBLIC KEY ----"):
            break
        if in_key:
            if line.startswith("Comment:"):
                comment = re.search(r'Comment:\s*"?(.+?)"?\s*$', line)
                comment = comment.group(1) if comment else "vinahostVPS"
            else:
                b64_lines.append(line.strip())

    b64 = "".join(b64_lines)
    openssh_key = f"ssh-rsa {b64} vinahostVPS"
    return openssh_key

# --- SSH and push key ---
def push_key(host, port, username, password, pubkey_line):
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())

    print(f"Connecting to {host}:{port} as {username}...")
    client.connect(hostname=host, port=port, username=username, password=password, timeout=15)
    print("Connected!")

    commands = [
        "mkdir -p ~/.ssh",
        "chmod 700 ~/.ssh",
        f"grep -qxF '{pubkey_line}' ~/.ssh/authorized_keys 2>/dev/null || echo '{pubkey_line}' >> ~/.ssh/authorized_keys",
        "chmod 600 ~/.ssh/authorized_keys",
        "echo 'SSH key pushed successfully!'",
        "cat ~/.ssh/authorized_keys | wc -l",
    ]

    for cmd in commands:
        stdin, stdout, stderr = client.exec_command(cmd)
        out = stdout.read().decode().strip()
        err = stderr.read().decode().strip()
        if out:
            print(f"  [stdout] {out}")
        if err:
            print(f"  [stderr] {err}")

    client.close()
    print("Done.")

if __name__ == "__main__":
    pubkey = read_ssh2_pubkey(PUBKEY_FILE)
    print(f"Public key (OpenSSH): {pubkey[:60]}...")
    push_key(HOST, PORT, USERNAME, PASSWORD, pubkey)
