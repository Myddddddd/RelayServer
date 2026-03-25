#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$SCRIPT_DIR"
TARGET_DIR="/opt/wg-dashboard"
VENV_DIR="$TARGET_DIR/venv"
SERVICE_FILE_SRC="$REPO_DIR/dashboard/wg-dashboard.service"
SERVICE_FILE_DST="/etc/systemd/system/wg-dashboard.service"
WG_CONF="/etc/wireguard/wg0.conf"
SERVER_PUBKEY_FILE="/etc/wireguard/server_public.key"
REPO_PATH_FILE="$TARGET_DIR/.repo-path"

require_root() {
  if [ "${EUID}" -ne 0 ]; then
    echo "Please run this script as root: sudo bash update-server.sh" >&2
    exit 1
  fi
}

ensure_packages() {
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -y
  apt-get install -y wireguard wireguard-tools python3 python3-venv python3-pip rsync
}

ensure_server_public_key() {
  mkdir -p /etc/wireguard
  chmod 700 /etc/wireguard

  if [ -s "$SERVER_PUBKEY_FILE" ]; then
    echo "server_public.key already present"
    return
  fi

  if [ -f "$WG_CONF" ]; then
    echo "Recovering server_public.key from wg0.conf..."
    local private_key
    private_key="$(awk -F'=' '/^[[:space:]]*PrivateKey[[:space:]]*=/{print $2; exit}' "$WG_CONF" | xargs)"
    if [ -n "$private_key" ]; then
      printf "%s" "$private_key" | wg pubkey > "$SERVER_PUBKEY_FILE"
      chmod 600 "$SERVER_PUBKEY_FILE"
      return
    fi
  fi

  echo "Unable to recover server public key automatically." >&2
  exit 1
}

sync_dashboard_files() {
  mkdir -p "$TARGET_DIR"
  rsync -av --delete \
    --exclude '__pycache__' \
    --exclude '.pytest_cache' \
    "$REPO_DIR/dashboard/" "$TARGET_DIR/"
  printf "%s\n" "$REPO_DIR" > "$REPO_PATH_FILE"
}

ensure_virtualenv() {
  if [ ! -d "$VENV_DIR" ]; then
    python3 -m venv "$VENV_DIR"
  fi

  "$VENV_DIR/bin/pip" install --upgrade pip
  "$VENV_DIR/bin/pip" install -r "$TARGET_DIR/requirements.txt"
}

install_service() {
  cp "$SERVICE_FILE_SRC" "$SERVICE_FILE_DST"
  systemctl daemon-reload
  systemctl enable wg-dashboard >/dev/null 2>&1 || true
}

restart_services() {
  systemctl enable wg-quick@wg0 >/dev/null 2>&1 || true
  systemctl restart wg-quick@wg0
  systemctl restart wg-dashboard
}

print_summary() {
  echo
  echo "Server update completed."
  echo "Dashboard status:"
  systemctl --no-pager --full status wg-dashboard | sed -n '1,12p'
  echo
  echo "API server info:"
  python3 - <<'PY'
import json
import urllib.request

try:
    with urllib.request.urlopen('http://127.0.0.1:8080/api/server-info', timeout=5) as response:
        print(json.dumps(json.load(response), indent=2))
except Exception as exc:
    print(f'Failed to query local dashboard: {exc}')
PY
}

require_root
ensure_packages
ensure_server_public_key
sync_dashboard_files
ensure_virtualenv
install_service
restart_services
print_summary