package com.lantern.tv

import android.app.ActivityManager
import android.app.Application
import android.content.Context
import com.lantern.tv.data.Api
import com.lantern.tv.data.Capabilities
import com.lantern.tv.data.Discovery
import com.lantern.tv.data.Trust
import com.lantern.tv.data.TrustStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import org.json.JSONObject

/**
 * Process-wide singletons.
 *
 * There is exactly one trust store, one discovery listener and one API client for the process;
 * the activities borrow them instead of creating their own, which is what keeps the memory
 * footprint of a 1 GB TV box flat across screen changes.
 */
class LanternApp : Application() {

    lateinit var trustStore: TrustStore
        private set

    lateinit var discovery: Discovery
        private set

    lateinit var capabilities: JSONObject
        private set

    var isLowRam: Boolean = false
        private set

    /**
     * Scope for work that must outlive an activity: stopping a transcode session when the player
     * closes, for example. Cancelled only when the process dies.
     */
    val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onCreate() {
        super.onCreate()

        instance = this
        trustStore = TrustStore(this)
        discovery = Discovery(this, trustStore.deviceId, trustStore.deviceName)
        val consent = ReceiverConsent(this)
        discovery.onInvitation = consent::receive
        isLowRam = (getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager)?.isLowRamDevice == true

        // MediaCodecList is walked once, not per request.
        capabilities = Capabilities.collect(
            deviceId = trustStore.deviceId,
            name = trustStore.deviceName,
            model = android.os.Build.MODEL,
            manufacturer = trustStore.manufacturer,
            lowRam = isLowRam,
        )
        registerActivityLifecycleCallbacks(object : ActivityLifecycleCallbacks {
            private var started = 0
            override fun onActivityStarted(activity: android.app.Activity) {
                if (started++ == 0) discovery.start()
            }
            override fun onActivityStopped(activity: android.app.Activity) {
                if (--started == 0) discovery.stop()
            }
            override fun onActivityCreated(activity: android.app.Activity, state: android.os.Bundle?) = Unit
            override fun onActivityResumed(activity: android.app.Activity) = consent.resumed(activity)
            override fun onActivityPaused(activity: android.app.Activity) = consent.paused(activity)
            override fun onActivitySaveInstanceState(activity: android.app.Activity, state: android.os.Bundle) = Unit
            override fun onActivityDestroyed(activity: android.app.Activity) = Unit
        })
    }

    /** An API client for a server, using the stored token when this device is trusted. */
    fun api(serverUrl: String, token: String? = null): Api = Api(serverUrl, token)

    fun trustedApi(): Api? {
        val trust: Trust = trustStore.load() ?: return null
        return Api(trust.serverUrl, trust.token)
    }

    /** The API used for anonymous calls (server info and the pairing handshake). */
    fun anonymousApi(serverUrl: String): Api = Api(serverUrl, null)

    override fun onTerminate() {
        discovery.stop()
        super.onTerminate()
    }

    companion object {
        lateinit var instance: LanternApp
            private set
    }
}
