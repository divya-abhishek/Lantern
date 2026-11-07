package com.lantern.tv.mirror

import android.util.Log
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URI

/**
 * What the receiver can say about itself while it is trying to connect.
 *
 * The mirror screen used to report one state - "Connecting" - for four different failures: the PC's API
 * not answering, a session that is not live yet, a stream URL this box cannot even read, and a relay
 * port nothing on this device can reach. They look identical on screen and they are fixed in three
 * completely different places, so the receiver now measures each one and names it.
 *
 * Nothing here ever writes a credential: the RTSP URL carries the session's own password, and a log
 * line about it says whether a username is present and nothing else. Every line goes to logcat under
 * one tag, so `adb logcat -s LanternMirror` is the complete story of a connection attempt.
 */
internal object MirrorDiagnostics {

    /** The one tag every line from the mirror receiver carries. */
    fun redact(value: String): String = value.replace(Regex("(?i)(rtsp[s]?://)[^\\s/]*@"), "$1[redacted]@")

    const val TAG = "LanternMirror"

    /** RTSP's own default port, used when a URL leaves the port out. */
    private const val DEFAULT_RTSP_PORT = 554

    /** How deep a nested failure chain is followed before it is called a loop and cut off. */
    private const val MAX_CAUSE_DEPTH = 8

    fun log(message: String) {
        Log.i(TAG, redact(message))
    }

    fun warn(message: String, error: Throwable? = null) {
        Log.w(TAG, redact(message) + (error?.let { " · " + describeChain(it) } ?: ""))
    }

    /** Where a stream URL points. Credentials are deliberately not part of this. */
    data class Target(val host: String, val port: Int, val path: String, val hasUser: Boolean) {

        /** `host:port/path`, the shape a message can put in front of a viewer. */
        val label: String get() = "$host:$port$path"

        /** `host:port`, for the pre-flight message, where the path is noise. */
        val endpoint: String get() = "$host:$port"
    }

    /**
     * Splits a stream URL into the parts a connection attempt depends on, or null when the URL cannot
     * be read at all - which is itself a finding worth reporting rather than a parse accident.
     */
    fun target(url: String): Target? = try {
        val parsed = URI(url.trim())
        val host = parsed.host
        if (host.isNullOrBlank()) {
            null
        } else {
            Target(
                host = host,
                port = if (parsed.port > 0) parsed.port else DEFAULT_RTSP_PORT,
                path = parsed.path ?: "",
                hasUser = !parsed.userInfo.isNullOrBlank(),
            )
        }
    } catch (_: Exception) {
        null
    }

    /** The URL as it may appear in a log line: the credential out, everything the diagnosis needs in. */
    fun describe(url: String): String {
        val target = target(url) ?: return "an unreadable URL"

        return "rtsp://<credential>@${target.label} " +
            "(host=${target.host} port=${target.port} path=${target.path} " +
            "user=${if (target.hasUser) "present" else "none"})"
    }

    /** What a pre-flight connect found, in words that fit on a TV screen. */
    data class TcpProbe(val ok: Boolean, val elapsedMs: Long, val detail: String)

    /**
     * Opens a plain TCP connection to the relay's own port and closes it again.
     *
     * This runs before Media3 on purpose. The player fails in a dozen ways that all end up as "could
     * not play", and none of them separate "the network does not carry this device to the PC" from
     * "the PC is streaming something this box cannot play". A bare socket answers the first question
     * exactly, so a transport problem is reported as a transport problem and never as a player bug.
     *
     * Blocking: call it from a worker dispatcher.
     */
    fun tcpProbe(host: String, port: Int, timeoutMs: Int): TcpProbe {
        val started = System.currentTimeMillis()

        return try {
            Socket().use { socket ->
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress(host, port), timeoutMs)
                val elapsed = System.currentTimeMillis() - started
                TcpProbe(ok = true, elapsedMs = elapsed, detail = "connected in $elapsed ms")
            }
        } catch (ex: Exception) {
            val elapsed = System.currentTimeMillis() - started
            val reason = ex.javaClass.simpleName + ": " + (ex.message ?: "no message")
            TcpProbe(ok = false, elapsedMs = elapsed, detail = "$reason (after $elapsed ms)")
        }
    }

    /**
     * Every link of a failure, not just its first line.
     *
     * A Media3 playback error is the outermost wrapper of a chain - the interesting part ("RTSP error
     * 401 Unauthorized", "Connection refused", "DECODER_INIT_FAILED") is a cause several levels down -
     * so the whole chain is flattened into one line that can be read at a glance.
     */
    fun describeChain(error: Throwable): String {
        val parts = mutableListOf<String>()
        var current: Throwable? = error
        var depth = 0

        while (current != null && depth < MAX_CAUSE_DEPTH) {
            parts += current.javaClass.name + ": " + (current.message ?: "no message")

            val next = current.cause
            if (next === current) break

            current = next
            depth++
        }

        if (current != null) parts += "…"

        return redact(parts.joinToString(" <- "))
    }
}
