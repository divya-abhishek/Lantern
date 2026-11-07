package com.lantern.tv.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * Screen Mirror.
 *
 * The projector side of screen mirroring is a receiver. The PC captures its own screen and this box
 * shows the live picture, so a viewer never needs to know how the stream is made or where it comes
 * from: this screen says one of exactly two things - "you are not connected yet" or "ready to
 * receive" - and the PC owns every setting.
 */
@Composable
fun MirrorScreen(
    connectedTo: String?,
    onStart: () -> Unit,
    onConnectToPc: () -> Unit,
    onBack: () -> Unit,
) {
    val connected = connectedTo != null

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp)
            .verticalScroll(rememberScrollState()),
    ) {
        ScreenTitle(
            title = "Screen Mirror",
            subtitle = "Watch your Windows screen on this device.",
        )

        Spacer(modifier = Modifier.height(24.dp))

        Box(
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(16.dp))
                .background(MirrorGradient)
                .padding(28.dp),
        ) {
            Column {
                Text(
                    text = if (connected) "Ready to receive" else "Connect to your PC first",
                    color = Color.White,
                    fontSize = 22.sp,
                    fontWeight = FontWeight.SemiBold,
                )
                Spacer(modifier = Modifier.height(8.dp))
                Text(
                    text = if (connected) {
                        "Start mirroring on your PC, then select Start receiving. Press Back to leave the stream."
                    } else {
                        "Open Lantern on your PC and connect both devices to the same home network."
                    },
                    color = Color.White.copy(alpha = 0.9f),
                    fontSize = 14.sp,
                )
            }
        }

        Spacer(modifier = Modifier.height(18.dp))

        LanternButton(
            label = if (connected) "Start receiving" else "Connect to PC",
            onClick = if (connected) onStart else onConnectToPc,
        )

        Spacer(modifier = Modifier.height(18.dp))

        LanternCard(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
            Text(
                text = "Back to Home",
                color = LanternText,
                fontSize = 17.sp,
                fontWeight = FontWeight.SemiBold,
            )
            Text(
                text = "Media Cast plays your movies and shows in their original quality.",
                color = LanternMuted,
                fontSize = 13.sp,
            )
        }
    }
}
