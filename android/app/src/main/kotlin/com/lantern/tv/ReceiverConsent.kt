package com.lantern.tv

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import com.lantern.tv.data.ConnectionInvitation
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.lang.ref.WeakReference

/** Foreground-only Yes/No consent, usable by touch, mouse and native D-pad focus. */
class ReceiverConsent(private val app: LanternApp) {
    private val main = Handler(Looper.getMainLooper())
    private var activity = WeakReference<Activity>(null)
    private var dialog: AlertDialog? = null
    private val seen = LinkedHashSet<String>()
    private var lastPrompt = 0L
    fun resumed(value: Activity) { activity = WeakReference(value) }
    fun paused(value: Activity) {
        if (activity.get() === value) { dialog?.dismiss(); dialog = null; activity.clear() }
    }

    fun receive(invitation: ConnectionInvitation) { main.post {
        val owner = activity.get() ?: return@post
        if (owner.isFinishing || owner.isDestroyed || !seen.add(invitation.secret)) return@post
        while (seen.size > 64) seen.remove(seen.first())
        val now = android.os.SystemClock.elapsedRealtime()
        if (dialog != null || now - lastPrompt < 5000) return@post
        lastPrompt = now
        val prompt = AlertDialog.Builder(owner)
            .setTitle("Connect to ${invitation.serverName}?")
            .setMessage("${invitation.serverUrl}\n\nAllow this PC to connect to Lantern on this receiver?")
            .setNegativeButton("No") { _, _ -> }
            .setPositiveButton("Yes") { _, _ -> accept(invitation) }
            .create()
        dialog = prompt
        prompt.setOnDismissListener { if (dialog === prompt) dialog = null }
        prompt.show()
        prompt.getButton(AlertDialog.BUTTON_NEGATIVE).requestFocus()
        main.postDelayed({ if (dialog === prompt) prompt.dismiss() }, 60_000)
    } }

    private fun accept(invitation: ConnectionInvitation) {
        app.applicationScope.launch {
            try {
                val api = app.anonymousApi(invitation.serverUrl)
                val requested = api.pairRequest(app.trustStore.deviceId, app.trustStore.deviceName,
                    android.os.Build.MODEL, app.trustStore.manufacturer, app.capabilities, invitation.secret)
                var token: String? = null
                for (attempt in 0..4) {
                    val status = api.pairStatus(requested.requestId)
                    token = status.token
                    if (status.isApproved && token != null) break
                    delay(500)
                }
                val approvedToken = token ?: error("The PC did not approve this connection. Click Connect again.")
                val info = app.api(invitation.serverUrl, approvedToken).info()
                app.trustStore.save(invitation.serverUrl, info.serverId, info.name, approvedToken)
                withContext(Dispatchers.Main) {
                    Toast.makeText(app, "Connected to ${info.name}", Toast.LENGTH_SHORT).show()
                    app.startActivity(Intent(app, MainActivity::class.java)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
                        .putExtra("connectionAccepted", true))
                }
            } catch (_: Exception) {
                withContext(Dispatchers.Main) {
                    Toast.makeText(app, "Connection failed. Check the PC address and click Connect again.", Toast.LENGTH_LONG).show()
                }
            }
        }
    }
}
