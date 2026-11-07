package com.lantern.tv.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** Address entry for the manual-connect fallback. */
@Composable
fun AddressField(onSubmit: (String) -> Unit, buttonLabel: String = "Connect") {
    var text by remember { mutableStateOf("") }

    Column(modifier = Modifier.fillMaxWidth()) {
        BasicTextField(
            value = text,
            onValueChange = { text = it },
            singleLine = true,
            textStyle = TextStyle(color = LanternText, fontSize = 16.sp),
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(10.dp))
                .background(LanternSurfaceAlt)
                .border(1.dp, LanternBorder, RoundedCornerShape(10.dp))
                .padding(horizontal = 14.dp, vertical = 12.dp),
        )

        Spacer(modifier = Modifier.height(10.dp))

        LanternButton(
            label = buttonLabel,
            onClick = { if (text.isNotBlank()) onSubmit(text) },
            primary = false,
        )
    }
}

@Composable
fun AboutPanel() {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(16.dp))
            .background(LanternSurface)
            .padding(20.dp),
    ) {
        Text(text = "About", color = LanternText, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        Spacer(modifier = Modifier.height(12.dp))

        Row(verticalAlignment = Alignment.CenterVertically) {
            // The one official Lantern mark, beam and all: the same drawing the Windows app shows.
            LanternLogo(modifier = Modifier.width(90.dp).height(67.dp))
            Spacer(modifier = Modifier.width(14.dp))
            Column {
                Text(text = "Lantern", color = LanternText, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
                Text(text = "Local Cast & Screen Mirror", color = LanternMuted, fontSize = 12.sp)
            }
        }

        Spacer(modifier = Modifier.height(10.dp))
        Text(text = "Movies. Shows. Your screen. No HDMI. Just Wi-Fi.", color = LanternMuted, fontSize = 13.sp)
    }
}
