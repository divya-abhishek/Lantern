using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Desktop.Mvvm;
using Lantern.Desktop.Services;

namespace Lantern.Desktop.ViewModels;

/// <summary>A grid card: the DTO plus the poster the UI loads in the background.</summary>
public sealed class MediaCard : ObservableObject
{
    private ImageSource? _poster;

    public MediaCard(MediaSummaryDto dto) => Dto = dto;

    public MediaSummaryDto Dto { get; }

    public long Id => Dto.Id;
    public string Title => string.IsNullOrWhiteSpace(Dto.Title) ? "Untitled" : Dto.Title;
    public string Badge => string.IsNullOrWhiteSpace(Dto.Badge) ? "—" : Dto.Badge;
    public string YearLabel => Dto.Year?.ToString() ?? string.Empty;
    public string DurationLabel => Dto.DurationLabel;
    public string ResolutionLabel => Dto.ResolutionLabel;
    public double ProgressPercent => Dto.ProgressPercent;
    public bool HasProgress => Dto.ProgressPercent > 0.5 && !Dto.Completed;

    public string Subtitle => string.Join(" · ", new[]
    {
        YearLabel,
        Dto.Kind == MediaKind.Episode ? Dto.EpisodeCode : null,
        DurationLabel == "—" ? null : DurationLabel,
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public ImageSource? Poster
    {
        get => _poster;
        set
        {
            if (Set(ref _poster, value)) Raise(nameof(HasPoster));
        }
    }

    public bool HasPoster => _poster is not null;
}

/// <summary>
/// The single view model behind the whole Windows shell: every page is a view onto the same
/// server connection, so the connected-device state stays consistent everywhere.
/// </summary>
public sealed partial class LanternViewModel : ObservableObject, IDisposable
{
    private readonly LanternClient _client = new();
    private readonly ServerHost _host = new();
    private readonly CancellationTokenSource _lifetime = new();

    private bool _isConnected;
    private bool _isBusy;
    private bool _hasLanAddress;
    private string _serverName = "Lantern";
    private string _serverUrl = "not connected";
    private string _lanAddressLabel = "not connected";
    private string _lanAddressesLabel = string.Empty;
    private string _statusMessage = "Starting…";
    private string _errorMessage = string.Empty;

    public LanternViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ConnectCommand = new AsyncRelayCommand(parameter => ConnectAsync(parameter as string));
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ClearSearchCommand = new AsyncRelayCommand(() => { SearchText = string.Empty; return SearchAsync(); });
        OpenMediaCommand = new AsyncRelayCommand(parameter => OpenMediaAsync(parameter as MediaCard));
        PlayCommand = new AsyncRelayCommand(parameter => PlayAsync(parameter as MediaCard, resume: false));
        ResumeCommand = new AsyncRelayCommand(parameter => PlayAsync(parameter as MediaCard, resume: true));
        CopyLinkCommand = new AsyncRelayCommand(parameter => CopyLinkAsync(parameter as MediaCard));

        InitDeviceCommands();
        InitMirror();

        _ = InitializeAsync();
        _ = PollDevicesAsync();
    }

    // --- connection ---------------------------------------------------------------------

    /// <summary>
    /// The welcome line on Home: "Good morning!" from 05:00, "Good afternoon!" from 12:00, and
    /// "Good evening!" from 17:00 until 04:59 - the PC's own clock decides, so nobody is wished a
    /// good morning at midnight. The shell re-reads it every time Home is shown.
    /// </summary>
    public string Greeting { get; private set; } = GreetingFor(DateTime.Now);

    /// <summary>Recomputes the greeting. Cheap enough to call on every navigation to Home.</summary>
    public void RefreshGreeting()
    {
        var greeting = GreetingFor(DateTime.Now);
        if (greeting == Greeting) return;

        Greeting = greeting;
        Raise(nameof(Greeting));
    }

    private static string GreetingFor(DateTime now) => now.Hour switch
    {
        >= 5 and < 12 => "Good morning!",
        >= 12 and < 17 => "Good afternoon!",
        _ => "Good evening!",
    };

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!Set(ref _isConnected, value)) return;

            Raise(nameof(ConnectionLabel));
            // Screen mirroring needs a server to capture for, so the button follows the connection.
            Raise(nameof(CanMirror));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string ServerName
    {
        get => _serverName;
        private set => Set(ref _serverName, value);
    }

    public string ServerUrl
    {
        get => _serverUrl;
        private set => Set(ref _serverUrl, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    public string ConnectionLabel => IsConnected ? "Connected" : "Offline";

    /// <summary>
    /// The address another device on this LAN types to reach the server, e.g. "192.168.1.72:8787".
    /// <see cref="ServerUrl"/> is the address <em>this</em> app talks to (loopback) and is never
    /// shown as the address for the projector: 127.0.0.1 only means anything on this machine.
    /// </summary>
    public string LanAddressLabel
    {
        get => _lanAddressLabel;
        private set => Set(ref _lanAddressLabel, value);
    }

    /// <summary>Every LAN address the server announced, for PCs with more than one network.</summary>
    public string LanAddressesLabel
    {
        get => _lanAddressesLabel;
        private set => Set(ref _lanAddressesLabel, value);
    }

    public bool HasLanAddress => _hasLanAddress;

    /// <summary>Shown next to the address so nobody has to guess which one to type.</summary>
    public string LanAddressCaption => HasLanAddress
        ? "Type this address on the projector, or let it find this PC automatically."
        : "This PC is not on a network yet, so no projector can reach it.";

    public string DiscoverySummary => IsConnected
        ? $"Discovery listening · {DiscoveredServers.Count} receiver(s) online"
        : "Discovery unavailable on this network";

    public ObservableCollection<DiscoveryReceiverDto> DiscoveredServers { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand OpenMediaCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CopyLinkCommand { get; }

    private async Task InitializeAsync()
    {

        StatusMessage = "Looking for a Lantern server on this PC…";
        if (await _host.EnsureRunningAsync(_lifetime.Token).ConfigureAwait(true))
        {
            await ConnectAsync(_host.LoopbackUrl).ConfigureAwait(true);
        }
        else
        {
            StatusMessage = _host.LastError ?? "No server is running yet. Add one under Devices.";
        }
    }

    /// <summary>Attaches to a server and loads everything the shell shows.</summary>
    public async Task ConnectAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            _client.Attach(url);
            var info = await _client.GetInfoAsync(_lifetime.Token).ConfigureAwait(true);

            ServerName = info.Name;
            ServerUrl = _client.ServerUrl ?? url;
            ApplyServerAddresses(info);
            IsConnected = true;

            StatusMessage = $"Connected to {info.Name} · v{info.Version} · " +
                            (info.Ffmpeg.Available ? "ffmpeg ready" : "direct play only");

            await LoadLibraryAsync().ConfigureAwait(true);
            await LoadDevicesAsync().ConfigureAwait(true);
            await LoadFoldersAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            _client.Detach();
            _hasLanAddress = false;
            LanAddressLabel = "not connected";
            LanAddressesLabel = string.Empty;
            Raise(nameof(HasLanAddress));
            Raise(nameof(LanAddressCaption));
            ReportError($"Could not reach {url}. {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Picks the address other devices should use. The server reports the LAN addresses it found
    /// and the address of the interface the request arrived on; for this app that request came
    /// over loopback, which is useless to a projector, so it is filtered out.
    /// </summary>
    private void ApplyServerAddresses(ServerInfoDto info)
    {
        var port = info.Port > 0 ? info.Port : 8787;

        var addresses = info.Addresses
            .Where(address => !string.IsNullOrWhiteSpace(address) && !address.StartsWith("127.", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primary = info.PrimaryAddress is { Length: > 0 } candidate && !candidate.StartsWith("127.", StringComparison.Ordinal)
            ? candidate
            : addresses.FirstOrDefault();

        _hasLanAddress = primary is not null;
        LanAddressLabel = primary is null ? "No LAN address" : $"{primary}:{port}";
        LanAddressesLabel = addresses.Count <= 1
            ? string.Empty
            : string.Join("   ·   ", addresses.Select(address => $"{address}:{port}"));

        Raise(nameof(HasLanAddress));
        Raise(nameof(LanAddressCaption));
    }

    public async Task RefreshAsync()
    {
        if (!IsConnected)
        {
            await InitializeAsync().ConfigureAwait(true);
            return;
        }

        await LoadLibraryAsync().ConfigureAwait(true);
        await LoadDevicesAsync().ConfigureAwait(true);
    }

    private async Task PollDevicesAsync()
    {
        try {
            while (!_lifetime.IsCancellationRequested) {
                await Task.Delay(3000, _lifetime.Token);
                if (IsConnected) await LoadDevicesAsync();
            }
        } catch (OperationCanceledException) { }
    }
}
