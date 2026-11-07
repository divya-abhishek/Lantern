package com.lantern.tv.player

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.KeyEvent
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.BackHandler
import androidx.annotation.OptIn
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.focusGroup
import androidx.compose.foundation.focusable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusProperties
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.text.font.FontWeight
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
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.Tracks
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.SeekParameters
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.datasource.DefaultDataSource
import kotlinx.coroutines.CancellationException
import androidx.compose.material3.TextButton
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import com.lantern.tv.LanternApp
import com.lantern.tv.data.Api
import com.lantern.tv.data.PlaybackPlan
import com.lantern.tv.data.SubtitleTrack
import com.lantern.tv.ui.LanternAccent
import com.lantern.tv.ui.LanternAccentBright
import com.lantern.tv.ui.LanternBg
import com.lantern.tv.ui.LanternBorder
import com.lantern.tv.ui.LanternMuted
import com.lantern.tv.ui.LanternSurface
import com.lantern.tv.ui.LanternText
import com.lantern.tv.ui.LanternTheme
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch


internal data class TrackChoice(
    val label: String,
    val group: Tracks.Group,
    val trackIndex: Int,
    val selected: Boolean,
)


internal data class PlayerMenus(
    val audio: List<TrackChoice> = emptyList(),
    val text: List<TrackChoice> = emptyList(),
    
    val subtitlesOff: Boolean = true,
)


@OptIn(UnstableApi::class)
class PlayerActivity : ComponentActivity() {

    private var player: ExoPlayer? = null
    private var playerView: PlayerView? = null
    private var api: Api? = null
    private var mediaId: Long = 0
    private var title: String = ""
    private var resumeMs: Long = 0
    private var subtitleTracks: List<SubtitleTrack> = emptyList()
    private var plan: PlaybackPlan? = null
    private var progressJob: Job? = null
    private var preparationJob: Job? = null
    private var triedTranscode = false
    private var lastReportedMs = 0L
    private var consumedKey: Int? = null

    private val status = mutableStateOf("Preparing…")
    private val errorText = mutableStateOf<String?>(null)
    private val menus = mutableStateOf(PlayerMenus())
    private val menuVisible = mutableStateOf(false)
    private val speed = mutableStateOf(1.0f)

    
    private val controlsVisible = mutableStateOf(true)
    private var hideControlsJob: Job? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        mediaId = intent.getLongExtra(EXTRA_MEDIA_ID, 0)
        title = intent.getStringExtra(EXTRA_TITLE) ?: "Playing"
        resumeMs = intent.getLongExtra(EXTRA_RESUME_MS, 0)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }

        val app = application as LanternApp
        val trust = app.trustStore.load()
        api = trust?.let { app.api(it.serverUrl, it.token) }

        if (mediaId <= 0 || api == null) {
            status.value = "This projector is not paired with a PC any more."
            errorText.value = "Open Settings and pair with the PC again."
        }

        createPlayer()

        setContent {
            // Android's predictive/system Back can bypass dispatchKeyEvent.
            BackHandler(enabled = menuVisible.value) { closeMenu() }
            LanternTheme {
                PlayerScreen(
                    player = player,
                    onPlayerView = { view ->
                        playerView = view
                        hideTrackChooserControls(view)
                    },
                    title = title,
                    status = status.value,
                    error = errorText.value,
                    controlsVisible = controlsVisible.value,
                    onToggleControls = { toggleControls() },
                    onOpenMenu = { openMenu() },
                    onCloseMenu = { closeMenu() },
                    menuVisible = menuVisible.value,
                    menus = menus.value,
                    speed = speed.value,
                    onSpeed = { applySpeed(it) },
                    onAudio = { applyAudio(it) },
                    onSubtitles = { applySubtitle(it) },
                )
            }
        }

        if (api != null && mediaId > 0) {
            preparationJob = lifecycleScope.launch { openStream(forceTranscode = false) }
        }
    }

    private fun createPlayer() {
        val built = ExoPlayer.Builder(this)
            .setSeekParameters(SeekParameters.CLOSEST_SYNC)
            .setLoadControl(
                DefaultLoadControl.Builder()
                    .setBufferDurationsMs(30_000, 120_000, 1_500, 5_000)
                    .build(),
            )
            .build()
        built.setWakeMode(C.WAKE_MODE_NETWORK)

        built.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                when (state) {
                    Player.STATE_BUFFERING -> status.value = "Buffering…"
                    Player.STATE_READY -> {
                        status.value = "Playing"
                        showControls()
                    }

                    Player.STATE_ENDED -> lifecycleScope.launch { reportProgress("ended", force = true) }
                }
                refreshMenus()
            }

            
            override fun onIsPlayingChanged(isPlaying: Boolean) = showControls(resetTimer = isPlaying)

            override fun onTracksChanged(tracks: Tracks) = refreshMenus()

            override fun onPlayerError(error: PlaybackException) = handleError(error)
        })

        player = built
    }

    private suspend fun openStream(forceTranscode: Boolean) {
        val client = api ?: return
        val exo = player ?: return

        try {
            status.value = if (forceTranscode) "Asking the PC to transcode…" else "Preparing…"

            var request = client.plan(mediaId, forceTranscode = forceTranscode)
            plan = request
            while (request.preparationState.equals("Preparing", true)) {
                status.value = "Preparing on PC · ${request.preparationPercent.toInt()}% · Back to cancel"
                delay(1000)
                request = client.plan(mediaId, forceTranscode = forceTranscode)
                plan = request
            }
            if (request.preparationState.equals("Failed", true)) error("The PC could not prepare this file.")

            val url = client.absolute(request.playUrl ?: request.directStreamUrl ?: request.hlsUrl)
            if (url == null) {
                errorText.value = "The PC did not provide a playable URL for this title."
                return
            }



            val token = (application as LanternApp).trustStore.load()?.token.orEmpty()
            val dataSourceFactory = DefaultHttpDataSource.Factory()
                .setDefaultRequestProperties(mapOf("X-Lantern-Token" to token))
                .setConnectTimeoutMs(8_000)
                .setReadTimeoutMs(20_000)
                .setAllowCrossProtocolRedirects(true)

            val builder = MediaItem.Builder().setUri(url).setMimeType(request.mimeType).setMediaId(mediaId.toString())
            val sidecars = try {
                client.subtitles(mediaId)
            } catch (_: Exception) {
                emptyList()
            }
            subtitleTracks = sidecars

            val configurations = sidecars
                .filter { !it.isBitmap && !it.url.isNullOrBlank() }
                .mapNotNull { track ->
                    val vtt = client.absolute(track.url) ?: return@mapNotNull null
                    MediaItem.SubtitleConfiguration.Builder(android.net.Uri.parse(vtt))
                        .setMimeType(track.mimeType ?: android.media.MediaFormat.MIMETYPE_TEXT_VTT)
                        .setLabel(track.label)
                        .setLanguage(track.language)
                        .build()
                }

            if (configurations.isNotEmpty()) builder.setSubtitleConfigurations(configurations)

            val mediaItem = builder.build()

            // DefaultMediaSourceFactory merges SubtitleConfigurations; the bare progressive
            // factory ignores them and leaves external subtitles unreachable.
            val source = DefaultMediaSourceFactory(DefaultDataSource.Factory(this, dataSourceFactory))
                .createMediaSource(mediaItem)

            exo.setMediaSource(source)
            if (resumeMs > 0) exo.seekTo(resumeMs)

            exo.prepare()
            exo.playWhenReady = true
            showControls()
            status.value = "Starting…"
            startProgressLoop()
        } catch (ex: CancellationException) {
            throw ex
        } catch (ex: Exception) {
            errorText.value = (ex as? com.lantern.tv.data.ApiException)?.friendly()
                ?: "The stream could not be started. ${ex.message ?: ""}".trim()
        }
    }

    
    private fun handleError(error: PlaybackException) {
        val wasDirect = plan?.isDirectPlay ?: true
        showControls(resetTimer = false)

        if (wasDirect && !triedTranscode) {
            triedTranscode = true
            status.value = "Trying once more, with the PC's help…"
            resumeMs = player?.currentPosition ?: resumeMs
            preparationJob?.cancel()
            preparationJob = lifecycleScope.launch { openStream(forceTranscode = true) }
            return
        }

        errorText.value = when (error.errorCode) {
            PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
            PlaybackException.ERROR_CODE_DECODING_FAILED,
            -> "This video cannot be played on this projector."

            PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
            PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT,
            -> "The PC stopped responding. Check the Wi-Fi connection and try again."

            PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND
            -> "The PC could not find that file. Rescan the library on the PC."

            PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS
            ->
                if (plan?.needsHls == true) {
                    "The PC could not prepare this video for playback. Check the PC and try again."
                } else {
                    "The PC could not read that file. Rescan the library on the PC."
                }

            else -> "Playback failed (${error.errorCodeName})."
        }
    }

    private fun refreshMenus() {
        val exo = player ?: return
        val audio = mutableListOf<TrackChoice>()
        val text = mutableListOf<TrackChoice>()
        var textOff = true

        exo.currentTracks.groups.forEach { group ->
            for (trackIndex in 0 until group.length) {
                if (!group.isTrackSupported(trackIndex)) continue
                val format = group.getTrackFormat(trackIndex)
                val label = format.label ?: format.language ?: format.sampleMimeType ?: "Track ${trackIndex + 1}"
                val selected = group.isTrackSelected(trackIndex)

                when (group.type) {
                    C.TRACK_TYPE_AUDIO -> audio.add(TrackChoice(label, group, trackIndex, selected))
                    C.TRACK_TYPE_TEXT -> {
                        text.add(TrackChoice(label, group, trackIndex, selected))
                        if (selected) textOff = false
                    }
                }
            }
        }

        menus.value = PlayerMenus(audio = audio, text = text, subtitlesOff = textOff)
    }

    
    private fun applyAudio(choice: TrackChoice) {
        val exo = player ?: return
        val group = exo.currentTracks.groups.firstOrNull { it.mediaTrackGroup == choice.group.mediaTrackGroup }
            ?: return

        exo.trackSelectionParameters = exo.trackSelectionParameters.buildUpon()
            .setTrackTypeDisabled(C.TRACK_TYPE_AUDIO, false)
            .setOverrideForType(TrackSelectionOverride(group.mediaTrackGroup, choice.trackIndex))
            .build()
        refreshMenus()
    }

    
    private fun applySubtitle(choice: TrackChoice?) {
        val exo = player ?: return

        if (choice == null) {
            exo.trackSelectionParameters = exo.trackSelectionParameters.buildUpon()
                .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
                .build()

            refreshMenus()
            return
        }

        val group = exo.currentTracks.groups.firstOrNull { it.mediaTrackGroup == choice.group.mediaTrackGroup }
            ?: return

        exo.trackSelectionParameters = exo.trackSelectionParameters.buildUpon()
            .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
            .setOverrideForType(TrackSelectionOverride(group.mediaTrackGroup, choice.trackIndex))
            .build()

        refreshMenus()
    }

    private fun applySpeed(value: Float) {
        player?.setPlaybackSpeed(value)
        speed.value = value
        closeMenu()
    }

    private fun closeMenu() {
        menuVisible.value = false
        playerView?.useController = true
    }

    
    private suspend fun reportProgress(state: String, force: Boolean = false) {
        val exo = player ?: return
        val client = api ?: return

        val position = exo.currentPosition
        if (!force && kotlin.math.abs(position - lastReportedMs) < 5_000) return

        lastReportedMs = position
        val duration = exo.duration.takeIf { it > 0 } ?: 0

        try {
            client.reportProgress(
                mediaId = mediaId,
                positionMs = position,
                durationMs = duration,
                state = state,
            )
        } catch (_: Exception) {
        }
    }

    private fun startProgressLoop() {
        progressJob?.cancel()
        progressJob = lifecycleScope.launch {
            while (true) {
                delay(10_000)
                reportProgress(if (player?.isPlaying == true) "playing" else "paused", force = true)
            }
        }
    }

    
    private fun showControls(resetTimer: Boolean = true) {
        controlsVisible.value = true
        playerView?.showController()

        hideControlsJob?.cancel()
        hideControlsJob = null

        if (resetTimer && player?.isPlaying == true) {
            hideControlsJob = lifecycleScope.launch {
                delay(CONTROLS_TIMEOUT_MS)
                if (player?.isPlaying == true && !menuVisible.value) hideControls()
            }
        }
    }

    private fun hideControls() {
        hideControlsJob?.cancel()
        hideControlsJob = null
        controlsVisible.value = false
        playerView?.hideController()
    }

    
    private fun toggleControls() {
        if (controlsVisible.value) hideControls() else showControls()
    }

    
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action == KeyEvent.ACTION_UP && consumedKey == event.keyCode) {
            consumedKey = null
            return true
        }
        if (event.action == KeyEvent.ACTION_DOWN && !menuVisible.value) showControls()
        if (event.action == KeyEvent.ACTION_DOWN && handleKey(event.keyCode)) {
            consumedKey = event.keyCode
            return true
        }
        return super.dispatchKeyEvent(event)
    }

    private fun handleKey(keyCode: Int): Boolean {
        if (player == null) return false
        if (menuVisible.value) {
            return if (keyCode == KeyEvent.KEYCODE_BACK) {
                closeMenu()
                true
            } else {
                false
            }
        }

        return when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_MENU -> {
                openMenu()
                true
            }

            KeyEvent.KEYCODE_DPAD_LEFT -> {
                seekBy(-SEEK_STEP_MS)
                true
            }

            KeyEvent.KEYCODE_DPAD_RIGHT -> {
                seekBy(SEEK_STEP_MS)
                true
            }

            KeyEvent.KEYCODE_DPAD_DOWN -> {
                playerView?.showController()
                false
            }

            KeyEvent.KEYCODE_DPAD_CENTER,
            KeyEvent.KEYCODE_ENTER,
            KeyEvent.KEYCODE_NUMPAD_ENTER,
            KeyEvent.KEYCODE_SPACE,
            -> {
                togglePlay()
                true
            }

            else -> false
        }
    }

    
    private fun openMenu() {
        playerView?.hideController()
        playerView?.useController = false
        refreshMenus()
        menuVisible.value = true
    }

    
    private fun hideTrackChooserControls(view: PlayerView) {
        val resources = view.context.resources
        listOf("exo_settings", "exo_subtitle", "exo_playback_speed").forEach { name ->
            val id = resources.getIdentifier(name, "id", view.context.packageName)
            if (id != 0) view.findViewById<android.view.View>(id)?.visibility = android.view.View.GONE
        }
    }

    
    private fun seekBy(deltaMs: Long) {
        val exo = player ?: return

        val duration = exo.duration
        val target = exo.currentPosition + deltaMs
        val clamped = when {
            target < 0 -> 0L
            duration > 0 && target > duration -> duration
            else -> target
        }

        exo.seekTo(clamped)
        playerView?.showController()
        lifecycleScope.launch { reportProgress(if (exo.isPlaying) "playing" else "paused", force = true) }
    }

    private fun togglePlay() {
        val exo = player ?: return
        exo.playWhenReady = !exo.playWhenReady
        playerView?.showController()
        lifecycleScope.launch { reportProgress(if (exo.playWhenReady) "playing" else "paused", force = true) }
    }

    
    override fun onResume() {
        super.onResume()
        showControls()
    }

    
    override fun onPause() {
        super.onPause()
        player?.playWhenReady = false
        lifecycleScope.launch { reportProgress("paused", force = true) }
    }

    override fun onDestroy() {
        preparationJob?.cancel()
        progressJob?.cancel()
        val sessionId = plan?.sessionId

        playerView?.player = null
        playerView = null

        player?.let { exo ->
            exo.stop()
            exo.release()
        }
        player = null
        if (!sessionId.isNullOrBlank()) {
            val client = api
            if (client != null) {
                LanternApp.instance.applicationScope.launch {
                    runCatching { client.stopTranscode(mediaId, sessionId) }
                }
            }
        }

        super.onDestroy()
    }

    companion object {
        private const val SEEK_STEP_MS = 10_000L

        
        private const val CONTROLS_TIMEOUT_MS = 5_000L
        private const val EXTRA_MEDIA_ID = "mediaId"
        private const val EXTRA_TITLE = "title"
        private const val EXTRA_RESUME_MS = "resumeMs"

        
        fun intent(context: Context, mediaId: Long, title: String, resumeMs: Long): Intent =
            Intent(context, PlayerActivity::class.java)
                .putExtra(EXTRA_MEDIA_ID, mediaId)
                .putExtra(EXTRA_TITLE, title)
                .putExtra(EXTRA_RESUME_MS, resumeMs)
    }
}

@Composable
private fun PlayerScreen(
    player: ExoPlayer?,
    onPlayerView: (PlayerView) -> Unit,
    title: String,
    status: String,
    error: String?,
    controlsVisible: Boolean,
    onToggleControls: () -> Unit,
    onOpenMenu: () -> Unit,
    onCloseMenu: () -> Unit,
    menuVisible: Boolean,
    menus: PlayerMenus,
    speed: Float,
    onSpeed: (Float) -> Unit,
    onAudio: (TrackChoice) -> Unit,
    onSubtitles: (TrackChoice?) -> Unit,
) {
    val interaction = remember { MutableInteractionSource() }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black)
            .focusProperties { canFocus = false }
            .clickable(
                interactionSource = interaction,
                indication = null,
                onClick = onToggleControls,
            ),
    ) {

        AndroidView(
            factory = { context ->
                PlayerView(context).apply {
                    useController = true
                    controllerAutoShow = false
                    controllerShowTimeoutMs = 0
                    setShowBuffering(PlayerView.SHOW_BUFFERING_WHEN_PLAYING)
                    setShowNextButton(false)
                    setShowPreviousButton(false)
                    setShowRewindButton(false)
                    setShowFastForwardButton(false)
                    resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT
                    onPlayerView(this)
                }
            },
            update = { view -> if (view.player !== player) view.player = player },
            modifier = Modifier.fillMaxSize(),
        )
        AnimatedVisibility(
            visible = controlsVisible && !menuVisible,
            enter = fadeIn(),
            exit = fadeOut(),
        ) {
            Box(modifier = Modifier.fillMaxWidth()) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(160.dp)
                        .background(
                            Brush.verticalGradient(
                                listOf(Color.Black.copy(alpha = 0.74f), Color.Transparent),
                            ),
                        ),
                )

                Column(modifier = Modifier.padding(horizontal = 32.dp, vertical = 24.dp)) {
                    Text(
                        text = title,
                        color = LanternText,
                        fontSize = 21.sp,
                        fontWeight = FontWeight.SemiBold,
                    )
                    Spacer(modifier = Modifier.height(4.dp))
                    Text(text = "$status · ${speed}×", color = LanternMuted, fontSize = 13.sp)
                    Spacer(modifier = Modifier.height(10.dp))
                    TextButton(onClick = onOpenMenu) { Text("Audio · Subtitles · Speed", color = LanternAccentBright) }
                    Text(
                        text = "OK play / pause   ·   ◀ ▶ ±10 s   ·   ▲ Audio · Subtitles · Speed",
                        color = LanternMuted.copy(alpha = 0.85f),
                        fontSize = 13.sp,
                    )
                }
            }
        }

        if (error != null) {
            ErrorCard(message = error, modifier = Modifier.align(Alignment.Center))
        }

        if (menuVisible) {
            MenuPanel(
                menus = menus,
                speed = speed,
                onAudio = onAudio,
                onSubtitles = onSubtitles,
                onSpeed = onSpeed,
                onClose = onCloseMenu,
                modifier = Modifier.align(Alignment.CenterEnd),
            )
        }
    }
}

@Composable
private fun ErrorCard(message: String, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .width(560.dp)
            .clip(RoundedCornerShape(16.dp))
            .background(LanternSurface)
            .border(1.dp, LanternBorder, RoundedCornerShape(16.dp))
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(text = "Playback stopped", color = LanternText, fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
        Spacer(modifier = Modifier.height(10.dp))
        Text(text = message, color = LanternMuted, fontSize = 15.sp)
        Spacer(modifier = Modifier.height(14.dp))
        Text(text = "Press Back to return to the library.", color = LanternMuted, fontSize = 13.sp)
    }
}

private val SPEEDS = listOf(0.75f, 1.0f, 1.25f, 1.5f, 2.0f)

@Composable
private fun MenuPanel(
    menus: PlayerMenus,
    speed: Float,
    onAudio: (TrackChoice) -> Unit,
    onSubtitles: (TrackChoice?) -> Unit,
    onSpeed: (Float) -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val firstRow = remember { FocusRequester() }

    LaunchedEffect(Unit) {
        runCatching { firstRow.requestFocus() }
    }

    Column(
        modifier = modifier
            .width(440.dp)
            .fillMaxHeight(0.88f)
            .clip(RoundedCornerShape(16.dp))
            .background(LanternSurface)
            .border(1.dp, LanternBorder, RoundedCornerShape(16.dp))
            .padding(16.dp)
            .focusGroup(),
    ) {
        LazyColumn(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            item { TextButton(onClick = onClose) { Text("Close", color = LanternText) } }
            item { MenuHeader("Audio track") }
            itemsIndexed(menus.audio) { index, choice ->
                MenuRow(
                    label = choice.label,
                    selected = choice.selected,
                    onSelect = { onAudio(choice) },
                    modifier = if (index == 0) Modifier.focusRequester(firstRow) else Modifier,
                )
            }

            item { MenuHeader("Subtitles") }
            item {
                MenuRow(
                    label = "Off",
                    selected = menus.subtitlesOff,
                    onSelect = { onSubtitles(null) },
                    modifier = if (menus.audio.isEmpty()) Modifier.focusRequester(firstRow) else Modifier,
                )
            }
            items(menus.text) { choice ->
                MenuRow(label = choice.label, selected = choice.selected, onSelect = { onSubtitles(choice) })
            }

            item { MenuHeader("Playback speed") }
            items(SPEEDS) { value ->
                MenuRow(
                    label = if (value == speed) "${value}×  ·  current" else "${value}×",
                    selected = value == speed,
                    onSelect = { onSpeed(value) },
                )
            }

            item {
                Text(
                    text = "D-pad moves · OK selects · Back closes",
                    color = LanternMuted.copy(alpha = 0.8f),
                    fontSize = 12.sp,
                    modifier = Modifier.padding(start = 12.dp, top = 14.dp, bottom = 4.dp),
                )
            }
        }
    }
}

@Composable
private fun MenuHeader(text: String) {
    Text(
        text = text,
        color = LanternMuted,
        fontSize = 12.sp,
        fontWeight = FontWeight.SemiBold,
        modifier = Modifier.padding(start = 12.dp, top = 12.dp, bottom = 6.dp),
    )
}


@Composable
private fun MenuRow(
    label: String,
    selected: Boolean,
    onSelect: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var focused by remember { mutableStateOf(false) }

    Row(
        modifier = modifier
            .fillMaxWidth()
            .onFocusChanged { focused = it.isFocused }
            .clip(RoundedCornerShape(8.dp))
            .background(
                when {
                    focused -> LanternAccent
                    selected -> LanternAccent.copy(alpha = 0.22f)
                    else -> Color.Transparent
                },
            )
            .border(
                width = if (focused || selected) 2.dp else 0.dp,
                color = if (focused) LanternAccentBright else LanternAccent.copy(alpha = 0.6f),
                shape = RoundedCornerShape(8.dp),
            )
            .clickable(onClick = onSelect)
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = if (selected) "●" else "○",
            color = if (selected) LanternAccentBright else LanternMuted,
            fontSize = 13.sp,
        )
        Spacer(modifier = Modifier.width(8.dp))
        Text(
            text = label,
            color = LanternText,
            fontSize = 15.sp,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
        )
    }
}

