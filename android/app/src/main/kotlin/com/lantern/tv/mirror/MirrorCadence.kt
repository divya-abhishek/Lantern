package com.lantern.tv.mirror

import android.net.TrafficStats
import android.os.Process
import android.os.SystemClock
import androidx.annotation.OptIn
import androidx.media3.common.Format
import androidx.media3.common.PlaybackParameters
import androidx.media3.common.Player
import androidx.media3.common.VideoSize
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.DecoderReuseEvaluation
import androidx.media3.exoplayer.analytics.AnalyticsListener
import androidx.media3.exoplayer.analytics.PlaybackStatsListener
import androidx.media3.exoplayer.source.LoadEventInfo
import androidx.media3.exoplayer.source.MediaLoadData
import com.lantern.tv.data.MirrorStatus
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch


internal data class CadenceSample(
    
    val sample: Int,
    val requestedFps: Int,
    val pcFps: Double,
    val pcKbps: Int,
    val containerFps: Float,
    val width: Int,
    val height: Int,
    val videoMime: String?,
    val audioMime: String?,
    val audioSampleRate: Int,
    val audioChannels: Int,
    val decoder: String?,
    val decodedFps: Double,
    val framesProcessed: Long,
    val droppedFrames: Long,
    val droppedThisWindow: Long,
    val rebuffers: Int,
    val rebufferTimeMs: Long,
    
    val audioUnderruns: Long,
    val positionAdvancedMs: Long,
    val speed: Float,
    val playing: Boolean,
    val state: String,
    val bufferedAheadMs: Long,
    val rxKbps: Int,
    
    val byteSource: String,
    val meanProcessingOffsetMs: Double,
    val renderedFirstFrame: Boolean,
    val inputFps: Double = 0.0,
    val skippedFrames: Int = 0,
) {
    

    
    val formatLabel: String
        get() {
            val size = if (width > 0 && height > 0) "$width × $height" else "unknown size"
            val rate = if (containerFps > 0f) {
                "${one(containerFps.toDouble())} fps declared"
            } else {
                "rate not declared"
            }
            return "$size · $rate"
        }

    
    // Observations only. Counters cannot identify network loss or prove panel refresh.
    fun verdict(): String = when {
        sample < MIN_SAMPLES -> "measuring"
        !playing -> "player $state"
        droppedThisWindow > 0 -> "$droppedThisWindow renderer drops in this sample"
        else -> "surface submission ${one(decodedFps)} fps; physical display unmeasured"
    }

    fun summary(): String =
        "t=${sample}s req=$requestedFps pc=${one(pcFps)}fps/${pcKbps}kbps " +
            "container=${formatLabel} rendered=${one(decodedFps)}fps input=${one(inputFps)}fps skipped=$skippedFrames frames=$framesProcessed " +
            "dropped=$droppedThisWindow(total $droppedFrames) rebuffers=$rebuffers " +
            "advanced=${positionAdvancedMs}ms speed=$speed buffered=${bufferedAheadMs}ms " +
            "rx=${rxKbps}kbps($byteSource) " +
            "offset=${one(meanProcessingOffsetMs)}ms decoder=${decoder ?: "unknown"} → ${verdict()}"

    
    fun panel(): String = buildString {
        appendLine("PC:  ${requestedFps} fps requested · ${one(pcFps)} fps encoded · $pcKbps kbps")
        appendLine("str: ${videoMime ?: "?"} · $formatLabel · ${audioLabel()}")
        appendLine("dec: ${decoder ?: "unknown"}")
        appendLine("in: ${one(inputFps)} fps into decoder · $skippedFrames skipped")
        appendLine("out: ${one(decodedFps)} fps submitted to surface · $droppedThisWindow dropped this second ($droppedFrames total)")
        appendLine("plr: ${speed}× · $state · +${positionAdvancedMs} ms/s · ahead $bufferedAheadMs ms")
        appendLine("net: $rxKbps kbps ($byteSource)")
        append("buf: $rebuffers rebuffers ($rebufferTimeMs ms) · $audioUnderruns audio underruns")
    }

    private fun audioLabel(): String =
        if (audioMime == null) "no audio track"
        else "$audioMime $audioSampleRate Hz $audioChannels ch"

    companion object {
        
        const val MIN_SAMPLES = 3

        
        fun one(value: Double): String {
            val scaled = Math.round(value * 10.0)
            return "${scaled / 10}.${Math.abs(scaled % 10)}"
        }
    }

}


@OptIn(UnstableApi::class)
internal class MirrorCadence(
    private val player: () -> Player?,
    private val pcStatus: () -> MirrorStatus?,
    private val onSample: (CadenceSample) -> Unit,
    
    private val rxBytes: () -> Long = { TrafficStats.getUidRxBytes(Process.myUid()) },
) {

    
    val playbackStats: PlaybackStatsListener = PlaybackStatsListener(false, null)

    @Volatile private var width = 0
    @Volatile private var height = 0
    @Volatile private var containerFps = 0f
    @Volatile private var videoMime: String? = null

    @Volatile private var audioMime: String? = null
    @Volatile private var audioSampleRate = 0
    @Volatile private var audioChannels = 0

    @Volatile private var decoder: String? = null
    @Volatile private var framesProcessed = 0L
    @Volatile private var droppedFrames = 0L
    @Volatile private var processingOffsetUs = 0L
    @Volatile private var renderedFirstFrame = false

    
    @Volatile private var streamBytes = 0L
    private val bytesSeenByLoad = HashMap<Long, Long>()

    private var job: Job? = null

    
    private var lastAt = 0L
    private var lastInput = -1L
    private var lastFrames = -1L
    private var lastDropped = 0L
    private var lastBytes = 0L
    private var lastPosition = 0L
    private var samples = 0

    val listener: AnalyticsListener = object : AnalyticsListener {

        override fun onVideoSizeChanged(eventTime: AnalyticsListener.EventTime, videoSize: VideoSize) {
            width = videoSize.width
            height = videoSize.height
        }

        override fun onVideoInputFormatChanged(
            eventTime: AnalyticsListener.EventTime,
            format: Format,
            decoderReuseEvaluation: DecoderReuseEvaluation?,
        ) {
            width = if (format.width > 0) format.width else width
            height = if (format.height > 0) format.height else height
            containerFps = if (format.frameRate > 0f) format.frameRate else 0f
            videoMime = format.sampleMimeType
            decoderReuseEvaluation?.decoderName?.let { decoder = it }
        }

        override fun onVideoDecoderInitialized(
            eventTime: AnalyticsListener.EventTime,
            decoderName: String,
            initializationDurationMs: Long,
        ) {
            decoder = decoderName
            MirrorDiagnostics.log("video decoder: $decoderName (initialised in ${initializationDurationMs} ms)")
        }

        override fun onVideoFrameProcessingOffset(
            eventTime: AnalyticsListener.EventTime,
            totalProcessingOffsetUs: Long,
            frameCount: Int,
        ) {
            framesProcessed += frameCount.toLong()
            processingOffsetUs += totalProcessingOffsetUs
        }

        override fun onDroppedVideoFrames(
            eventTime: AnalyticsListener.EventTime,
            droppedFramesCount: Int,
            elapsedMs: Long,
        ) {
            if (droppedFramesCount > 0) droppedFrames += droppedFramesCount
        }

        override fun onAudioInputFormatChanged(
            eventTime: AnalyticsListener.EventTime,
            format: Format,
            decoderReuseEvaluation: DecoderReuseEvaluation?,
        ) {
            audioMime = format.sampleMimeType
            audioSampleRate = format.sampleRate
            audioChannels = format.channelCount
        }

        override fun onLoadCompleted(
            eventTime: AnalyticsListener.EventTime,
            loadEventInfo: LoadEventInfo,
            mediaLoadData: MediaLoadData,
        ) {
            val task = loadEventInfo.loadTaskId
            val previous = bytesSeenByLoad[task] ?: 0L
            val now = loadEventInfo.bytesLoaded

            if (now > previous) streamBytes += now - previous
            bytesSeenByLoad[task] = now
        }

        override fun onRenderedFirstFrame(
            eventTime: AnalyticsListener.EventTime,
            output: Any,
            renderTimeMs: Long,
        ) {
            if (!renderedFirstFrame) {
                renderedFirstFrame = true
                MirrorDiagnostics.log("first frame rendered at elapsedRealtime=$renderTimeMs ms")
            }
        }
    }

    fun startSampling(scope: CoroutineScope) {
        job?.cancel()
        job = scope.launch {
            delay(INTERVAL_MS)

            while (isActive) {
                take()
                delay(INTERVAL_MS)
            }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
    }

    private fun take() {
        val now = SystemClock.elapsedRealtime()
        val exo = player()
        val status = pcStatus()
        val stats = playbackStats.combinedPlaybackStats

        val counters = (exo as? ExoPlayer)?.videoDecoderCounters
        counters?.ensureUpdated()
        val frames = counters?.renderedOutputBufferCount?.toLong() ?: 0L
        val input = counters?.queuedInputBufferCount?.toLong() ?: 0L
        val dropped = counters?.droppedBufferCount?.toLong() ?: 0L
        val bytes = if (streamBytes > 0) streamBytes else rxBytes()
        val byteSource = if (streamBytes > 0) "stream" else "socket"

        val seconds = if (lastAt == 0L) 0.0 else (now - lastAt) / 1000.0
        val position = exo?.currentPosition ?: 0L
        val buffered = exo?.let { (it.bufferedPosition - it.currentPosition).coerceAtLeast(0) } ?: 0L
        val frameDelta = if (lastFrames in 0..frames) frames - lastFrames else 0L
        val decodedFps = if (seconds > 0.0) frameDelta / seconds else 0.0

        val droppedDelta = if (dropped >= lastDropped) dropped - lastDropped else 0L
        val byteDelta = if (bytes >= lastBytes) bytes - lastBytes else 0L
        val rxKbps = if (seconds > 0.0) (byteDelta * 8 / seconds / 1000).toInt() else 0
        val advanced = if (lastPosition == 0L) 0L else position - lastPosition

        val inputFps = if (seconds > 0 && lastInput in 0..input) (input - lastInput) / seconds else 0.0
        lastInput = input
        samples++
        lastAt = now
        lastFrames = frames
        lastDropped = dropped
        lastBytes = bytes
        lastPosition = position

        onSample(
            CadenceSample(
                sample = samples,
                requestedFps = status?.fps ?: 0,
                pcFps = status?.framesPerSecond ?: 0.0,
                pcKbps = status?.measuredBitrateKbps ?: 0,
                containerFps = containerFps,
                width = width,
                height = height,
                videoMime = videoMime,
                audioMime = audioMime,
                audioSampleRate = audioSampleRate,
                audioChannels = audioChannels,
                decoder = decoder,
                decodedFps = decodedFps,
                inputFps = inputFps,
                skippedFrames = counters?.skippedOutputBufferCount ?: 0,
                framesProcessed = frames,
                droppedFrames = stats.totalDroppedFrames,
                droppedThisWindow = droppedDelta,
                rebuffers = stats.totalRebufferCount,
                rebufferTimeMs = stats.totalRebufferTimeMs,
                audioUnderruns = stats.totalAudioUnderruns,
                positionAdvancedMs = advanced,
                speed = exo?.playbackParameters?.speed ?: PlaybackParameters.DEFAULT.speed,
                playing = exo?.isPlaying ?: false,
                state = stateName(exo?.playbackState),
                bufferedAheadMs = buffered,
                rxKbps = rxKbps,
                byteSource = byteSource,
                meanProcessingOffsetMs = if (frames > 0) processingOffsetUs / 1000.0 / frames else 0.0,
                renderedFirstFrame = renderedFirstFrame,
            )
        )
    }

    
    private fun stateName(state: Int?): String = when (state) {
        Player.STATE_BUFFERING -> "BUFFERING"
        Player.STATE_READY -> "READY"
        Player.STATE_ENDED -> "ENDED"
        Player.STATE_IDLE -> "IDLE"
        else -> "?"
    }

    companion object {
        
        const val INTERVAL_MS = 1_000L
    }
}
