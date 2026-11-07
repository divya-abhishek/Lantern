package com.lantern.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lantern.tv.session.ConnectionState

/**
 * Settings: the short list of things a viewer can actually decide on this box.
 *
 * Connecting, pairing and unpairing all live on Devices, so there is exactly one place that does
 * each of them. Nothing technical is shown here: the codecs, the resolution and the device id are
 * what the PC works out for itself, and a viewer never has to read them.
 */
@Composable
fun SettingsScreen(
    connection: ConnectionState,
    onOpenDevices: () -> Unit,
    onBack: () -> Unit,
) {
    val settings: @Composable () -> Unit = {
        Column(
            modifier = Modifier.fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            ScreenTitle(title = "Settings", subtitle = "Lantern on this device.")

            LanternCard(onClick = onOpenDevices, modifier = Modifier.fillMaxWidth()) {
                Text(
                    text = "Your PC",
                    color = LanternText,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    text = when (connection) {
                        is ConnectionState.Online -> "Connected to ${connection.serverName}"
                        is ConnectionState.Pairing -> "Waiting for the code on your PC"
                        is ConnectionState.Searching -> "Searching for your PC…"
                        is ConnectionState.Failed -> "Can't find your PC"
                        ConnectionState.Idle -> "Starting…"
                    },
                    color = LanternText,
                    fontSize = 15.sp,
                )
                Text(
                    text = "Connect this device to your PC, or change which PC it uses.",
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
            }

            LanternCard(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                Text(
                    text = "Back to Home",
                    color = LanternText,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    text = "Media Cast and Screen Mirror live on Home, ready to start.",
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
            }
        }

    }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        if (maxWidth < 700.dp) {
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(24.dp)) {
                settings()
                AboutPanel()
            }
        } else {
            Row(Modifier.fillMaxSize().padding(36.dp), horizontalArrangement = Arrangement.spacedBy(24.dp)) {
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) { settings() }
                Column(Modifier.width(320.dp).verticalScroll(rememberScrollState())) { AboutPanel() }
            }
        }
    }
}
