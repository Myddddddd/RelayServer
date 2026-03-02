package com.wgrelay.client

import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class RegisterResponse(val id: String, val status: String, val message: String = "")
data class PollConfig(
    val vpnIp: String,
    val serverPublicKey: String,
    val serverEndpoint: String,
    val dns: String,
    val allowedIps: String,
)
data class PollResponse(val status: String, val config: PollConfig? = null)

class ServerApiClient {

    private val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .build()

    private val json = "application/json".toMediaTypeOrNull()

    suspend fun register(serverUrl: String, name: String, publicKey: String): RegisterResponse {
        val body = JSONObject().apply {
            put("name", name)
            put("public_key", publicKey)
            put("platform", "android")
        }.toString().toRequestBody(json)

        val request = Request.Builder()
            .url("$serverUrl/api/register")
            .post(body)
            .build()

        val response = client.newCall(request).execute()
        val responseBody = response.body?.string() ?: throw Exception("Empty response")
        if (!response.isSuccessful) {
            val err = try { JSONObject(responseBody).getString("detail") } catch (e: Exception) { responseBody }
            throw Exception("Register failed: $err")
        }

        val json = JSONObject(responseBody)
        return RegisterResponse(
            id = json.getString("id"),
            status = json.getString("status"),
            message = json.optString("message", ""),
        )
    }

    suspend fun poll(serverUrl: String, peerId: String): PollResponse {
        val request = Request.Builder()
            .url("$serverUrl/api/poll/$peerId")
            .get()
            .build()

        val response = client.newCall(request).execute()
        val responseBody = response.body?.string() ?: return PollResponse("error")

        if (!response.isSuccessful) return PollResponse("error")

        val json = JSONObject(responseBody)
        val status = json.getString("status")

        if (status != "approved") return PollResponse(status)

        val cfg = json.optJSONObject("config") ?: return PollResponse(status)
        return PollResponse(
            status = status,
            config = PollConfig(
                vpnIp = cfg.getString("vpn_ip"),
                serverPublicKey = cfg.getString("server_public_key"),
                serverEndpoint = cfg.getString("server_endpoint"),
                dns = cfg.getString("dns"),
                allowedIps = cfg.getString("allowed_ips"),
            )
        )
    }

    suspend fun getServerInfo(serverUrl: String): JSONObject {
        val request = Request.Builder()
            .url("$serverUrl/api/server-info")
            .get()
            .build()
        val response = client.newCall(request).execute()
        val body = response.body?.string() ?: throw Exception("Empty response")
        if (!response.isSuccessful) throw Exception("Failed: $body")
        return JSONObject(body)
    }
}
