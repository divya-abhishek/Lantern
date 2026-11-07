package com.lantern.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lantern.tv.data.DiscoveredServer
import com.lantern.tv.session.ConnectionState

/**
 * Devices: the consumer view of "which PC is this projector connected to".
 *
 * Nothing here is a network term. The screen shows the PC when it has been found, says so plainly
 * when it has not, and keeps the address box hidden until the viewer actually asks for it: an
 * address, a port or a device id is something the app works out on its own.
 */
@Composable
fun DevicesScreen(
    connection: ConnectionState,
    discovered: List<DiscoveredServer>,
    pairedName: String?,
    onReconnect: () -> Unit,
    onPair: (DiscoveredServer) -> Unit,
    onConnectAddress: (String) -> Unit,
    onForget: () -> Unit,
    onBack: () -> Unit,
) {
    // The manual address field only exists after the viewer asks for it.
    var manual by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp)
            .verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        ScreenTitle(
            title = "Devices",
            subtitle = "Connect this receiver to your Lantern PC.",
        )

        // --- 1. where this projector stands right now ------------------------------------
        LanternPanel {
            Row(verticalAlignment = Alignment.CenterVertically) {
                StatusDot(connected = connection is ConnectionState.Online)
                Spacer(modifier = Modifier.width(10.dp))
                Text(
                    text = when (connection) {
                        is ConnectionState.Online -> "Connected to ${connection.serverName}"
                        is ConnectionState.Pairing -> "Waiting for the code on your PC"
                        is ConnectionState.Searching -> "Searching for your PC…"
                        is ConnectionState.Failed -> "Can't find your PC"
                        ConnectionState.Idle -> "Starting…"
                    },
                    color = LanternText,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.SemiBold,
                )
            }

            Text(
                text = when (connection) {
                    is ConnectionState.Online ->
                        "Movies, shows and screen mirroring are ready."
                    is ConnectionState.Pairing ->
                        "Open Lantern on your PC and enter the 4-digit code shown on this screen."
                    is ConnectionState.Failed -> connection.message
                    else ->
                        "Make sure Lantern is running on your PC and that both are on the same Wi-Fi."
                },
                color = LanternMuted,
                fontSize = 13.sp,
            )

        }

        // --- 2. what has been found ------------------------------------------------------
        LanternPanel {
            Text(
                text = if (discovered.isEmpty()) "Searching for your PC…" else "Detected",
                color = LanternText,
                fontSize = 17.sp,
                fontWeight = FontWeight.SemiBold,
            )

            if (discovered.isEmpty()) {
                Text(
                    text = "Keep Lantern open on your PC. This screen updates by itself as soon as " +
                        "the PC answers.",
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
                LanternButton(label = "Try Again", onClick = onReconnect, primary = false)
            } else {
                discovered.forEach { server ->
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(text = server.name, color = LanternText, fontSize = 17.sp)
                            Text(
                                text = "Lantern PC found on this network",
                                color = LanternMuted,
                                fontSize = 13.sp,
                            )
                        }

                        LanternButton(label = "Connect", onClick = { onPair(server) })
                    }
                }
            }
        }

        // --- 3. the fallback, revealed only when the viewer asks -------------------------
        LanternPanel {
            Text(
                text = "Can't find your PC?",
                color = LanternText,
                fontSize = 17.sp,
                fontWeight = FontWeight.SemiBold,
            )

            if (manual) {
                Text(
                    text = "Type the address shown in Lantern on your PC, for example " +
                        "192.168.1.72:8787.",
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
                AddressField(onSubmit = onConnectAddress, buttonLabel = "Connect")
            } else {
                Text(
                    text = "On the same network but still missing? Enter the address shown in Lantern on your PC.",
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
                LanternButton(label = "Enter address", onClick = { manual = true }, primary = false)
            }
        }

        // --- 4. disconnect ---------------------------------------------------------------
        if (pairedName != null) {
            LanternPanel {
                Text(
                    text = "Disconnect",
                    color = LanternText,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    text = "$pairedName will be forgotten, and the next connection asks for a new " +
                        "4-digit code.",
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
                LanternButton(label = "Forget PC", onClick = onForget, primary = false)
            }
        }

        LanternButton(label = "Back to Home", onClick = onBack, primary = false)
    }
}
