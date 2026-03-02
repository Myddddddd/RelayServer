package com.wgrelay.client

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "wg_config")

data class AppConfig(
    val serverUrl: String = "",
    val deviceName: String = android.os.Build.MODEL,
    val privateKey: String = "",
    val publicKey: String = "",
    val peerId: String = "",
    val approvalStatus: String = "",  // "pending", "approved", "rejected"
    val vpnIp: String = "",
    val serverPublicKey: String = "",
    val serverEndpoint: String = "",
    val vpnSubnet: String = "10.0.0.0/24",
    val bypassDomains: String = "",  // newline-separated
    val autoConnect: Boolean = false,
)

class ConfigStore(private val context: Context) {

    private object Keys {
        val SERVER_URL = stringPreferencesKey("server_url")
        val DEVICE_NAME = stringPreferencesKey("device_name")
        val PRIVATE_KEY = stringPreferencesKey("private_key")
        val PUBLIC_KEY = stringPreferencesKey("public_key")
        val PEER_ID = stringPreferencesKey("peer_id")
        val APPROVAL_STATUS = stringPreferencesKey("approval_status")
        val VPN_IP = stringPreferencesKey("vpn_ip")
        val SERVER_PUBKEY = stringPreferencesKey("server_pubkey")
        val SERVER_ENDPOINT = stringPreferencesKey("server_endpoint")
        val VPN_SUBNET = stringPreferencesKey("vpn_subnet")
        val BYPASS_DOMAINS = stringPreferencesKey("bypass_domains")
        val AUTO_CONNECT = booleanPreferencesKey("auto_connect")
    }

    val configFlow: Flow<AppConfig> = context.dataStore.data.map { prefs ->
        AppConfig(
            serverUrl = prefs[Keys.SERVER_URL] ?: "",
            deviceName = prefs[Keys.DEVICE_NAME] ?: android.os.Build.MODEL,
            privateKey = prefs[Keys.PRIVATE_KEY] ?: "",
            publicKey = prefs[Keys.PUBLIC_KEY] ?: "",
            peerId = prefs[Keys.PEER_ID] ?: "",
            approvalStatus = prefs[Keys.APPROVAL_STATUS] ?: "",
            vpnIp = prefs[Keys.VPN_IP] ?: "",
            serverPublicKey = prefs[Keys.SERVER_PUBKEY] ?: "",
            serverEndpoint = prefs[Keys.SERVER_ENDPOINT] ?: "",
            vpnSubnet = prefs[Keys.VPN_SUBNET] ?: "10.0.0.0/24",
            bypassDomains = prefs[Keys.BYPASS_DOMAINS] ?: "",
            autoConnect = prefs[Keys.AUTO_CONNECT] ?: false,
        )
    }

    suspend fun save(config: AppConfig) {
        context.dataStore.edit { prefs ->
            prefs[Keys.SERVER_URL] = config.serverUrl
            prefs[Keys.DEVICE_NAME] = config.deviceName
            prefs[Keys.PRIVATE_KEY] = config.privateKey
            prefs[Keys.PUBLIC_KEY] = config.publicKey
            prefs[Keys.PEER_ID] = config.peerId
            prefs[Keys.APPROVAL_STATUS] = config.approvalStatus
            prefs[Keys.VPN_IP] = config.vpnIp
            prefs[Keys.SERVER_PUBKEY] = config.serverPublicKey
            prefs[Keys.SERVER_ENDPOINT] = config.serverEndpoint
            prefs[Keys.VPN_SUBNET] = config.vpnSubnet
            prefs[Keys.BYPASS_DOMAINS] = config.bypassDomains
            prefs[Keys.AUTO_CONNECT] = config.autoConnect
        }
    }
}
