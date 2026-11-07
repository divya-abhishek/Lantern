using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Database.Repositories;

namespace Lantern.Server.Services;

/// <summary>
/// Resume positions: the one place that turns a client progress report into the persisted
/// record and back into the DTOs, shared by the REST endpoint and the WebSocket hub.
/// </summary>
public sealed class ProgressService
{
    private readonly ProgressRepository _progress;
    private readonly MediaRepository _media;
    private readonly SettingsStore _settings;

    public ProgressService(ProgressRepository progress, MediaRepository media, SettingsStore settings)
    {
        _progress = progress;
        _media = media;
        _settings = settings;
    }

    /// <summary>Stores a report; null when the media item is unknown.</summary>
    public async Task<ProgressAckDto?> ReportAsync(
        ProgressReportDto report, string? clientId, CancellationToken ct = default)
    {
        if (report.MediaId <= 0) return null;

        var item = await _media.GetItemAsync(report.MediaId, includeFiles: true, ct).ConfigureAwait(false);
        if (item is null) return null;

        var settings = _settings.Current;
        var client = string.IsNullOrWhiteSpace(clientId) ? report.ClientId ?? string.Empty : clientId!;
        var duration = report.DurationMs > 0 ? report.DurationMs : item.PrimaryFile?.DurationMs ?? 0;

        if (report.Reset)
        {
            await _progress.ResetAsync(item.Id, client, ct).ConfigureAwait(false);
            return new ProgressAckDto
            {
                MediaId = item.Id,
                PositionMs = 0,
                Percent = 0,
                Completed = false,
                UpdatedUtc = DateTime.UtcNow,
                AudioMode = AudioMode.Projector,
            };
        }

        var position = Math.Clamp(report.PositionMs, 0, duration > 0 ? duration : report.PositionMs);
        var percent = duration > 0 ? Math.Clamp(position * 100.0 / duration, 0, 100) : 0;
        var completed = report.Completed
            ?? (report.State == PlaybackState.Ended
                || (duration > 0 && percent >= settings.CompletedThresholdPercent));

        var record = new PlaybackProgressRecord
        {
            MediaId = item.Id,
            ClientId = client,
            PositionMs = completed && duration > 0 ? duration : position,
            DurationMs = duration,
            State = report.State,
            PlaybackRate = report.PlaybackRate,
            Completed = completed,
            ExplicitReset = report.Reset ? true : null,
        };

        record = await _progress.SaveAsync(record, ct).ConfigureAwait(false);

        var mode = report.AudioMode == AudioMode.Pc && settings.PcAudioEnabled
            ? AudioMode.Pc
            : AudioMode.Projector;

        return new ProgressAckDto
        {
            MediaId = item.Id,
            PositionMs = record.PositionMs,
            Percent = record.Percent,
            Completed = record.Completed,
            UpdatedUtc = record.UpdatedUtc,
            AudioMode = mode,
            PcAudioOffsetMs = mode == AudioMode.Pc ? settings.PcAudioOffsetMs : 0,
        };
    }

    /// <summary>The "continue watching" shelf for one client.</summary>
    public async Task<List<ResumeDto>> ContinueAsync(
        string? clientId, int limit = 20, CancellationToken ct = default)
    {
        var client = clientId ?? string.Empty;
        var items = await _media
            .GetContinueWatchingAsync(client, Math.Clamp(limit, 1, 100), ct)
            .ConfigureAwait(false);

        var shelf = new List<ResumeDto>(items.Count);
        foreach (var item in items)
        {
            var record = await _progress.GetAsync(item.Id, client, ct).ConfigureAwait(false);
            var duration = record?.DurationMs ?? 0;

            if (duration <= 0)
            {
                var withFiles = item.Files.Count > 0 ? item : await _media.GetItemAsync(item.Id, true, ct)
                    .ConfigureAwait(false);
                duration = withFiles?.PrimaryFile?.DurationMs ?? 0;
            }

            shelf.Add(new ResumeDto
            {
                MediaId = item.Id,
                PositionMs = record?.PositionMs ?? 0,
                DurationMs = duration,
                Percent = record?.Percent ?? 0,
                Completed = record?.Completed ?? false,
                UpdatedUtc = record?.UpdatedUtc,
                MediaTitle = item.Title,
                ThumbnailUrl = ApiRoutes.MediaThumbnail(item.Id),
            });
        }

        return shelf;
    }
}
