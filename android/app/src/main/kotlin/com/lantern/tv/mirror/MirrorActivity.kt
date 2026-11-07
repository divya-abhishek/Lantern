package com.lantern.tv.mirror

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.KeyEvent
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.annotation.OptIn
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.rtsp.RtspMediaSource
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import com.lantern.tv.BuildConfig
import com.lantern.tv.LanternApp
import com.lantern.tv.data.Api
import com.lantern.tv.data.ApiException
import com.lantern.tv.data.MirrorStatus
import com.lantern.tv.ui.LanternAccent
import com.lantern.tv.ui.LanternBg
import com.lantern.tv.ui.LanternBorder
import com.lantern.tv.ui.LanternMuted
import com.lantern.tv.ui.LanternSurface
import com.lantern.tv.ui.LanternText
import com.lantern.tv.ui.LanternTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

import androidx.compose.foundation.clickable
import androidx.compose.material3.TextButton
import kotlinx.coroutines.isActive
import kotlinx.coroutines.CancellationException
import android.os.SystemClock
@OptIn(UnstableApi::class)
class MirrorActivity : ComponentActivity() {
    private var player: ExoPlayer? = null
    private var api: Api? = null
    private var receiveJob: Job? = null
    private var cadence: MirrorCadence? = null
    private var lastStatus: MirrorStatus? = null
    private var openedUrl: String? = null
    private var openedAt = 0L
    private var bufferingSince = 0L
    private var retryAt = 0L
    private var lastCatchUp = 0L
    private var excessiveBufferSamples = 0
    private var failures = 0
    private val status = mutableStateOf("Connecting to the PC…")
    private val errorText = mutableStateOf<String?>(null)
    private val detail = mutableStateOf<String?>(null)
    private val panel = mutableStateOf<String?>(null)
    private val live = mutableStateOf(false)
    private val pictureSize = mutableStateOf<String?>(null)
    private val overlay = mutableStateOf(true)
    private val details = mutableStateOf(false)
    private val buffer = mutableStateOf(0L)
    private var hideAt = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
        api = (application as LanternApp).trustedApi()
        val exo = ExoPlayer.Builder(this).setLoadControl(DefaultLoadControl.Builder()
            .setBufferDurationsMs(350, 1000, 200, 350)
            .setPrioritizeTimeOverSizeThresholds(true).setBackBuffer(0, false).build()).build()
        player = exo
        exo.setWakeMode(C.WAKE_MODE_NETWORK)
        exo.addListener(object : Player.Listener {
            override fun onVideoSizeChanged(videoSize: androidx.media3.common.VideoSize) {
                pictureSize.value = if (videoSize.width > 0 && videoSize.height > 0)
                    "${videoSize.width} × ${videoSize.height}" else null
            }

            override fun onPlaybackStateChanged(state: Int) {
                MirrorDiagnostics.log("player state=$state")
                live.value = state == Player.STATE_READY
                if (state == Player.STATE_BUFFERING && bufferingSince == 0L) bufferingSince = SystemClock.elapsedRealtime()
                if (live.value) {
                    bufferingSince = 0L
                    failures = 0
                    errorText.value = null
                    status.value = "LIVE"
                    hideAt = SystemClock.elapsedRealtime() + 5000
                }
                if (state == Player.STATE_ENDED) reconnect()
            }
            override fun onPlayerError(error: PlaybackException) {
                detail.value = MirrorDiagnostics.describeChain(error)
                MirrorDiagnostics.warn("Media3 ${error.errorCodeName}: ${detail.value}")
                val cause = detail.value.orEmpty()
                errorText.value = when {
                    cause.contains("401") || cause.contains("403") -> "RTSP authentication failed. Refreshing the screen session…"
                    error.errorCodeName.contains("DECOD") -> "This device could not decode the screen. Try 720p / 30 FPS on the PC. Retrying…"
                    error.errorCodeName.contains("TIMEOUT") -> "Screen connection timed out at ${api?.serverUrl}. Retrying…"
                    else -> "RTSP playback failed (${error.errorCodeName}). Reconnecting to ${api?.serverUrl}…"
                }
                failures++
                retryAt = SystemClock.elapsedRealtime() + minOf(10000L, 1000L * failures)
                openedUrl = null
                live.value = false
                overlay.value = true
            }
        })
        val metrics = MirrorCadence({ player }, { lastStatus }, onSample = {
            if (BuildConfig.DEBUG) MirrorDiagnostics.log("cadence: ${it.summary()}")
            panel.value = it.panel()
        })
        exo.addAnalyticsListener(metrics.listener)
        exo.addAnalyticsListener(metrics.playbackStats)
        cadence = metrics
        setContent {
            LanternTheme {
                val title = if (live.value) pictureSize.value?.let { "Screen mirror · $it" } else null
                MirrorScreen(player, live.value, overlay.value, title,
                    "Receiver buffer: ${buffer.value} ms", status.value, errorText.value,
                    detail.value, panel.value, null, details.value,
                    onDetails = { details.value = !details.value; overlay.value = true },
                    onReconnect = { reconnect() })
            }
        }
    }

    override fun onStart() {
        super.onStart()
        cadence?.startSampling(lifecycleScope)
        receiveJob?.cancel()
        receiveJob = lifecycleScope.launch {
            while (isActive) {
                val now = SystemClock.elapsedRealtime()
                try {
                    val client = api ?: error("Pair this receiver with the PC from Devices.")
                    val snapshot = client.mirrorStatus()
                    lastStatus = snapshot
                    val exo = player ?: break
                    val url = snapshot.streamUrl
                    if (!snapshot.isLive || url.isNullOrBlank()) {
                        if (openedUrl != null) exo.stop()
                        openedUrl = null
                        live.value = false
                        overlay.value = true
                        errorText.value = null
                        status.value = "Waiting for the PC to start mirroring…"
                    } else if (url != openedUrl && now >= retryAt) {
                        // Each session owns new credentials. Never retry the previous session URL.
                        exo.stop()
                        live.value = false
                        overlay.value = true
                        status.value = "Connecting to ${client.serverUrl}…"
                        openedUrl = url
                        openedAt = now
                        bufferingSince = now
                        val source = RtspMediaSource.Factory().setForceUseRtpTcp(true)
                            .setTimeoutMs(5000).setDebugLoggingEnabled(false)
                            .createMediaSource(MediaItem.fromUri(url))
                        exo.setMediaSource(source)
                        exo.setPlaybackSpeed(1f)
                        excessiveBufferSamples = 0
                        exo.prepare()
                        exo.playWhenReady = true
                    } else if (!live.value && openedUrl != null && bufferingSince > 0 && now - bufferingSince > 10000) {
                        val target = MirrorDiagnostics.target(openedUrl!!)
                        val probe = target?.let { withContext(Dispatchers.IO) { MirrorDiagnostics.tcpProbe(it.host, it.port, 1500) } }
                        errorText.value = if (probe?.ok == false)
                            "RTSP relay unreachable at ${target?.endpoint}. Run Allow-Lantern-LAN.ps1 on the PC once. Retrying…"
                        else "RTSP playback timed out at ${client.serverUrl}. The relay answers but no playable frames arrived. Retrying…"
                        reconnect()
                    }
                    buffer.value = exo.totalBufferedDuration.coerceAtLeast(0)
                    // RTSP has no manifest live offset: keep its observable queue near
                    // the live edge using Media3's pitch-preserving A/V clock adjustment.
                    // Startup and a single Wi-Fi burst must not cause a restart loop.
                    if (exo.isPlaying && now - openedAt > 5000) {
                        val rate = when {
                            buffer.value > 600 -> 1.03f
                            buffer.value < 350 -> 1f
                            else -> exo.playbackParameters.speed
                        }
                        if (exo.playbackParameters.speed != rate) exo.setPlaybackSpeed(rate)
                    }
                    excessiveBufferSamples = if (buffer.value > 3000) excessiveBufferSamples + 1 else 0
                    if (excessiveBufferSamples >= 5 && now - lastCatchUp > 30000) {
                        lastCatchUp = now
                        excessiveBufferSamples = 0
                        reconnect()
                    }
                    if (live.value && !details.value && now > hideAt) overlay.value = false
                } catch (ex: CancellationException) { throw ex }
                catch (ex: Exception) {
                    errorText.value = if (ex is ApiException && ex.status == 401)
                        "Pairing was revoked. Return to Devices and pair with the PC again."
                    else "Cannot reach Lantern PC at ${api?.serverUrl ?: "unpaired"}. Check the connection."
                    detail.value = MirrorDiagnostics.describeChain(ex)
                    overlay.value = true
                }
                delay(1000)
            }
        }
    }

    private fun reconnect() {
        player?.stop()
        openedUrl = null
        live.value = false
        overlay.value = true
        status.value = "Reconnecting…"
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.keyCode == KeyEvent.KEYCODE_BACK) return super.dispatchKeyEvent(event)
        if (event.action == KeyEvent.ACTION_DOWN && event.keyCode == KeyEvent.KEYCODE_MENU) {
            details.value = !details.value
            overlay.value = true
            return true
        }
        if (event.action == KeyEvent.ACTION_DOWN) { overlay.value = true; hideAt = SystemClock.elapsedRealtime() + 5000 }
        return super.dispatchKeyEvent(event)
    }

    override fun onStop() {
        receiveJob?.cancel()
        cadence?.stop()
        player?.stop()
        openedUrl = null
        live.value = false
        super.onStop()
    }

    override fun onDestroy() {
        player?.release()
        player = null
        super.onDestroy()
    }

    companion object {
        fun intent(context: Context): Intent = Intent(context, MirrorActivity::class.java)
    }
}

@Composable
private fun MirrorScreen(
    player: ExoPlayer?,
    live: Boolean,
    overlayVisible: Boolean,
    desktop: String?,
    latency: String?,
    status: String,
    error: String?,
    detail: String?,
    cadence: String?,
    cadenceVerdict: String?,
    debug: Boolean,
    onDetails: () -> Unit,
    onReconnect: () -> Unit,
) {
    Box(modifier = Modifier.fillMaxSize().background(LanternBg).clickable(onClick = onDetails)) {
        if (player != null) {
            AndroidView(
                factory = { context ->
                    PlayerView(context).apply {
                        useController = false
                        // FIT, never stretched: the desktop keeps its aspect ratio on any panel.
                        resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT
                        setShutterBackgroundColor(android.graphics.Color.TRANSPARENT)
                        this.player = player
                    }
                },
                modifier = Modifier.fillMaxSize(),
            )
        }

        if (overlayVisible && error == null) {
            Column(
                modifier = Modifier
                    .align(Alignment.TopStart)
                    .padding(28.dp)
                    .clip(RoundedCornerShape(14.dp))
                    .background(LanternSurface.copy(alpha = 0.88f))
                    .padding(horizontal = 20.dp, vertical = 14.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (live) {
                        Box(
                            modifier = Modifier
                                .clip(RoundedCornerShape(4.dp))
                                .background(LanternAccent)
                                .padding(horizontal = 8.dp, vertical = 3.dp),
                        ) {
                            Text(
                                text = "● LIVE",
                                color = Color.White,
                                fontSize = 12.sp,
                                fontWeight = FontWeight.Bold,
                            )
                        }

                        Spacer(modifier = Modifier.width(10.dp))
                    }

                    Text(
                        text = desktop ?: "Screen mirror",
                        color = LanternText,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.SemiBold,
                    )
                }

                if (!live) Text(text = status, color = LanternMuted, fontSize = 13.sp)
                Row {
                    TextButton(onClick = onDetails) { Text("Details", color = LanternText) }
                    TextButton(onClick = onReconnect) { Text("Reconnect", color = LanternText) }
                }

                if (debug) latency?.let { Text(text = it, color = LanternMuted, fontSize = 13.sp) }

                // What this receiver measures about the picture it is being handed. On every build, not
                // only debug ones: the projector is the one screen in this project that cannot be reached
                // from a development machine, so the numbers have to be readable where they happen.
                if (debug) cadence?.let { panel ->
                    Spacer(modifier = Modifier.height(10.dp))
                    Text(text = panel, color = LanternMuted, fontSize = 11.sp, lineHeight = 15.sp)
                }

                if (debug) cadenceVerdict?.let { answer ->
                    Spacer(modifier = Modifier.height(6.dp))
                    Text(
                        text = answer,
                        color = LanternText,
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                    )
                }

                // Debug builds only: the exact reason the connection attempt is where it is. A release
                // build shows the sentence a viewer can act on instead, and logs the rest.
                if (debug && detail != null) {
                    Text(text = detail, color = LanternMuted, fontSize = 11.sp)
                }
            }
        }
        error?.let { message ->
            Box(
                modifier = Modifier
                    .align(Alignment.Center)
                    .width(620.dp)
                    .clip(RoundedCornerShape(16.dp))
                    .background(LanternSurface)
                    .border(1.dp, LanternBorder, RoundedCornerShape(16.dp))
                    .padding(24.dp),
            ) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        text = "Screen mirror stopped",
                        color = LanternText,
                        fontSize = 20.sp,
                        fontWeight = FontWeight.SemiBold,
                    )
                    Spacer(modifier = Modifier.height(10.dp))
                    Text(
                        text = message,
                        color = LanternMuted,
                        fontSize = 15.sp,
                        textAlign = TextAlign.Center,
                    )
                    Spacer(modifier = Modifier.height(14.dp))
                    // The same debug detail under the failure card, so the reason is on the screen a
                    // tester is looking at instead of only in logcat.
                    if (debug && detail != null) {
                        Text(
                            text = detail,
                            color = LanternMuted,
                            fontSize = 12.sp,
                            textAlign = TextAlign.Center,
                        )
                        Spacer(modifier = Modifier.height(10.dp))
                    }
                    TextButton(onClick = onReconnect) { Text("Reconnect", color = LanternText) }
                    Text(text = "Press Back to return home.", color = LanternMuted, fontSize = 13.sp)
                }
            }
        }
    }
}
