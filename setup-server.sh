#!/bin/bash
# setup-server.sh — Auto setup WireGuard RelayServer + Dashboard
# Usage: bash setup-server.sh

set -e

# 1. Update & install dependencies
sudo apt update
sudo apt install -y wireguard python3 python3-pip git ufw

# 2. Enable IP forwarding
sudo sed -i 's/#net.ipv4.ip_forward=1/net.ipv4.ip_forward=1/' /etc/sysctl.conf
sudo sed -i 's/#net.ipv6.conf.all.forwarding=1/net.ipv6.conf.all.forwarding=1/' /etc/sysctl.conf
sudo sysctl -p

# 3. Setup WireGuard config (if not exists)
WG_CONF="/etc/wireguard/wg0.conf"
if [ ! -f "$WG_CONF" ]; then
  echo "Creating WireGuard config..."
  umask 077
  wg genkey | tee server.key | wg pubkey > server.pub
  SERVER_PRIV=$(cat server.key)
  SERVER_PUB=$(cat server.pub)
  SERVER_IP="10.0.0.1/24"
  SERVER_PORT=51820
  cat > $WG_CONF <<EOF
[Interface]
Address = $SERVER_IP
ListenPort = $SERVER_PORT
PrivateKey = $SERVER_PRIV
EOF
  rm server.key server.pub
fi

# 4. Start WireGuard
sudo wg-quick up wg0 || true

# 5. Setup UFW firewall
sudo ufw allow 51820/udp
sudo ufw allow 8080/tcp
sudo ufw --force enable

# 6. Install Python dependencies for dashboard
cd dashboard
pip3 install -r requirements.txt

# 7. Create systemd service for dashboard
SERVICE_FILE="/etc/systemd/system/wg-dashboard.service"
if [ ! -f "$SERVICE_FILE" ]; then
  echo "[Unit]
Description=WireGuard Relay Dashboard
After=network.target

[Service]
Type=simple
WorkingDirectory=$(pwd)
ExecStart=/usr/bin/python3 -m uvicorn main:app --host 0.0.0.0 --port 8080
Restart=always

[Install]
WantedBy=multi-user.target" | sudo tee $SERVICE_FILE
  sudo systemctl daemon-reload
  sudo systemctl enable wg-dashboard
fi
sudo systemctl restart wg-dashboard

# 8. Show status
sudo systemctl status wg-dashboard --no-pager
sudo wg show

# Done
IP=$(hostname -I | awk '{print $1}')
echo "Setup complete! Dashboard: http://$IP:8080"
