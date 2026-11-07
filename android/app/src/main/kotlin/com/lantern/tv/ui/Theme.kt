package com.lantern.tv.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lantern.tv.R

/**
 * The Lantern palette, shared with the Windows app so both look like one family: dark cinematic
 * surfaces, blue and purple accents, a single green "connected" light.
 */
val LanternBg = Color(0xFF06090F)
val LanternSurface = Color(0xFF0F1523)
val LanternSurfaceAlt = Color(0xFF161D30)
val LanternBorder = Color(0xFF22303F55)
val LanternAccent = Color(0xFF3378F5)
val LanternAccentBright = Color(0xFF5BB6FF)
val LanternText = Color(0xFFE7EDFF)
val LanternMuted = Color(0xFF8C97B2)
val LanternSuccess = Color(0xFF35D07F)

private val FlatSurface = Brush.linearGradient(listOf(LanternSurface, LanternSurface))
private val FlatSurfaceAlt = Brush.linearGradient(listOf(LanternSurfaceAlt, LanternSurfaceAlt))
val AccentGradient = Brush.linearGradient(listOf(Color(0xFF7B5BFF), Color(0xFF2C55E9)))
val MirrorGradient = Brush.linearGradient(listOf(Color(0xFF1D3BC7), Color(0xFF29266B)))

@Composable
fun LanternTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = MaterialTheme.colorScheme.copy(
            primary = LanternAccent,
            background = LanternBg,
            surface = LanternSurface,
            onPrimary = Color.White,
            onBackground = LanternText,
            onSurface = LanternText,
        ),
        content = content,
    )
}

/** The one true "connected" light. */
@Composable
fun StatusDot(connected: Boolean, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .size(10.dp)
            .clip(CircleShape)
            .background(if (connected) LanternSuccess else LanternMuted),
    )
}

/** A card that shows focus the way a TV viewer expects: a bright ring, no shadow tricks. */
@Composable
fun LanternCard(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    background: Brush = FlatSurface,
    content: @Composable ColumnScope.() -> Unit,
) {
    var focused by remember { mutableStateOf(false) }

    Column(
        modifier = modifier
            .onFocusChanged { focused = it.isFocused }
            .clickable(onClick = onClick)
            .clip(RoundedCornerShape(14.dp))
            .background(background)
            .border(
                width = if (focused) 3.dp else 1.dp,
                color = if (focused) LanternAccentBright else LanternBorder,
                shape = RoundedCornerShape(14.dp),
            )
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
        content = content,
    )
}

/** A card that is not itself a button: for cards that hold their own controls, so pressing OK on
 *  the remote can never trigger the card and the button inside it at the same time. */
@Composable
fun LanternPanel(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(LanternSurface)
            .border(1.dp, LanternBorder, RoundedCornerShape(14.dp))
            .padding(20.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
        content = content,
    )
}

/** A pill-shaped selectable tab (Movies / TV Shows / Folders). */
@Composable
fun TabPill(label: String, selected: Boolean, onClick: () -> Unit) {
    var focused by remember { mutableStateOf(false) }

    Box(
        modifier = Modifier
            .onFocusChanged { focused = it.isFocused }
            .clickable(onClick = onClick)
            .clip(RoundedCornerShape(20.dp))
            .background(if (selected) LanternAccent else LanternSurfaceAlt)
            .border(
                width = if (focused) 2.dp else 1.dp,
                color = if (focused) LanternAccentBright else LanternBorder,
                shape = RoundedCornerShape(20.dp),
            )
            .padding(horizontal = 18.dp, vertical = 9.dp),
    ) {
        Text(
            text = label,
            color = if (selected) Color.White else LanternMuted,
            fontSize = 15.sp,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
        )
    }
}

/** A TV-friendly action button. */
@Composable
fun LanternButton(
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    primary: Boolean = true,
) {
    var focused by remember { mutableStateOf(false) }

    Box(
        modifier = modifier
            .onFocusChanged { focused = it.isFocused }
            .clickable(onClick = onClick)
            .clip(RoundedCornerShape(12.dp))
            .background(if (primary) AccentGradient else FlatSurfaceAlt)
            .border(
                width = if (focused) 3.dp else 1.dp,
                color = if (focused) LanternAccentBright else LanternBorder,
                shape = RoundedCornerShape(12.dp),
            )
            .padding(horizontal = 24.dp, vertical = 13.dp),
    ) {
        Text(
            text = label,
            color = if (primary) Color.White else LanternText,
            fontSize = 16.sp,
            fontWeight = FontWeight.SemiBold,
        )
    }
}

/** The official Lantern mark, drawn from a vector built out of the supplied SVG geometry. */
@Composable
fun LanternLogo(modifier: Modifier = Modifier) {
    Image(
        painter = painterResource(id = R.drawable.ic_lantern),
        contentDescription = "Lantern",
        // Fit, never Crop: the mark keeps the proportions of the drawing it came from. A logo that
        // is squashed into a box of the wrong shape reads as a cropped scrawl instead of a lamp.
        contentScale = ContentScale.Fit,
        modifier = modifier,
    )
}

@Composable
fun ScreenTitle(title: String, subtitle: String? = null) {
    Column(modifier = Modifier.fillMaxWidth()) {
        Text(text = title, color = LanternText, fontSize = 30.sp, fontWeight = FontWeight.SemiBold)
        if (subtitle != null) {
            Spacer(modifier = Modifier.height(4.dp))
            Text(text = subtitle, color = LanternMuted, fontSize = 15.sp)
        }
    }
}

@Composable
fun SectionLabel(text: String) {
    Text(text = text, color = LanternMuted, fontSize = 13.sp)
}

/** Poster slot: the image once it has arrived, a quiet placeholder until then. */
@Composable
fun PosterFrame(bitmap: ImageBitmap?, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(10.dp))
            .background(LanternSurfaceAlt),
        contentAlignment = Alignment.Center,
    ) {
        if (bitmap != null) {
            Image(
                bitmap = bitmap,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize(),
            )
        } else {
            Text(text = "Lantern", color = LanternMuted, fontSize = 12.sp)
        }
    }
}
