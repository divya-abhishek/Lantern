package com.lantern.tv.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
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
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lantern.tv.data.Api
import com.lantern.tv.data.LibraryFolder
import com.lantern.tv.data.MediaSummary
import com.lantern.tv.data.PosterCache
import com.lantern.tv.session.LibraryTab

/**
 * Loads a poster only when its tile is actually composed (the grid is lazy) and takes it from the
 * bounded cache when it has been seen before. Nothing else in the app decodes images.
 */
@Composable
fun rememberPoster(api: Api?, mediaId: Long): ImageBitmap? {
    var bitmap by remember(mediaId) {
        mutableStateOf(PosterCache.peek(mediaId.toString())?.asImageBitmap())
    }

    LaunchedEffect(mediaId, api) {
        if (bitmap == null && api != null) {
            val loaded = PosterCache.load(api, mediaId)
            if (loaded != null) bitmap = loaded.asImageBitmap()
        }
    }

    return bitmap
}

/** The library: tabs on top, a lazily built poster grid below, folders as their own list. */
@Composable
@OptIn(ExperimentalLayoutApi::class)
fun LibraryScreen(
    items: List<MediaSummary>,
    tab: LibraryTab,
    folders: List<LibraryFolder>,
    activeFolder: LibraryFolder?,
    busy: Boolean,
    api: Api?,
    onSelectTab: (LibraryTab) -> Unit,
    onOpenFolder: (LibraryFolder) -> Unit,
    onCloseFolder: () -> Unit,
    onOpen: (MediaSummary) -> Unit,
) {
    Column(modifier = Modifier.fillMaxSize().padding(16.dp)) {
        FlowRow(horizontalArrangement = Arrangement.spacedBy(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(
                text = "Media Library",
                color = LanternText,
                fontSize = 28.sp,
                fontWeight = FontWeight.SemiBold,
            )

            FlowRow(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                TabPill("Movies", tab == LibraryTab.Movies) { onSelectTab(LibraryTab.Movies) }
                TabPill("TV Shows", tab == LibraryTab.Shows) { onSelectTab(LibraryTab.Shows) }
                TabPill("Folders", tab == LibraryTab.Folders) { onSelectTab(LibraryTab.Folders) }
            }

            if (busy) {
                CircularProgressIndicator(
                    modifier = Modifier.height(22.dp).width(22.dp),
                    color = LanternAccentBright,
                    strokeWidth = 2.dp,
                )
            }
        }

        if (activeFolder != null) {
            Spacer(modifier = Modifier.height(12.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = "${activeFolder.name} · ${activeFolder.path}",
                    color = LanternMuted,
                    fontSize = 13.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                LanternButton(label = "All titles", onClick = onCloseFolder, primary = false)
            }
        }

        Spacer(modifier = Modifier.height(16.dp))

        when {
            tab == LibraryTab.Folders && activeFolder == null ->
                FolderList(folders = folders, onOpenFolder = onOpenFolder)

            items.isEmpty() && !busy -> EmptyLibrary()

            else -> LazyVerticalGrid(
                columns = GridCells.Adaptive(150.dp),
                horizontalArrangement = Arrangement.spacedBy(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(items, key = { it.id }) { item ->
                    MediaTile(item = item, api = api) { onOpen(item) }
                }
            }
        }
    }
}

@Composable
private fun EmptyLibrary() {
    Column(
        modifier = Modifier.fillMaxSize(),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(text = "Nothing here yet", color = LanternText, fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
        Spacer(modifier = Modifier.height(8.dp))
        Text(
            text = "Add a movie folder in Lantern on the PC and scan it there.\n" +
                "This device shows what the PC has found.",
            color = LanternMuted,
            fontSize = 14.sp,
        )
    }
}

@Composable
private fun FolderList(folders: List<LibraryFolder>, onOpenFolder: (LibraryFolder) -> Unit) {
    if (folders.isEmpty()) {
        EmptyLibrary()
        return
    }

    LazyColumn(verticalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.fillMaxSize()) {
        items(folders, key = { it.id }) { folder ->
            LanternCard(onClick = { onOpenFolder(folder) }, modifier = Modifier.fillMaxWidth()) {
                Text(text = folder.name, color = LanternText, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
                Text(
                    text = "${folder.mediaCount} title(s) · ${folder.path}",
                    color = LanternMuted,
                    fontSize = 13.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
private fun MediaTile(item: MediaSummary, api: Api?, onClick: () -> Unit) {
    var focused by remember { mutableStateOf(false) }
    val poster = rememberPoster(api, item.id)

    Column(
        modifier = Modifier
            .onFocusChanged { focused = it.isFocused }
            .clickable(onClick = onClick)
            .clip(RoundedCornerShape(12.dp))
            .background(LanternSurface)
            .border(
                width = if (focused) 3.dp else 1.dp,
                color = if (focused) LanternAccentBright else LanternBorder,
                shape = RoundedCornerShape(12.dp),
            )
            .padding(10.dp),
    ) {
        PosterFrame(bitmap = poster, modifier = Modifier.fillMaxWidth().height(180.dp))

        Spacer(modifier = Modifier.height(8.dp))
        Text(
            text = item.title,
            color = LanternText,
            fontSize = 15.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            text = item.subtitleLabel,
            color = LanternMuted,
            fontSize = 12.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )

        if (item.resumeMs > 0) {
            Spacer(modifier = Modifier.height(6.dp))
            LinearProgressIndicator(
                progress = { (item.progressPercent / 100.0).toFloat().coerceIn(0f, 1f) },
                modifier = Modifier.fillMaxWidth().height(4.dp),
                color = LanternAccentBright,
                trackColor = LanternSurfaceAlt,
            )
        }
    }
}
