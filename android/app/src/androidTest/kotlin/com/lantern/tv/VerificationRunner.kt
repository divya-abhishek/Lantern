package com.lantern.tv

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.os.Bundle
import android.os.SystemClock
import android.view.KeyEvent
import androidx.media3.common.C
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.analytics.AnalyticsListener
import com.lantern.tv.player.PlayerActivity
import com.lantern.tv.mirror.MirrorActivity
import kotlinx.coroutines.runBlocking

/** Runs the real receiver/player on Android. No test-only activity or auth bypass ships in the app. */
class VerificationRunner : Instrumentation() {
    private lateinit var args: Bundle
    override fun onCreate(arguments: Bundle?) { super.onCreate(arguments); args = arguments ?: Bundle(); start() }
    private val results = ArrayList<String>()
    private fun checkThat(ok: Boolean, name: String) { check(ok) { name }; results.add("PASS $name") }
    private fun <T> main(block: () -> T): T {
        var result: Result<T>? = null
        runOnMainSync { result = runCatching(block) }
        return result!!.getOrThrow()
    }
    private fun waitFor(name: String, seconds: Int = 40, condition: () -> Boolean) {
        val until = SystemClock.elapsedRealtime() + seconds * 1000
        while (SystemClock.elapsedRealtime() < until) { if (main(condition)) { results.add("PASS $name"); return }; SystemClock.sleep(100) }
        error("Timed out: $name")
    }
    private fun player(activity: Activity): ExoPlayer = activity.javaClass.getDeclaredField("player").apply { isAccessible = true }.get(activity) as ExoPlayer
    override fun onStart() {
        try {
            uiAutomation // Connect accessibility before the first window query.
            val app = targetContext.applicationContext as LanternApp
            when (args.getString("mode", "media")) {
                "pair" -> runBlocking {
                    val url = args.getString("server", "http://10.0.2.2:8787")
                    val api = app.anonymousApi(url)
                    val info = api.info()
                    val request = api.pairRequest(app.trustStore.deviceId, "Lantern emulator verification", android.os.Build.MODEL, "Android", app.capabilities)
                    sendStatus(1, Bundle().apply { putString("stream", "Approve PIN ${request.requestId} on the PC\n") })
                    var paired = false
                    repeat(80) {
                        if (!paired) {
                            kotlinx.coroutines.delay(1000)
                            val status = api.pairStatus(request.requestId)
                            if (status.isApproved && status.token != null) {
                                app.trustStore.save(url, info.serverId, info.name, status.token)
                                paired = app.trustedApi()!!.isAuthorized()
                            }
                        }
                    }
                    checkThat(paired, "PIN approval, private polling secret, token authentication")
                }
                "reconnect" -> {
                    val activity = startActivitySync(MirrorActivity.intent(targetContext).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    val exo = main { player(activity) }
                    waitFor("initial 720p mirror", 40) { exo.isPlaying && exo.videoSize.height == 720 }
                    sendStatus(1, Bundle().apply { putString("stream", "RESTART_READY: change PC mirror to 1080p now\n") })
                    waitFor("new session reconnects at 1080p", 60) { exo.isPlaying && exo.videoSize.height == 1080 }
                    main { activity.moveTaskToBack(true) }
                    waitFor("background stops receiver") { !exo.isPlaying }
                    main { targetContext.startActivity(MirrorActivity.intent(targetContext).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT)) }
                    waitFor("foreground reconnects", 40) { exo.isPlaying && exo.videoSize.height == 1080 }
                    main { checkThat(exo.playerError == null, "reconnected without player error"); activity.finish() }
                }
                "mirror" -> {
                    val activity = startActivitySync(MirrorActivity.intent(targetContext).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    val exo = main { player(activity) }
                    var decoderName = "unknown"
                    var bufferingEvents = 0
                    var audioSinkErrors = 0
                    main { exo.addAnalyticsListener(object : AnalyticsListener {
                        override fun onVideoDecoderInitialized(eventTime: AnalyticsListener.EventTime, name: String, initializationDurationMs: Long) { decoderName = name }
                        override fun onPlaybackStateChanged(eventTime: AnalyticsListener.EventTime, state: Int) { if (state == Player.STATE_BUFFERING) bufferingEvents++ }
                        override fun onAudioSinkError(eventTime: AnalyticsListener.EventTime, audioSinkError: Exception) { audioSinkErrors++ }
                    }) }
                    waitFor("Android RTSP READY", 40) { exo.isPlaying }
                    SystemClock.sleep(3000)
                    val initialCounters = main { checkNotNull(exo.videoDecoderCounters) { "Mirror decoder disappeared during startup" } }
                    val start = main { initialCounters.ensureUpdated(); initialCounters.renderedOutputBufferCount }
                    val inputStart = initialCounters.queuedInputBufferCount
                    val dropStart = initialCounters.droppedBufferCount
                    val dropInputStart = initialCounters.droppedInputBufferCount
                    val skippedStart = initialCounters.skippedOutputBufferCount
                    val skippedInputStart = initialCounters.skippedInputBufferCount
                    val initialBuffer = main { exo.totalBufferedDuration }
                    val initialBufferEvents = bufferingEvents
                    val began = SystemClock.elapsedRealtime()
                    var maxBuffer = 0L
                    var resets = 0
                    repeat(args.getString("seconds", "15").toInt()) {
                        SystemClock.sleep(1000)
                        main {
                            maxBuffer = maxOf(maxBuffer, exo.totalBufferedDuration)
                            if (exo.videoDecoderCounters !== initialCounters) resets++
                        }
                    }
                    main {
                        val counters = checkNotNull(exo.videoDecoderCounters) { "Mirror decoder stopped during observation (resets=$resets)" }; counters.ensureUpdated()
                        val fps = (counters.renderedOutputBufferCount - start) * 1000.0 / (SystemClock.elapsedRealtime() - began)
                        val elapsedSeconds = (SystemClock.elapsedRealtime() - began) / 1000.0
                        results.add("MEASURE windowSeconds=$elapsedSeconds decoder=$decoderName decoderInputFps=${(counters.queuedInputBufferCount - inputStart) / elapsedSeconds} droppedInput=${counters.droppedInputBufferCount - dropInputStart} skippedInput=${counters.skippedInputBufferCount - skippedInputStart} droppedTotal=${counters.droppedBufferCount - dropStart} skippedOutput=${counters.skippedOutputBufferCount - skippedStart} bufferingEvents=${bufferingEvents - initialBufferEvents} initialBufferMs=$initialBuffer finalBufferMs=${exo.totalBufferedDuration}")
                        checkThat(exo.playerError == null && exo.isPlaying, "mirror remained playing")
                        checkThat(audioSinkErrors == 0, "audio clock remains continuous: $audioSinkErrors sink errors")
                        checkThat(exo.currentTracks.groups.any { it.type == C.TRACK_TYPE_AUDIO }, "RTSP audio track present")
                        results.add("MEASURE submittedToSurfaceFps=${if (resets == 0) fps else "INVALID-reset"} dropped=${counters.droppedBufferCount} skipped=${counters.skippedOutputBufferCount} bufferMs=${exo.totalBufferedDuration} maxBufferMs=$maxBuffer resetSamples=$resets size=${exo.videoSize.width}x${exo.videoSize.height}")
                        checkThat(resets == 0, "no decoder restart during observation")
                        activity.finish()
                    }
                }
                else -> {
                    val id = args.getString("mediaId")!!.toLong()
                    val activity = startActivitySync(PlayerActivity.intent(targetContext, id, "Verification", 0).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    val exo = main { player(activity) }
                    waitFor("Android finite media READY", 180) { exo.isPlaying }
                    val duration = main { exo.duration }
                    checkThat(duration > 5000, "finite duration known at READY: $duration ms")
                    main { checkThat(exo.isCurrentMediaItemSeekable && !exo.isCurrentMediaItemLive, "seekable non-live timeline"); exo.pause() }
                    waitFor("pause acknowledged") { !exo.isPlaying && !exo.playWhenReady }
                    SystemClock.sleep(300)
                    val pausedAt = main { exo.currentPosition }
                    SystemClock.sleep(1200)
                    main { checkThat(kotlin.math.abs(exo.currentPosition - pausedAt) < 100, "pause holds position: $pausedAt -> ${exo.currentPosition} ms"); exo.seekTo(duration / 2); exo.play() }
                    waitFor("seek forward resumes") { exo.isPlaying && exo.currentPosition >= duration / 2 - 1500 }
                    main { exo.seekTo(1000) }
                    waitFor("seek backward resumes") { exo.isPlaying && exo.currentPosition < 5000 }
                    main { exo.pause() } // Do not let a short fixture reach EOF while checking controls.
                    sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_UP)
                    SystemClock.sleep(700)
                    val audioBefore = main { exo.currentTracks.groups.filter { it.type == C.TRACK_TYPE_AUDIO }.indexOfFirst { it.isSelected } }
                    val audioCount = main { exo.currentTracks.groups.count { it.type == C.TRACK_TYPE_AUDIO } }
                    results.add("MEASURE audioBefore=$audioBefore audioCount=$audioCount")
                    if (audioCount > 1) {
                        if (audioBefore == 0) sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_DOWN)
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_CENTER)
                        waitFor("D-pad selects different audio track") { exo.currentTracks.groups.filter { it.type == C.TRACK_TYPE_AUDIO }.indexOfFirst { it.isSelected } != audioBefore }
                    }
                    if (args.getString("subtitles", "false") == "true") {
                        // Off follows audio rows, then embedded/external subtitle rows.
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_DOWN)
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_DOWN)
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_CENTER)
                        waitFor("D-pad enables subtitle track") { exo.currentTracks.groups.any { it.type == C.TRACK_TYPE_TEXT && it.isSelected } }
                        main { checkThat(exo.currentTracks.groups.count { it.type == C.TRACK_TYPE_TEXT } >= 2, "embedded and external subtitle tracks attached") }
                        val embedded = main { exo.currentTracks.groups.first { it.type == C.TRACK_TYPE_TEXT && it.isSelected }.mediaTrackGroup.id }
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_DOWN)
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_CENTER)
                        waitFor("external subtitle actually selected") { exo.currentTracks.groups.any { it.type == C.TRACK_TYPE_TEXT && it.isSelected && it.mediaTrackGroup.id != embedded } }
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_UP)
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_UP)
                        sendKeyDownUpSync(KeyEvent.KEYCODE_DPAD_CENTER)
                        waitFor("D-pad subtitle Off") { exo.currentTracks.groups.none { it.type == C.TRACK_TYPE_TEXT && it.isSelected } }
                    }
                    sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
                    SystemClock.sleep(500)
                    main { checkThat(!activity.isFinishing && !activity.isDestroyed, "Back closes chooser without exiting playback") }
                    // A real pointer tap must open the same chooser on phones.
                    uiAutomation.waitForIdle(300, 5000)
                    var root = uiAutomation.rootInActiveWindow
                    repeat(20) { if (root == null) { SystemClock.sleep(100); root = uiAutomation.rootInActiveWindow } }
                    fun findTouchControl(node: android.view.accessibility.AccessibilityNodeInfo?): android.view.accessibility.AccessibilityNodeInfo? {
                        if (node == null) return null
                        if (node.text?.toString() == "Audio · Subtitles · Speed") return node
                        for (i in 0 until node.childCount) findTouchControl(node.getChild(i))?.let { return it }
                        return null
                    }
                    val control = findTouchControl(root)
                        ?: error("Touch track control missing")
                    val rect = android.graphics.Rect().also { control.getBoundsInScreen(it) }
                    val touchAt = SystemClock.uptimeMillis()
                    for (action in listOf(android.view.MotionEvent.ACTION_DOWN, android.view.MotionEvent.ACTION_UP)) {
                        val event = android.view.MotionEvent.obtain(touchAt, SystemClock.uptimeMillis(), action, rect.centerX().toFloat(), rect.centerY().toFloat(), 0)
                        event.source = android.view.InputDevice.SOURCE_TOUCHSCREEN
                        uiAutomation.injectInputEvent(event, true)
                        event.recycle()
                    }
                    waitFor("touch opens Audio/Subtitles chooser") {
                        (activity.javaClass.getDeclaredField("menuVisible").apply { isAccessible = true }.get(activity) as androidx.compose.runtime.State<*>).value == true
                    }
                    sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
                    main { checkThat(exo.duration == duration, "duration did not grow"); exo.seekTo(duration - 1500); exo.play() }
                    waitFor("EOF", 15) { exo.playbackState == Player.STATE_ENDED }
                    main { checkThat(exo.playerError == null, "no playback error"); activity.finish() }
                }
            }
            finish(Activity.RESULT_OK, Bundle().apply { putString("stream", results.joinToString("\n") + "\n") })
        } catch (t: Throwable) {
            val root = uiAutomation.rootInActiveWindow
            fun dump(node: android.view.accessibility.AccessibilityNodeInfo?, depth: Int = 0) {
                if (node == null) return
                if (node.text != null || node.isFocused) results.add("UI ${node.text} focused=${node.isFocused}")
                for (i in 0 until node.childCount) dump(node.getChild(i), depth + 1)
            }
            dump(root)
            finish(Activity.RESULT_CANCELED, Bundle().apply { putString("stream", results.joinToString("\n") + "\nFAIL " + t.toString() + "\n") })
        }
    }
}
