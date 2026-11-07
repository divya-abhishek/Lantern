package com.lantern.tv.session

import com.lantern.tv.LanternApp
import com.lantern.tv.data.Api
import com.lantern.tv.data.ApiException
import com.lantern.tv.data.DiscoveredServer
import com.lantern.tv.data.LanternAddress
import com.lantern.tv.data.LibraryFolder
import com.lantern.tv.data.LibrarySnapshot
import com.lantern.tv.data.MediaDetail
import com.lantern.tv.data.MediaSummary
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Where the app is with respect to the PC. */
sealed interface ConnectionState {
    /** Nothing tried yet. */
    data object Idle : ConnectionState

    /** Looking for a server on the LAN (or verifying a remembered one). */
    data object Searching : ConnectionState

    /** An unpaired server is showing a PIN the user types on the PC. */
    data class Pairing(
        val pin: String,
        val serverName: String,
        val secondsLeft: Int,
        val attempt: Int,
        val message: String?,
    ) : ConnectionState

    /** Paired and talking to the PC. */
    data class Online(
        val serverUrl: String,
        val serverName: String,
        val version: String,
        val transcodingAvailable: Boolean,
    ) : ConnectionState

    /** Something went wrong; the UI offers retry / manual address. */
    data class Failed(val message: String) : ConnectionState
}

enum class LibraryTab { Movies, Shows, Folders }

/**
 * The one place that owns "what is on screen".
 *
 * It drives the connection state machine (remembered server -> verify -> online, otherwise
 * discover -> PIN pairing), loads the library lazily, and never keeps a player or a page of
 * bitmaps alive beyond what the current screen needs.
 */
class Session(private val app: LanternApp) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var pairingJob: Job? = null

    private val _connection = MutableStateFlow<ConnectionState>(ConnectionState.Idle)
    val connection: StateFlow<ConnectionState> = _connection

    private val _library = MutableStateFlow<LibrarySnapshot?>(null)
    val library: StateFlow<LibrarySnapshot?> = _library

    private val _items = MutableStateFlow<List<MediaSummary>>(emptyList())
    val items: StateFlow<List<MediaSummary>> = _items

    private val _tab = MutableStateFlow(LibraryTab.Movies)
    val tab: StateFlow<LibraryTab> = _tab

    private val _activeFolder = MutableStateFlow<LibraryFolder?>(null)
    val activeFolder: StateFlow<LibraryFolder?> = _activeFolder

    private val _detail = MutableStateFlow<MediaDetail?>(null)
    val detail: StateFlow<MediaDetail?> = _detail

    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy

    private val _message = MutableStateFlow<String?>(null)
    val message: StateFlow<String?> = _message

    /** Servers the discovery listener has heard from. */
    val discovered: StateFlow<List<DiscoveredServer>> get() = app.discovery.servers

    /** The API of the current server, or null while unpaired/offline. */
    var api: Api? = null
        private set

    fun start() {
        app.discovery.start()
        scope.launch { autoConnect() }
    }

    fun stop() {
        pairingJob?.cancel()
        scope.cancel()
        app.discovery.stop()
    }

    /** Re-checks the server and reloads what the current screen shows. */
    fun refresh() {
        scope.launch {
            when (_connection.value) {
                is ConnectionState.Online -> {
                    loadLibrary()
                    loadTab(_tab.value, _activeFolder.value)
                }
                else -> autoConnect()
            }
        }
    }

    fun dismissMessage() {
        _message.value = null
    }

    private fun report(text: String) {
        _message.value = text
    }

    // --- connection --------------------------------------------------------------------

    /**
     * Automatic reconnect first: a device that was paired keeps its token, so opening the app on
     * the projector is enough. Only when that fails does the user see a PIN.
     */
    private suspend fun autoConnect() {
        _connection.value = ConnectionState.Searching
        _busy.value = true

        try {
            val trust = app.trustStore.load()
            val saved = LanternAddress.reconnectUrl(trust?.serverUrl)

            if (trust != null && saved != null) {
                val remembered = app.api(saved, trust.token)
                try {
                    val info = remembered.info()
                    if (remembered.isAuthorized()) {
                        app.trustStore.renameServer(info.name)
                        online(saved, info.name, info.version, info.transcodingAvailable)
                        rememberCapabilities()
                        return
                    }

                    // The PC no longer trusts this device (removed, or the database was reset).
                    app.trustStore.forget()
                    report("${info.name} no longer trusts this projector. Pair it again on the PC.")
                } catch (_: Exception) {
                    // The PC is off or on another network: fall through to discovery.
                    report("Could not reach ${trust.serverName}. Searching the network…")
                }
            }

            // Nothing remembered, or nothing answered: use discovery, which runs in the background.
            // Every advertised address is tried - the IPv4 ones first, because a `.local` name is
            // not resolvable on a fair number of TV boxes and routers.
            for (attempt in 0..DISCOVERY_ATTEMPTS) {
                app.discovery.probe()

                for (candidate in LanternAddress.candidates(saved = null, discovered = discovered.value)) {
                    val server = discovered.value.firstOrNull { it.baseUrl() == candidate }
                    val info = runCatching { app.anonymousApi(candidate).info() }.getOrNull() ?: continue

                    if (!info.requiresPairing) {
                        online(candidate, info.name, info.version, info.transcodingAvailable)
                        return
                    }

                    pairWithAddress(candidate, server?.name ?: info.name)
                    return
                }

                delay(DISCOVERY_POLL_MS)
            }

            _connection.value = ConnectionState.Failed(
                "No Lantern PC answered on this network. Make sure the Windows app is running and " +
                    "this projector is on the same Wi-Fi, or enter the PC address in Devices.",
            )
        } finally {
            _busy.value = false
        }
    }

    private suspend fun online(serverUrl: String, name: String, version: String, transcoding: Boolean) {
        api = app.api(serverUrl, app.trustStore.load()?.token)

        // Whichever address actually worked becomes the one to try first next time.
        app.trustStore.rememberAddress(serverUrl)
        _connection.value = ConnectionState.Online(serverUrl, name, version, transcoding)
        loadLibrary()
        loadTab(_tab.value, _activeFolder.value)
    }

    /** Tells the PC which decoders this box really has, so it can plan direct play honestly. */
    private suspend fun rememberCapabilities() {
        val client = api ?: return
        try {
            client.reportCapabilities(app.capabilities)
        } catch (_: Exception) {
            // Diagnostics only: a failed report never blocks playback.
        }
    }

    /** Connects by address (the manual fallback when multicast is filtered). */
    fun connectManually(address: String) {
        val url = LanternAddress.url(address)
        if (url == null) {
            report("That does not look like an address. Try something like 192.168.1.72:8787.")
            return
        }

        scope.launch {
            _connection.value = ConnectionState.Searching
            _busy.value = true
            try {
                val probe = app.anonymousApi(url)
                val info = probe.info()
                app.trustStore.rememberAddress(url)

                if (!info.requiresPairing) {
                    online(url, info.name, info.version, info.transcodingAvailable)
                    return@launch
                }

                pairWithAddress(url, info.name)
            } catch (ex: Exception) {
                _connection.value = ConnectionState.Failed(
                    "Nothing answered at $url. Check the address shown in Lantern on the PC.",
                )
            } finally {
                _busy.value = false
            }
        }
    }

    /** Reconnect: verifies the PC again and reloads whatever the current screen shows. */
    fun reconnect() = refresh()

    /** The name of the PC this projector is paired with, if any. */
    fun pairedServerName(): String? = app.trustStore.load()?.serverName

    fun isPaired(): Boolean = app.trustStore.load() != null

    /** Forgets the PC so the next connection needs a fresh PIN. */
    fun forgetServer() {
        pairingJob?.cancel()
        api = null
        app.trustStore.forget()
        app.discovery.probe()
        _library.value = null
        _items.value = emptyList()
        _detail.value = null
        _connection.value = ConnectionState.Idle
        scope.launch { autoConnect() }
    }

    private companion object {
        const val DISCOVERY_ATTEMPTS = 12
        const val DISCOVERY_POLL_MS = 1_500L
        const val MAX_PIN_ATTEMPTS = 3
    }

    // --- pairing -----------------------------------------------------------------------

    /** Pairs with a discovered server: the PIN it returns is the code the PC must type. */
    fun pairWith(server: DiscoveredServer) {
        val url = server.baseUrl()
        if (url == null) {
            report("${server.name} did not advertise an address. Connect by address instead.")
            return
        }

        scope.launch { pairWithAddress(url, server.name) }
    }

    /** Manual entry path: the user typed an address instead of using discovery. */
    fun pairManually(address: String) = connectManually(address)

    private suspend fun pairWithAddress(serverUrl: String, serverName: String) {
        pairingJob?.cancel()

        var attempt = 0
        _busy.value = true

        try {
            while (attempt < MAX_PIN_ATTEMPTS) {
                attempt++
                val anonymous = app.anonymousApi(serverUrl)

                val request = try {
                    anonymous.pairRequest(
                        deviceId = app.trustStore.deviceId,
                        name = app.trustStore.deviceName,
                        model = android.os.Build.MODEL,
                        manufacturer = app.trustStore.manufacturer,
                        capabilities = app.capabilities,
                    )
                } catch (ex: Exception) {
                    _connection.value =
                        ConnectionState.Failed(describe(ex, "The PC refused the pairing request."))
                    return
                }

                val pin = request.requestId
                var secondsLeft = if (request.expiresInSeconds > 0) request.expiresInSeconds else 120
                val pollMs = request.pollIntervalMs.toLong().coerceIn(800L, 3_000L)

                _connection.value = ConnectionState.Pairing(
                    pin = pin,
                    serverName = request.serverName ?: serverName,
                    secondsLeft = secondsLeft,
                    attempt = attempt,
                    message = request.message,
                )

                // Poll until the PC types this code, the code expires, or the PC refuses.
                while (secondsLeft > 0) {
                    delay(pollMs)
                    secondsLeft -= (pollMs / 1000).toInt().coerceAtLeast(1)

                    val polled = try {
                        anonymous.pairStatus(pin)
                    } catch (_: Exception) {
                        continue
                    }

                    if (polled.isApproved && polled.token != null) {
                        val token = polled.token
                        val info = runCatching { app.api(serverUrl, token).info() }.getOrNull()

                        app.trustStore.save(
                            serverUrl = serverUrl,
                            serverId = info?.serverId ?: "",
                            serverName = info?.name ?: serverName,
                            token = token,
                        )

                        report("Paired with ${info?.name ?: serverName}.")
                        online(
                            serverUrl = serverUrl,
                            name = info?.name ?: serverName,
                            version = info?.version ?: "",
                            transcoding = info?.transcodingAvailable ?: true,
                        )
                        rememberCapabilities()
                        return
                    }

                    if (polled.isRejected) {
                        _connection.value = ConnectionState.Failed(
                            polled.message ?: "The PC refused the pairing request.",
                        )
                        return
                    }

                    if (polled.isExpired) break

                    _connection.value = ConnectionState.Pairing(pin, serverName, secondsLeft, attempt, polled.message)
                }

                report("That code expired. A new one is on the way.")
            }

            _connection.value = ConnectionState.Failed(
                "Pairing timed out. Open Lantern on the PC, then try again from Settings.",
            )
        } finally {
            _busy.value = false
        }
    }

    // --- library -----------------------------------------------------------------------

    suspend fun loadLibrary() {
        val client = api ?: return
        try {
            _library.value = client.library()
        } catch (ex: Exception) {
            report(describe(ex, "The library could not be loaded."))
        }
    }

    fun selectTab(tab: LibraryTab) {
        _tab.value = tab
        if (tab != LibraryTab.Folders) _activeFolder.value = null
        scope.launch { loadTab(tab, _activeFolder.value) }
    }

    /** Shows the titles of one folder (the Folders tab). */
    fun openFolder(folder: LibraryFolder) {
        _activeFolder.value = folder
        scope.launch { loadTab(LibraryTab.Movies, folder) }
    }

    fun closeFolder() {
        _activeFolder.value = null
        scope.launch { loadTab(_tab.value, null) }
    }

    private suspend fun loadTab(tab: LibraryTab, folder: LibraryFolder?) {
        val client = api ?: return
        _busy.value = true

        try {
            val kind = when (tab) {
                LibraryTab.Movies -> "movies"
                LibraryTab.Shows -> "episode"
                LibraryTab.Folders -> "all"
            }

            val sort = if (tab == LibraryTab.Shows) "name" else "added"
            val page = client.media(kind = kind, sort = sort, folderId = folder?.id, limit = 60)
            _items.value = page.items
        } catch (ex: Exception) {
            report(describe(ex, "The library could not be loaded."))
        } finally {
            _busy.value = false
        }
    }

    fun openDetail(mediaId: Long) {
        scope.launch {
            _busy.value = true
            try {
                _detail.value = api?.detail(mediaId, withPlan = true)
            } catch (ex: Exception) {
                report(describe(ex, "That title could not be opened."))
            } finally {
                _busy.value = false
            }
        }
    }

    fun closeDetail() {
        _detail.value = null
    }

    private fun describe(ex: Exception, fallback: String): String = when (ex) {
        is ApiException -> ex.friendly()
        else -> "$fallback ${ex.message ?: ""}".trim()
    }
}
