package com.wgrelay.client

import android.app.Application
import android.content.Intent
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.wireguard.crypto.KeyPair
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

enum class AppState {
    IDLE, REGISTERING, PENDING_APPROVAL, APPROVED, CONNECTED, ERROR
}

data class UiState(
    val appState: AppState = AppState.IDLE,
    val statusMessage: String = "Not configured",
    val vpnIp: String = "",
    val publicKey: String = "",
    val serverUrl: String = "",
    val deviceName: String = android.os.Build.MODEL,
    val bypassDomains: String = "",
    val errorMessage: String = "",
    val autoConnect: Boolean = false,
)

class MainViewModel(application: Application) : AndroidViewModel(application) {

    private val context = application.applicationContext
    private val configStore = ConfigStore(context)
    private val apiClient = ServerApiClient()

    private val _uiState = MutableStateFlow(UiState())
    val uiState: StateFlow<UiState> = _uiState.asStateFlow()

    private var pollingJob: kotlinx.coroutines.Job? = null

    init {
        loadInitialState()
    }

    private fun loadInitialState() = viewModelScope.launch {
        val config = configStore.configFlow.first()
        val isConnected = WireGuardVpnService.isRunning

        val state = when {
            isConnected -> AppState.CONNECTED
            config.approvalStatus == "approved" -> AppState.APPROVED
            config.approvalStatus == "pending" -> {
                startPolling()
                AppState.PENDING_APPROVAL
            }
            else -> AppState.IDLE
        }

        _uiState.value = UiState(
            appState = state,
            vpnIp = config.vpnIp,
            publicKey = config.publicKey,
            serverUrl = config.serverUrl,
            deviceName = config.deviceName,
            bypassDomains = config.bypassDomains,
            autoConnect = config.autoConnect,
            statusMessage = stateMessage(state, config.vpnIp),
        )

        // Auto-connect if approved and setting enabled
        if (state == AppState.APPROVED && config.autoConnect && !isConnected) {
            connect(vpnPermissionGranted = true)
        }
    }

    fun register(serverUrl: String, deviceName: String) = viewModelScope.launch {
        _uiState.value = _uiState.value.copy(appState = AppState.REGISTERING, statusMessage = "Registering...")

        try {
            var config = configStore.configFlow.first()
            config = config.copy(serverUrl = serverUrl.trimEnd('/'), deviceName = deviceName)

            // Generate keys if needed
            if (config.privateKey.isEmpty()) {
                val keyPair = KeyPair()
                config = config.copy(
                    privateKey = keyPair.privateKey.toBase64(),
                    publicKey = keyPair.publicKey.toBase64(),
                )
            }
            configStore.save(config)

            val result = apiClient.register(config.serverUrl, config.deviceName, config.publicKey)
            config = config.copy(peerId = result.id, approvalStatus = result.status)
            configStore.save(config)

            val newState = if (result.status == "approved") AppState.APPROVED else AppState.PENDING_APPROVAL
            _uiState.value = _uiState.value.copy(
                appState = newState,
                publicKey = config.publicKey,
                serverUrl = config.serverUrl,
                deviceName = config.deviceName,
                statusMessage = stateMessage(newState, config.vpnIp),
                errorMessage = "",
            )

            if (newState == AppState.PENDING_APPROVAL) startPolling()

        } catch (e: Exception) {
            _uiState.value = _uiState.value.copy(
                appState = AppState.ERROR,
                statusMessage = "Registration failed",
                errorMessage = e.message ?: "Unknown error",
            )
        }
    }

    private fun startPolling() {
        pollingJob?.cancel()
        pollingJob = viewModelScope.launch {
            while (isActive) {
                delay(5_000)
                checkApproval()
            }
        }
    }

    private suspend fun checkApproval() {
        val config = configStore.configFlow.first()
        if (config.peerId.isEmpty() || config.serverUrl.isEmpty()) return

        try {
            val poll = apiClient.poll(config.serverUrl, config.peerId)
            if (poll.status == "approved" && poll.config != null) {
                val updated = config.copy(
                    approvalStatus = "approved",
                    vpnIp = poll.config.vpnIp,
                    serverPublicKey = poll.config.serverPublicKey,
                    serverEndpoint = poll.config.serverEndpoint,
                    vpnSubnet = poll.config.allowedIps,
                )
                configStore.save(updated)
                pollingJob?.cancel()
                _uiState.value = _uiState.value.copy(
                    appState = AppState.APPROVED,
                    vpnIp = poll.config.vpnIp,
                    statusMessage = "Approved! VPN IP: ${poll.config.vpnIp}",
                )
                // Auto-connect after approval if enabled
                if (updated.autoConnect) {
                    connect(vpnPermissionGranted = true)
                }
            } else if (poll.status == "rejected") {
                val updated = config.copy(approvalStatus = "rejected")
                configStore.save(updated)
                pollingJob?.cancel()
                _uiState.value = _uiState.value.copy(
                    appState = AppState.ERROR,
                    statusMessage = "Rejected by admin",
                    errorMessage = "Your device registration was rejected.",
                )
            }
        } catch (_: Exception) {}
    }

    fun connect(vpnPermissionGranted: Boolean = true) = viewModelScope.launch {
        if (!vpnPermissionGranted) return@launch

        val config = configStore.configFlow.first()
        if (config.approvalStatus != "approved") {
            _uiState.value = _uiState.value.copy(errorMessage = "Not approved yet")
            return@launch
        }

        val intent = Intent(context, WireGuardVpnService::class.java).apply {
            action = WireGuardVpnService.ACTION_CONNECT
            putExtra(WireGuardVpnService.EXTRA_VPN_IP, config.vpnIp)
            putExtra(WireGuardVpnService.EXTRA_PRIVATE_KEY, config.privateKey)
            putExtra(WireGuardVpnService.EXTRA_SERVER_PUBKEY, config.serverPublicKey)
            putExtra(WireGuardVpnService.EXTRA_SERVER_ENDPOINT, config.serverEndpoint)
            putExtra(WireGuardVpnService.EXTRA_VPN_SUBNET, config.vpnSubnet)
            putExtra(WireGuardVpnService.EXTRA_BYPASS_DOMAINS, config.bypassDomains)
        }
        context.startService(intent)

        _uiState.value = _uiState.value.copy(
            appState = AppState.CONNECTED,
            statusMessage = "Connected • ${config.vpnIp}",
        )
    }

    fun disconnect() = viewModelScope.launch {
        val intent = Intent(context, WireGuardVpnService::class.java).apply {
            action = WireGuardVpnService.ACTION_DISCONNECT
        }
        context.startService(intent)

        val config = configStore.configFlow.first()
        _uiState.value = _uiState.value.copy(
            appState = AppState.APPROVED,
            statusMessage = "Disconnected. VPN IP: ${config.vpnIp}",
        )
    }

    fun saveDomains(domains: String) = viewModelScope.launch {
        val config = configStore.configFlow.first()
        configStore.save(config.copy(bypassDomains = domains))
        _uiState.value = _uiState.value.copy(bypassDomains = domains)
    }

    fun saveAutoConnect(enabled: Boolean) = viewModelScope.launch {
        val config = configStore.configFlow.first()
        configStore.save(config.copy(autoConnect = enabled))
        _uiState.value = _uiState.value.copy(autoConnect = enabled)
    }

    private fun stateMessage(state: AppState, vpnIp: String): String = when (state) {
        AppState.IDLE -> "Enter server URL to register"
        AppState.REGISTERING -> "Registering..."
        AppState.PENDING_APPROVAL -> "Waiting for admin approval..."
        AppState.APPROVED -> "Approved • VPN IP: $vpnIp"
        AppState.CONNECTED -> "Connected • $vpnIp"
        AppState.ERROR -> "Error"
    }
}
