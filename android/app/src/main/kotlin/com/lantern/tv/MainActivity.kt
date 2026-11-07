package com.lantern.tv

import android.content.Context
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.lantern.tv.mirror.MirrorActivity
import com.lantern.tv.player.PlayerActivity
import com.lantern.tv.session.ConnectionState
import com.lantern.tv.session.LibraryTab
import com.lantern.tv.session.Session
import com.lantern.tv.ui.DetailScreen
import com.lantern.tv.ui.DevicesScreen
import com.lantern.tv.ui.HomeScreen
import com.lantern.tv.ui.LanternBg
import com.lantern.tv.ui.LanternBorder
import com.lantern.tv.ui.LanternSurface
import com.lantern.tv.ui.LanternText
import com.lantern.tv.ui.LanternTheme
import com.lantern.tv.ui.LibraryScreen
import com.lantern.tv.ui.MirrorScreen
import com.lantern.tv.ui.PairScreen
import com.lantern.tv.ui.SettingsScreen

/**
 * The one screen the projector shows. Navigation is a small sealed state machine rather than a
 * library: six destinations, a D-pad and a Back button do not need a navigation framework, and a
 * screen that is left is dropped from composition immediately.
 */
class MainActivity : ComponentActivity() {

    private lateinit var session: Session

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        session = Session(application as LanternApp)
        session.start()

        setContent {
            LanternTheme {
                Box(modifier = Modifier.fillMaxSize().background(LanternBg)) {
                    LanternRoot(session = session, onExit = { finish() })
                }
            }
        }
    }

    override fun onDestroy() {
        session.stop()
        super.onDestroy()
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        if (intent.getBooleanExtra("connectionAccepted", false)) recreate()
    }
}

private sealed interface Screen {
    data object Home : Screen
    data object Library : Screen
    data class Detail(val id: Long) : Screen
    data object Mirror : Screen
    data object Devices : Screen
    data object Settings : Screen
}

/**
 * Opens the fullscreen player for one title.
 *
 * The context is the Activity that is showing the screen, never the Application: with
 * targetSdk >= 28 the framework refuses `startActivity` on an Application context unless the
 * Intent carries FLAG_ACTIVITY_NEW_TASK, and throws
 * `AndroidRuntimeException: Calling startActivity() from outside of an Activity context requires
 * the FLAG_ACTIVITY_NEW_TASK flag` - which is exactly what tapping a card used to do.
 */
private fun openPlayer(context: Context, mediaId: Long, title: String, resumeMs: Long) {
    context.startActivity(PlayerActivity.intent(context, mediaId, title, resumeMs))
}

/** Opens the fullscreen screen mirror receiver. */
private fun openMirror(context: Context) {
    context.startActivity(MirrorActivity.intent(context))
}

/** The app shell: one destination at a time, driven by the session state. */
@Composable
private fun LanternRoot(session: Session, onExit: () -> Unit) {
    val connection by session.connection.collectAsStateWithLifecycle()
    val library by session.library.collectAsStateWithLifecycle()
    val items by session.items.collectAsStateWithLifecycle()
    val tab by session.tab.collectAsStateWithLifecycle()
    val folder by session.activeFolder.collectAsStateWithLifecycle()
    val detail by session.detail.collectAsStateWithLifecycle()
    val busy by session.busy.collectAsStateWithLifecycle()
    val message by session.message.collectAsStateWithLifecycle()
    val discovered by session.discovered.collectAsStateWithLifecycle()

    // The Activity that is on screen: every startActivity below has to come from it, not from the
    // Application object (see openPlayer).
    val context = LocalContext.current

    var screen by remember { mutableStateOf<Screen>(Screen.Home) }

    BackHandler {
        screen = when {
            screen is Screen.Detail -> {
                session.closeDetail()
                Screen.Library
            }

            screen is Screen.Library && folder != null -> {
                session.closeFolder()
                Screen.Library
            }

            screen is Screen.Home && connection is ConnectionState.Pairing -> Screen.Devices

            screen is Screen.Home -> {
                onExit()
                Screen.Home
            }

            else -> Screen.Home
        }
    }

    Box(modifier = Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)) {
        when {
            // An unpaired projector shows its PIN, unless the user deliberately opened Settings.
            connection is ConnectionState.Pairing && screen !is Screen.Settings && screen !is Screen.Devices -> PairScreen(
                pairing = connection as ConnectionState.Pairing,
                onCancel = { screen = Screen.Devices },
            )

            screen is Screen.Detail && detail != null -> {
                val current = detail!!
                DetailScreen(
                    detail = current,
                    api = session.api,
                    onPlay = { resumeMs -> openPlayer(context, current.id, current.summary.title, resumeMs) },
                    onBack = {
                        session.closeDetail()
                        screen = Screen.Library
                    },
                )
            }

            screen is Screen.Library -> LibraryScreen(
                items = items,
                tab = tab,
                folders = library?.folders ?: emptyList(),
                activeFolder = folder,
                busy = busy,
                api = session.api,
                onSelectTab = { session.selectTab(it) },
                onOpenFolder = { session.openFolder(it) },
                onCloseFolder = { session.closeFolder() },
                onOpen = { item ->
                    session.openDetail(item.id)
                    screen = Screen.Detail(item.id)
                },
            )

            screen is Screen.Mirror -> MirrorScreen(
                connectedTo = (connection as? ConnectionState.Online)?.serverName,
                onStart = { openMirror(context) },
                onConnectToPc = { screen = Screen.Devices },
                onBack = { screen = Screen.Home },
            )

            screen is Screen.Settings -> SettingsScreen(
                connection = connection,
                onOpenDevices = { screen = Screen.Devices },
                onBack = { screen = Screen.Home },
            )

            screen is Screen.Devices -> DevicesScreen(
                connection = connection,
                discovered = discovered,
                pairedName = session.pairedServerName(),
                onReconnect = { session.reconnect() },
                onPair = { session.pairWith(it) },
                onConnectAddress = { session.connectManually(it) },
                onForget = { session.forgetServer() },
                onBack = { screen = Screen.Home },
            )

            else -> HomeScreen(
                connection = connection,
                pcFound = discovered.isNotEmpty(),
                folderCount = library?.folders?.count { it.exists } ?: 0,
                totalItems = library?.totalItems ?: 0,
                onOpenLibrary = {
                    session.selectTab(LibraryTab.Movies)
                    screen = Screen.Library
                },
                onOpenMirror = { screen = Screen.Mirror },
                onOpenDevices = { screen = Screen.Devices },
                onOpenSettings = { screen = Screen.Settings },
            )
        }

        message?.let { text ->
            NoticeBanner(
                text = text,
                onDismiss = { session.dismissMessage() },
                modifier = Modifier.align(Alignment.BottomCenter),
            )
        }
    }
}

/** A short, dismissible notice: pairing hints, "the PC no longer trusts this device", scan results. */
@Composable
private fun NoticeBanner(text: String, onDismiss: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .padding(bottom = 24.dp)
            .width(780.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(LanternSurface)
            .border(1.dp, LanternBorder, RoundedCornerShape(12.dp))
            .clickable(onClick = onDismiss)
            .padding(horizontal = 20.dp, vertical = 14.dp),
    ) {
        Text(
            text = "$text   (OK to dismiss)",
            color = LanternText,
            fontSize = 14.sp,
            textAlign = TextAlign.Center,
        )
    }
}
