package com.lantern.tv.mirror

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The cadence report's own logic, driven with numbers instead of a device.
 *
 * The measurement path itself needs a projector and a live stream, but the part that decides *which*
 * cause the numbers support does not - and that is the part that has to be right, because it is what a
 * stutter gets blamed on. Each case below is one of the causes the receiver has to be able to tell apart,
 * written as the numbers that would produce it.
 */
class MirrorCadenceTest {

    /** A healthy 1920 × 1080 @ 60 session: 60 asked for, 60 encoded, 60 decoded, nothing dropped. */
    private fun healthy() = CadenceSample(
        sample = 10,
        requestedFps = 60,
        pcFps = 59.8,
        pcKbps = 11900,
        containerFps = 60f,
        width = 1920,
        height = 1080,
        videoMime = "video/avc",
        audioMime = "audio/mp4a-latm",
        audioSampleRate = 48_000,
        audioChannels = 2,
        decoder = "c2.android.avc.decoder",
        decodedFps = 59.4,
        framesProcessed = 594,
        droppedFrames = 0,
        droppedThisWindow = 0,
        rebuffers = 0,
        rebufferTimeMs = 0,
        audioUnderruns = 0L,
        positionAdvancedMs = 1_002,
        speed = 1.0f,
        playing = true,
        state = "READY",
        bufferedAheadMs = 380,
        rxKbps = 12_400,
        byteSource = "stream",
        meanProcessingOffsetMs = 0.4,
        renderedFirstFrame = true,
    )

    @Test fun `diagnostics distinguish surface submission from physical display`() {
        val sample = healthy()
        assertTrue(sample.panel().contains("59.4 fps submitted to surface"))
        assertTrue(sample.verdict().contains("physical display unmeasured"))
        assertTrue(sample.copy(droppedThisWindow = 7).verdict().contains("7 renderer drops"))
    }
    @Test fun `missing frame rate is not fabricated`() {
        assertTrue(healthy().copy(containerFps = 0f).formatLabel.contains("rate not declared"))
    }
    @Test fun `catchup speed is not rounded to normal speed`() {
        assertTrue(healthy().copy(speed = 1.03f).summary().contains("speed=1.03"))
    }
    @Test fun `buffering is an observation not a decoder diagnosis`() {
        assertEquals("player BUFFERING", healthy().copy(playing=false, state="BUFFERING").verdict())
    }
}
