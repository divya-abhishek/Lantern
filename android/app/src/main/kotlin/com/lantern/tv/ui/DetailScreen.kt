package com.lantern.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.lantern.tv.data.Api
import com.lantern.tv.data.MediaDetail

/**
 * Movie / episode details: everything the PC knows about the file, and the ways to start it.
 * The poster and the facts come straight from GET /media/{id}?plan=true.
 */
@Composable
@OptIn(ExperimentalLayoutApi::class)
fun DetailScreen(
    detail: MediaDetail,
    api: Api?,
    onPlay: (resumeMs: Long) -> Unit,
    onBack: () -> Unit,
) {
    val poster = rememberPoster(api, detail.id)
    val resumeMs = detail.summary.resumeMs
    val audio = detail.audioTracks.firstOrNull()

    val facts: @Composable () -> Unit = {
        Column(
            modifier = Modifier.fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Text(
                text = detail.summary.title,
                color = LanternText,
                fontSize = 30.sp,
                fontWeight = FontWeight.SemiBold,
            )

            Text(
                text = listOfNotNull(
                    detail.summary.year?.toString(),
                    detail.summary.durationLabel.takeIf { it != "—" },
                    detail.summary.resolutionLabel.takeIf { it.isNotBlank() },
                ).joinToString(" · "),
                color = LanternMuted,
                fontSize = 15.sp,
            )

            if (detail.summary.episodeCode != null) {
                Text(text = detail.summary.episodeCode, color = LanternAccentBright, fontSize = 14.sp)
            }

            Spacer(modifier = Modifier.height(18.dp))

            FlowRow(horizontalArrangement = Arrangement.spacedBy(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                if (resumeMs > 0) {
                    LanternButton(label = "Resume", onClick = { onPlay(resumeMs) })
                    LanternButton(label = "Start over", onClick = { onPlay(0) }, primary = false)
                } else {
                    LanternButton(label = "Play", onClick = { onPlay(0) })
                }
                LanternButton(label = "Back", onClick = onBack, primary = false)
            }

            if (resumeMs > 0) {
                Spacer(modifier = Modifier.height(6.dp))
                Text(
                    text = "Resumes at ${formatTime(resumeMs)} · ${detail.summary.progressPercent.toInt()}% watched",
                    color = LanternMuted,
                    fontSize = 13.sp,
                )
            }

            Spacer(modifier = Modifier.height(20.dp))

            SectionLabel("Sound & subtitles")
            Spacer(modifier = Modifier.height(6.dp))
            LabeledRow(
                "Audio",
                if (audio != null) {
                    if (detail.audioTracks.size == 1) "1 track" else "${detail.audioTracks.size} tracks"
                } else {
                    "No audio"
                },
            )
            LabeledRow(
                "Subtitles",
                if (detail.subtitles.isEmpty()) "None"
                else detail.subtitles.joinToString(", ") { it.label },
            )

            if (detail.description != null) {
                Spacer(modifier = Modifier.height(16.dp))
                Text(text = detail.description, color = LanternText, fontSize = 14.sp)
            }

            Spacer(modifier = Modifier.height(16.dp))
            Text(
                text = "The PC prepares compatible playback when needed.",
                color = LanternMuted,
                fontSize = 12.sp,
            )
        }
    }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        if (maxWidth < 700.dp) {
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(20.dp)) {
                facts()
                PosterFrame(bitmap = poster, modifier = Modifier.width(180.dp).height(240.dp))
            }
        } else {
            Row(Modifier.fillMaxSize().padding(36.dp), horizontalArrangement = Arrangement.spacedBy(28.dp)) {
                PosterFrame(bitmap = poster, modifier = Modifier.width(280.dp).height(400.dp))
                Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) { facts() }
            }
        }
    }
}

@Composable
private fun LabeledRow(label: String, value: String) {
    Row(modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.Top) {
        Text(text = label, color = LanternMuted, fontSize = 13.sp, modifier = Modifier.width(96.dp))
        Text(text = value, color = LanternText, fontSize = 13.sp)
    }
}

/** 01:14:32 style time, matching the resume label the PC shows. */
private fun formatTime(ms: Long): String {
    val total = ms / 1000
    val hours = total / 3600
    val minutes = (total % 3600) / 60
    val seconds = total % 60
    return if (hours > 0) {
        "%02d:%02d:%02d".format(hours, minutes, seconds)
    } else {
        "%02d:%02d".format(minutes, seconds)
    }
}
