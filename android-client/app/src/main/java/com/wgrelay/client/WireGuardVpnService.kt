package com.wgrelay.client

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.net.VpnService
import android.os.Build
import androidx.core.app.NotificationCompat
import com.wireguard.android.backend.GoBackend
import com.wireguard.android.backend.Tunnel
import com.wireguard.config.Config
import com.wireguard.config.Interface
import com.wireguard.config.InetNetwork
import com.wireguard.config.Peer
import com.wireguard.crypto.Key
import com.wireguard.crypto.KeyPair
import java.net.InetAddress

class WireGuardVpnService : VpnService() {

    companion object {
        const val ACTION_CONNECT = "com.wgrelay.client.CONNECT"
        const val ACTION_DISCONNECT = "com.wgrelay.client.DISCONNECT"
        const val EXTRA_VPN_IP = "vpn_ip"
        const val EXTRA_PRIVATE_KEY = "private_key"
        const val EXTRA_SERVER_PUBKEY = "server_pubkey"
        const val EXTRA_SERVER_ENDPOINT = "server_endpoint"
        const val EXTRA_VPN_SUBNET = "vpn_subnet"
        const val EXTRA_BYPASS_DOMAINS = "bypass_domains"
        const val NOTIFICATION_ID = 1001
        const val CHANNEL_ID = "wgrelay_vpn"
        var isRunning = false
    }

    private var backend: GoBackend? = null
    private var activeTunnel: WgTunnel? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_CONNECT -> {
                val vpnIp = intent.getStringExtra(EXTRA_VPN_IP) ?: return START_NOT_STICKY
                val privateKey = intent.getStringExtra(EXTRA_PRIVATE_KEY) ?: return START_NOT_STICKY
                val serverPubKey = intent.getStringExtra(EXTRA_SERVER_PUBKEY) ?: return START_NOT_STICKY
                val serverEndpoint = intent.getStringExtra(EXTRA_SERVER_ENDPOINT) ?: return START_NOT_STICKY
                val vpnSubnet = intent.getStringExtra(EXTRA_VPN_SUBNET) ?: "10.0.0.0/24"
                val bypassDomains = intent.getStringExtra(EXTRA_BYPASS_DOMAINS) ?: ""

                startForeground(NOTIFICATION_ID, createNotification("Connecting..."))
                Thread { connect(vpnIp, privateKey, serverPubKey, serverEndpoint, vpnSubnet, bypassDomains) }.start()
            }
            ACTION_DISCONNECT -> {
                disconnect()
                stopSelf()
            }
        }
        return START_STICKY
    }

    private fun connect(
        vpnIp: String,
        privateKey: String,
        serverPubKey: String,
        serverEndpoint: String,
        vpnSubnet: String,
        bypassDomains: String,
    ) {
        try {
            val privKey = Key.fromBase64(privateKey)
            val peerPubKey = Key.fromBase64(serverPubKey)

            // Build WireGuard config
            val ifaceBuilder = Interface.Builder().apply {
                setKeyPair(KeyPair(privKey))
                addAddress(InetNetwork.parse("$vpnIp/24"))
                // NOTE: No DNS override — keep system DNS to avoid breaking internet
            }

            // Determine AllowedIPs based on domain routing
            val allowedIpsList = if (bypassDomains.isBlank()) {
                // Default: route only VPN subnet (split-tunnel, safe)
                mutableListOf(InetNetwork.parse(vpnSubnet))
            } else {
                // Only route VPN subnet + resolve domain IPs
                val ips = mutableListOf(InetNetwork.parse(vpnSubnet))
                bypassDomains.lines().filter { it.isNotBlank() }.forEach { domain ->
                    try {
                        InetAddress.getAllByName(domain.trim()).forEach { addr ->
                            if (addr is java.net.Inet4Address)
                                ips.add(InetNetwork.parse("${addr.hostAddress}/32"))
                        }
                    } catch (_: Exception) {}
                }
                ips
            }

            // Parse server endpoint
            val epParts = serverEndpoint.split(":")
            val epHost = epParts[0]
            val epPort = epParts.getOrNull(1)?.toIntOrNull() ?: 51820

            val peerBuilder = Peer.Builder().apply {
                setPublicKey(peerPubKey)
                setEndpoint(com.wireguard.config.InetEndpoint.parse(serverEndpoint))
                addAllowedIps(allowedIpsList)
                setPersistentKeepalive(25)
            }

            val config = Config.Builder()
                .setInterface(ifaceBuilder.build())
                .addPeer(peerBuilder.build())
                .build()

            val tunnel = WgTunnel("wgrelay")
            if (backend == null) backend = GoBackend(this)

            backend!!.setState(tunnel, Tunnel.State.UP, config)
            activeTunnel = tunnel
            isRunning = true

            updateNotification("Connected • $vpnIp")
        } catch (e: Exception) {
            isRunning = false
            updateNotification("Connection failed: ${e.message}")
        }
    }

    private fun disconnect() {
        try {
            activeTunnel?.let { backend?.setState(it, Tunnel.State.DOWN, null) }
        } catch (_: Exception) {}
        isRunning = false
        activeTunnel = null
    }

    override fun onDestroy() {
        disconnect()
        super.onDestroy()
    }

    private fun createNotificationChannel() {
        val channel = NotificationChannel(CHANNEL_ID, "WireGuard VPN", NotificationManager.IMPORTANCE_LOW)
        channel.description = "WireGuard Relay VPN tunnel"
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    private fun createNotification(text: String): Notification {
        val openIntent = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("WireGuard Relay")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_lock_lock)
            .setContentIntent(openIntent)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification(text: String) {
        val nm = getSystemService(NotificationManager::class.java)
        nm.notify(NOTIFICATION_ID, createNotification(text))
    }
}

class WgTunnel(private val name: String) : Tunnel {
    override fun getName() = name
    override fun onStateChange(newState: Tunnel.State) {}
}
