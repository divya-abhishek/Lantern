using System.Collections.ObjectModel;
using Lantern.Core.Dtos;
using Lantern.Desktop.Services;

namespace Lantern.Desktop.ViewModels;

/// <summary>Library browsing, the detail panel and starting playback on this PC.</summary>
public sealed partial class LanternViewModel
{
    private string _searchText = string.Empty;
    private string _selectedKind = "movies";
    private string _selectedSort = "added";
    private MediaCard? _selectedCard;
    private string _detailTitle = "Nothing selected";
    private string _detailMeta = "Pick a title from the library.";
    private string _detailTechnical = string.Empty;
    private string _detailSubtitles = string.Empty;
    private string _detailResume = string.Empty;
    private long _detailDurationMs;

    public ObservableCollection<MediaCard> MediaItems { get; } = [];

    public ObservableCollection<MediaCard> ContinueWatching { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value);
    }

    /// <summary>movies | tv | all: drives the tab pills above the grid.</summary>
    public string SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (Set(ref _selectedKind, value)) _ = SearchAsync();
        }
    }

    /// <summary>added | name | year: the sort pill group.</summary>
    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (Set(ref _selectedSort, value)) _ = SearchAsync();
        }
    }

    public MediaCard? SelectedCard
    {
        get => _selectedCard;
        private set
        {
            if (Set(ref _selectedCard, value)) Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedCard is not null;

    public string DetailTitle
    {
        get => _detailTitle;
        private set => Set(ref _detailTitle, value);
    }

    public string DetailMeta
    {
        get => _detailMeta;
        private set => Set(ref _detailMeta, value);
    }

    public string DetailTechnical
    {
        get => _detailTechnical;
        private set => Set(ref _detailTechnical, value);
    }

    public string DetailSubtitles
    {
        get => _detailSubtitles;
        private set => Set(ref _detailSubtitles, value);
    }

    public string DetailResume
    {
        get => _detailResume;
        private set
        {
            if (Set(ref _detailResume, value)) Raise(nameof(HasResume));
        }
    }

    public bool HasResume => !string.IsNullOrWhiteSpace(_detailResume);

    public string LibrarySummary =>
        MediaItems.Count == 0 ? "No titles yet — add a folder under Settings." : $"{MediaItems.Count} titles";

    public bool HasFolders => Folders.Count > 0;

    /// <summary>An empty library is never a mystery: it either says "no folders" or "no matches".</summary>
    public string LibraryEmptyTitle => HasFolders
        ? "No titles match this filter yet."
        : "No media folders configured.";

    public string LibraryEmptyHint => HasFolders
        ? "Try another filter, or clear the search to see everything again."
        : "Add a folder in Settings to start your library.";

    public string LibraryEmptyAction => HasFolders ? "Clear search" : "Open Settings";

    public int TotalMediaCount { get; private set; }

    public int ContinueCount => ContinueWatching.Count;

    /// <summary>Loads the grid plus the continue-watching shelf.</summary>
    private async Task LoadLibraryAsync()
    {
        if (!IsConnected) return;

        IsBusy = true;
        try
        {
            var items = await _client
                .ListMediaAsync(SearchText, SelectedKind, SelectedSort, limit: 60, ct: _lifetime.Token)
                .ConfigureAwait(true);

            MediaItems.Clear();
            foreach (var item in items) MediaItems.Add(new MediaCard(item));

            var library = await _client.GetLibraryAsync(_lifetime.Token).ConfigureAwait(true);

            ContinueWatching.Clear();
            foreach (var resume in library.ContinueWatching) ContinueWatching.Add(new MediaCard(resume));

            TotalMediaCount = MediaItems.Count;
            Raise(nameof(LibrarySummary));
            Raise(nameof(TotalMediaCount));
            Raise(nameof(ContinueCount));

            // Posters arrive after the grid exists so the window never waits on ffmpeg.
            _ = LoadPostersAsync([.. MediaItems]);
            _ = LoadPostersAsync([.. ContinueWatching]);
        }
        catch (Exception ex)
        {
            ReportError($"The library could not be loaded. {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task SearchAsync() => LoadLibraryAsync();

    /// <summary>Fetches posters with a bounded number of parallel requests.</summary>
    private async Task LoadPostersAsync(IReadOnlyList<MediaCard> cards)
    {
        if (cards.Count == 0) return;

        using var gate = new SemaphoreSlim(4);
        var tasks = cards.Select(async card =>
        {
            try
            {
                await gate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
                card.Poster = await _client.GetThumbnailAsync(card.Id, _lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A missing poster keeps the placeholder; it is not worth an error banner.
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    /// <summary>Fills the detail panel for the selected title.</summary>
    private async Task OpenMediaAsync(MediaCard? card)
    {
        if (card is null || !IsConnected) return;

        SelectedCard = card;
        DetailTitle = card.Title;
        DetailMeta = $"{card.Subtitle} · loading details…";
        DetailTechnical = string.Empty;
        DetailSubtitles = string.Empty;
        DetailResume = string.Empty;

        try
        {
            var detail = await _client.GetMediaAsync(card.Id, plan: true, _lifetime.Token).ConfigureAwait(true);
            if (detail is null)
            {
                DetailMeta = "This title is no longer in the library. Refresh.";
                return;
            }

            _detailDurationMs = detail.DurationMs;
            DetailTitle = detail.Title;
            DetailMeta = string.Join(" · ", new[]
            {
                detail.Year?.ToString(),
                detail.DurationLabel == "—" ? null : detail.DurationLabel,
                detail.ResolutionLabel,
                detail.VideoCodec?.ToUpperInvariant(),
            }.Where(part => !string.IsNullOrWhiteSpace(part)));

            var firstAudio = detail.AudioTracks.Count > 0 ? detail.AudioTracks[0].Label : "unknown";

            DetailTechnical = string.Join(
                Environment.NewLine,
                $"File       {detail.FileName}",
                $"Container  {detail.Container?.ToUpperInvariant()}",
                $"Video      {detail.VideoCodec?.ToUpperInvariant()} {detail.ResolutionLabel}" +
                    (detail.BitrateBps is > 0 ? $" · {detail.BitrateLabel}" : string.Empty),
                $"Audio      {detail.AudioTracks.Count} track(s) · {firstAudio}",
                $"Plan       {detail.Plan?.ModeLabel ?? "Direct Play"}" +
                    (detail.Plan?.Reason is { Length: > 0 } reason ? $" — {reason}" : string.Empty));

            DetailSubtitles = detail.Subtitles.Count == 0
                ? "No subtitles found beside this file."
                : string.Join(" · ", detail.Subtitles.Select(track => track.Label));

            DetailResume = detail.PositionMs > 15_000 && !detail.Completed
                ? $"Resume from {MediaSummaryDto.FormatTime(detail.PositionMs)}"
                : string.Empty;
        }
        catch (Exception ex)
        {
            ReportError($"The details could not be loaded. {ex.Message}");
        }
    }
}
