package com.lantern.tv.ui

import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lantern.tv.session.ConnectionState

/**
 * Home: the two things this box can do, and who it is connected to. Everything is reachable with
 * the four D-pad arrows and the OK button.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun HomeScreen(
    connection: ConnectionState,
    pcFound: Boolean,
    folderCount: Int,
    totalItems: Int,
    onOpenLibrary: () -> Unit,
    onOpenMirror: () -> Unit,
    onOpenDevices: () -> Unit,
    onOpenSettings: () -> Unit,
) {
    val compact = LocalConfiguration.current.screenWidthDp < 600
    val cardWidth = ((LocalConfiguration.current.screenWidthDp - 96) / 2).dp
    val cardModifier = if (compact) Modifier.fillMaxWidth() else Modifier.width(cardWidth)
    Column(modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(if (compact) 16.dp else 36.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            // The official Lantern mark, on the same square master the app icon is drawn from.
            LanternLogo(modifier = Modifier.size(if (compact) 52.dp else 72.dp))
            Spacer(modifier = Modifier.width(16.dp))
            Column {
                Text(text = "Lantern", color = LanternText, fontSize = 30.sp, fontWeight = FontWeight.SemiBold)
                Text(text = "Local Cast & Screen Mirror", color = LanternMuted, fontSize = 14.sp)
            }
        }

        Spacer(modifier = Modifier.height(if (compact) 14.dp else 22.dp))
        ConnectionBanner(
            connection = connection,
            compact = compact,
            pcFound = pcFound,
            folderCount = folderCount,
            totalItems = totalItems,
            onConnect = onOpenDevices,
        )

        Spacer(modifier = Modifier.height(if (compact) 16.dp else 24.dp))

        FlowRow(horizontalArrangement = Arrangement.spacedBy(24.dp), verticalArrangement = Arrangement.spacedBy(16.dp), maxItemsInEachRow = if (compact) 1 else 2, modifier = Modifier.fillMaxWidth()) {
            LanternCard(onClick = onOpenLibrary, background = AccentGradient, modifier = cardModifier) {
                Text(text = "Media Library", color = Color.White, fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
                Text(text = "Watch videos from your PC", color = Color.White.copy(alpha = 0.9f), fontSize = 15.sp)
            }

            LanternCard(onClick = onOpenMirror, background = MirrorGradient, modifier = cardModifier) {
                Text(text = "Screen Mirror", color = Color.White, fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
                Text(text = "Watch your Windows screen live", color = Color.White.copy(alpha = 0.9f), fontSize = 15.sp)
            }
        }

        Spacer(modifier = Modifier.height(20.dp))

        FlowRow(horizontalArrangement = Arrangement.spacedBy(24.dp), verticalArrangement = Arrangement.spacedBy(16.dp), maxItemsInEachRow = if (compact) 1 else 2, modifier = Modifier.fillMaxWidth()) {
            LanternCard(onClick = onOpenDevices, modifier = cardModifier) {
                Text(
                    text = "Devices & Connection",
                    color = LanternText,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    text = when (connection) {
                        is ConnectionState.Online -> "Connected to ${connection.serverName}"
                        else -> "Pair a PC, reconnect, or forget one."
                    },
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
            }

            LanternCard(onClick = onOpenSettings, modifier = cardModifier) {
                Text(
                    text = "Settings",
                    color = LanternText,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    text = "Playback and app preferences",
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
            }
        }
    }
}

@Composable
private fun ConnectionBanner(
    connection: ConnectionState,
    compact: Boolean,
    pcFound: Boolean,
    folderCount: Int,
    totalItems: Int,
    onConnect: () -> Unit,
) {
    val connected = connection is ConnectionState.Online

    LanternPanel {
        Row(verticalAlignment = Alignment.CenterVertically) {
            StatusDot(connected = connected)
            Spacer(modifier = Modifier.width(14.dp))

            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = when (connection) {
                        is ConnectionState.Online -> "Connected to ${connection.serverName}"
                        is ConnectionState.Searching ->
                            if (pcFound) "Lantern PC found" else "Looking for your PC…"
                        is ConnectionState.Pairing -> "Waiting for the code on your PC"
                        is ConnectionState.Failed -> "Connect to your PC"
                        ConnectionState.Idle ->
                            if (pcFound) "Lantern PC found" else "Connect to your PC"
                    },
                    color = LanternText,
                    fontSize = 16.sp,
                    fontWeight = FontWeight.SemiBold,
                )

                Text(
                    text = when (connection) {
                        is ConnectionState.Online ->
                            if (folderCount > 0) {
                                "$totalItems title(s) ready to play from $folderCount folder(s)."
                            } else {
                                "No folders have been added in Lantern on your PC yet."
                            }
                        is ConnectionState.Failed -> "Open Lantern on your PC and use the same home network."
                        is ConnectionState.Pairing -> "Enter the 4-digit code shown here on your PC."
                        else ->
                            "Open Lantern on your PC and keep both devices on the same Wi-Fi."
                    },
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
            }

            if (!connected && !compact) {
                Spacer(modifier = Modifier.width(14.dp))
                LanternButton(
                    label = if (pcFound) "Connect" else "Connect to PC",
                    onClick = onConnect,
                )
            }
        }
        if (!connected && compact) {
            LanternButton(label = "Connect to PC", onClick = onConnect)
        }
    }
}

/**
 * The pairing screen. The PIN comes from the PC (it is the id of the pairing request this device
 * just made) and is typed there, so the projector remote never has to type anything.
 */
@Composable
fun PairScreen(pairing: ConnectionState.Pairing, onCancel: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(modifier = Modifier.height(14.dp))
        Text(text = "PAIR WITH YOUR PC", color = LanternText, fontSize = 26.sp, fontWeight = FontWeight.SemiBold)
        Spacer(modifier = Modifier.height(8.dp))
        Text(
            text = "Open Lantern on the Windows PC, choose Devices, and enter this code:",
            color = LanternMuted,
            fontSize = 15.sp,
            textAlign = TextAlign.Center,
        )

        Spacer(modifier = Modifier.height(24.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            pairing.pin.take(4).forEach { digit ->
                Box(
                    modifier = Modifier
                        .size(width = ((LocalConfiguration.current.screenWidthDp - 90) / 4).coerceAtMost(108).dp, height = 100.dp)
                        .clip(RoundedCornerShape(16.dp))
                        .background(LanternSurfaceAlt)
                        .border(2.dp, LanternAccent, RoundedCornerShape(16.dp)),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = digit.toString(),
                        color = Color.White,
                        fontSize = 48.sp,
                        fontWeight = FontWeight.Bold,
                    )
                }
            }
        }

        Spacer(modifier = Modifier.height(20.dp))

        Text(
            text = "This code expires in ${pairing.secondsLeft.coerceAtLeast(0)} seconds",
            color = LanternMuted,
            fontSize = 15.sp,
        )

        Spacer(modifier = Modifier.height(10.dp))

        LinearProgressIndicator(
            progress = { pairing.secondsLeft.coerceIn(0, 120) / 120f },
            modifier = Modifier.width(440.dp),
            color = LanternAccentBright,
            trackColor = LanternSurfaceAlt,
        )

        Spacer(modifier = Modifier.height(20.dp))

        Row(verticalAlignment = Alignment.CenterVertically) {
            CircularProgressIndicator(
                modifier = Modifier.size(20.dp),
                color = LanternAccentBright,
                strokeWidth = 2.dp,
            )
            Spacer(modifier = Modifier.width(12.dp))
            Text(
                text = pairing.message ?: "Waiting for the PC to enter the code…",
                color = LanternMuted,
                fontSize = 14.sp,
            )
        }

        if (pairing.attempt > 1) {
            Spacer(modifier = Modifier.height(6.dp))
            Text(text = "New code · attempt ${pairing.attempt}", color = LanternMuted, fontSize = 12.sp)
        }

        Spacer(modifier = Modifier.height(24.dp))

        LanternButton(label = "Cancel and go back", onClick = onCancel, primary = false)
    }
}
