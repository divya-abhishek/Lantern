using Lantern.Core.Compatibility;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Database.Repositories;
using Lantern.Media.Transcoding;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;
public sealed record PlanFailure(int Status, string Code, string Message, string? Detail = null);
public sealed record PlanOutcome(
    MediaItem Item,
    MediaFileRecord File,
    PlaybackPlan Plan,
    PlaybackPlanDto Dto,
    TranscodeSession? Session);
public sealed class PlaybackPlanner
{
    

    private readonly MediaRepository _media;
    private readonly DeviceRepository _devices;
    private readonly CompatibilityEngine _engine;
    private readonly TranscodeSessionManager _transcode;
    private readonly SettingsStore _settings;
    private readonly ILogger<PlaybackPlanner> _logger;

    public PlaybackPlanner(
        MediaRepository media,
        DeviceRepository devices,
        CompatibilityEngine engine,
        TranscodeSessionManager transcode,
        SettingsStore settings,
        ILogger<PlaybackPlanner> logger)
    {
        _media = media;
        _devices = devices;
        _engine = engine;
        _transcode = transcode;
        _settings = settings;
        _logger = logger;
    }

    public async Task<(PlanOutcome? Outcome, PlanFailure? Failure)> PlanAsync(
        PlaybackPlanRequestDto request, string? deviceId, CancellationToken ct = default, bool prepare = true)
    {
        var (item, file, failure) = await ResolveAsync(request, ct).ConfigureAwait(false);
        if (failure is not null) return (null, failure);

        if (!File.Exists(file!.Path))
        {
            return (null, new PlanFailure(
                404, "file_missing", $"\"{file.FileName}\" is no longer on disk. Rescan the library."));
        }

        var settings = _settings.Current;
        var device = await ResolveDeviceAsync(deviceId, ct).ConfigureAwait(false);

        var options = CompatibilityOptions.FromSettings(settings);
        if (request.ForceTranscode) options.AllowRemux = false;

        var plan = _engine.Evaluate(
            file, device, options, request.Quality, request.ForceTranscode, request.AudioOnly);

        if (request.ForceRemux && plan.Mode == PlaybackMode.DirectPlay)
        {
            plan.Mode = PlaybackMode.Remux;
            plan.Decision = StreamDecision.TranscodeAudio;
            plan.VideoAction = "copy";
            plan.AudioAction = "transcode";
            plan.OutputAudioCodec ??= CodecNames.Aac;
            plan.Notes.Add("Remux was forced by the client.");
        }

        if (request.AudioTrackIndex is { } trackIndex) plan.SelectedAudioStreamIndex = trackIndex;

        if (!plan.RequiresSession)
        {
            return (new PlanOutcome(item!, file, plan, DtoMapper.Plan(plan, item!.Id, null), null), null);
        }

        if (!settings.TranscodingEnabled)
        {
            return (null, new PlanFailure(
                409, "transcode_disabled",
                "This server has transcoding turned off, and this file cannot be direct played."));
        }

        if (!prepare)
        {
            var preview = DtoMapper.Plan(plan, item!.Id, null);
            preview.PlayUrl = null;
            preview.DirectStreamUrl = null;
            preview.PreparationState = "NotStarted";
            return (new PlanOutcome(item, file, plan, preview, null), null);
        }

        var startMs = request.StartSeconds is { } seconds && seconds > 0 ? (long)(seconds * 1000) : 0;
        TranscodeSession session;
        try
        {
            session = await _transcode.StartAsync(
                file,
                plan,
                settings,
                clientId: deviceId,
                title: DisplayTitle(item!),
                startMs: startMs,
                segmentSeconds: null,
                ct: ct).ConfigureAwait(false);
        }
        catch (TranscodeLimitException ex)
        {
            return (null, new PlanFailure(429, "transcode_limit", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return (null, new PlanFailure(409, "transcode_unavailable", ex.Message));
        }

        session.Touch();
        if (session.State == TranscodeState.Failed)
            return (null, new PlanFailure(409, "transcode_failed", Reason(session.Error)));

        return (new PlanOutcome(item!, file, plan, DtoMapper.Plan(plan, item!.Id, session), session), null);
    }
    private static string Reason(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "The PC's transcoder did not produce a stream.";

        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = lines.FirstOrDefault(l => l.Contains("rror", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault()
            ?? error;
        return line.Length > 220 ? line[..220] + "…" : line;
    }
    private async Task<(MediaItem? Item, MediaFileRecord? File, PlanFailure? Failure)> ResolveAsync(
        PlaybackPlanRequestDto request, CancellationToken ct)
    {
        var mediaId = request.MediaId is > 0 ? request.MediaId.Value : 0L;
        var fileId = 0L;

        if (DtoMapper.TryParseStreamId(request.StreamId, out var parsedMediaId, out var parsedFileId))
        {
            if (mediaId == 0) mediaId = parsedMediaId;
            fileId = parsedFileId;
        }

        if (mediaId <= 0)
        {
            return (null, null, new PlanFailure(400, "media_required", "Pass mediaId or streamId to plan playback."));
        }

        var item = await _media.GetItemAsync(mediaId, includeFiles: true, ct).ConfigureAwait(false);
        if (item is null)
        {
            return (null, null, new PlanFailure(404, "not_found", $"Media {mediaId} was not found."));
        }

        var file = fileId > 0 ? item.Files.FirstOrDefault(f => f.Id == fileId) : null;
        file ??= item.PrimaryFile;
        if (file is null)
        {
            return (null, null, new PlanFailure(404, "file_missing", "This item has no playable file."));
        }

        return (item, file, null);
    }
    public async Task<DeviceCapabilities> ResolveDeviceAsync(string? deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return DeviceCapabilities.Unknown();

        var device = await _devices.GetByDeviceIdAsync(deviceId, ct).ConfigureAwait(false);
        var capabilities = DtoMapper.ParseCapabilities(device?.CapabilitiesJson);
        if (capabilities is not null)
        {
            capabilities.DeviceId = deviceId;
            capabilities.Name ??= device?.Name;
            return capabilities;
        }

        if (device is null) return DeviceCapabilities.Unknown(deviceId);

        var fallback = DeviceCapabilities.Unknown(device.DeviceId);
        fallback.Name = device.Name;
        fallback.Model = device.Model;
        fallback.Manufacturer = device.Manufacturer;
        fallback.Platform = device.Platform;
        return fallback;
    }

    private static string DisplayTitle(MediaItem item) =>
        item.Kind == MediaKind.Episode && !string.IsNullOrWhiteSpace(item.EpisodeCode)
            ? $"{item.Title} ({item.EpisodeCode})"
            : item.Title;
}
