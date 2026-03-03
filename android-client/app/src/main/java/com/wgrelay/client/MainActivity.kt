package com.wgrelay.client

import android.content.Intent
import android.net.VpnService
import android.os.Bundle
import android.view.View
import android.widget.Toast
import android.view.inputmethod.EditorInfo
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.wgrelay.client.databinding.ActivityMainBinding
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private val viewModel: MainViewModel by viewModels()

    private val vpnPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        if (result.resultCode == RESULT_OK) {
            viewModel.connect(vpnPermissionGranted = true)
        } else {
            Toast.makeText(this, "VPN permission denied", Toast.LENGTH_SHORT).show()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        setupClickListeners()
        observeState()
    }

    private fun setupClickListeners() {
        binding.btnRegister.setOnClickListener {
            val serverUrl = binding.etServerUrl.text.toString().trim()
            val deviceName = binding.etDeviceName.text.toString().trim()

            if (serverUrl.isEmpty() || deviceName.isEmpty()) {
                Toast.makeText(this, "Please fill in all fields", Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }

            viewModel.register(serverUrl, deviceName)
        }

        binding.btnConnect.setOnClickListener {
            requestVpnPermissionAndConnect()
        }

        binding.btnDisconnect.setOnClickListener {
            viewModel.disconnect()
        }

        binding.btnSaveDomains.setOnClickListener {
            val domains = binding.etDomains.text.toString()
            viewModel.saveDomains(domains)
            Toast.makeText(this, "Domains saved. Will apply on next connect.", Toast.LENGTH_SHORT).show()
        }

        // Save MTU when user finishes editing
        binding.etMtu.setOnFocusChangeListener { _, hasFocus ->
            if (!hasFocus) {
                val mtu = binding.etMtu.text.toString().toIntOrNull() ?: 0
                viewModel.saveMtu(mtu)
            }
        }
        binding.etMtu.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_DONE) {
                val mtu = binding.etMtu.text.toString().toIntOrNull() ?: 0
                viewModel.saveMtu(mtu)
            }
            false
        }
    }

    private fun observeState() {
        lifecycleScope.launch {
            viewModel.uiState.collect { state ->
                updateUi(state)
            }
        }
    }

    private fun updateUi(state: UiState) {
        // Status text
        binding.tvStatus.text = state.statusMessage

        // Status bar background + dot color based on state
        val (barColor, dotColor) = when (state.appState) {
            AppState.CONNECTED -> Pair(
                android.graphics.Color.parseColor("#0D2818"),
                getColor(android.R.color.holo_green_dark)
            )
            AppState.APPROVED -> Pair(
                android.graphics.Color.parseColor("#0D1A2A"),
                getColor(android.R.color.holo_blue_dark)
            )
            AppState.PENDING_APPROVAL -> Pair(
                android.graphics.Color.parseColor("#1A1A0D"),
                getColor(android.R.color.holo_orange_dark)
            )
            AppState.ERROR -> Pair(
                android.graphics.Color.parseColor("#2A0D0D"),
                getColor(android.R.color.holo_red_dark)
            )
            else -> Pair(
                android.graphics.Color.parseColor("#161B22"),
                getColor(android.R.color.darker_gray)
            )
        }
        binding.statusBar.setBackgroundColor(barColor)
        binding.viewStatusDot.setBackgroundColor(dotColor)

        // VPN IP
        binding.tvVpnIp.text = if (state.vpnIp.isNotEmpty()) "VPN IP: ${state.vpnIp}" else ""

        // Error
        if (state.errorMessage.isNotEmpty()) {
            binding.tvError.text = state.errorMessage
            binding.tvError.visibility = View.VISIBLE
        } else {
            binding.tvError.visibility = View.GONE
        }

        // Pre-fill fields
        if (binding.etServerUrl.text.isEmpty() && state.serverUrl.isNotEmpty())
            binding.etServerUrl.setText(state.serverUrl)
        if (binding.etDeviceName.text.isEmpty() && state.deviceName.isNotEmpty())
            binding.etDeviceName.setText(state.deviceName)
        if (binding.etDomains.text.isEmpty() && state.bypassDomains.isNotEmpty())
            binding.etDomains.setText(state.bypassDomains)

        // Restore MTU (only if not focused to avoid disrupting user input)
        if (!binding.etMtu.isFocused)
            binding.etMtu.setText(if (state.mtu > 0) state.mtu.toString() else "")

        // Auto-connect switch (update without re-triggering listener)
        binding.switchAutoConnect.setOnCheckedChangeListener(null)
        binding.switchAutoConnect.isChecked = state.autoConnect
        binding.switchAutoConnect.setOnCheckedChangeListener { _, isChecked ->
            viewModel.saveAutoConnect(isChecked)
        }

        // Button visibility
        binding.btnConnect.visibility = if (state.appState == AppState.APPROVED) View.VISIBLE else View.GONE
        binding.btnDisconnect.visibility = if (state.appState == AppState.CONNECTED) View.VISIBLE else View.GONE
        binding.btnRegister.isEnabled = state.appState !in listOf(AppState.REGISTERING, AppState.CONNECTED)

        // Key display
        if (state.publicKey.isNotEmpty())
            binding.tvPublicKey.text = "Public Key: ${state.publicKey.take(20)}..."
    }

    private fun requestVpnPermissionAndConnect() {
        val intent = VpnService.prepare(this)
        if (intent != null) {
            vpnPermissionLauncher.launch(intent)
        } else {
            viewModel.connect(vpnPermissionGranted = true)
        }
    }
}
