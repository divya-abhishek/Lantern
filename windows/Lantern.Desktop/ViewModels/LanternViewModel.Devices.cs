using System.Collections.ObjectModel;
using System.Windows.Input;
using Lantern.Core.Dtos;
using Lantern.Desktop.Mvvm;
using Lantern.Desktop.Services;

namespace Lantern.Desktop.ViewModels;

/// <summary>Devices, the PIN pairing flow and the library folders.</summary>
public sealed partial class LanternViewModel
{
    private string _newServerUrl = string.Empty;
    private string _pairingCodeInput = string.Empty;
    private string _pairingMessage = string.Empty;
    private string _newFolderPath = string.Empty;
    private string _folderMessage = string.Empty;
    private string _scanMessage = string.Empty;
    private bool _isScanning;
    private TrustedDeviceDto? _selectedDevice;

    public ObservableCollection<TrustedDeviceDto> TrustedDevices { get; } = [];

    public ObservableCollection<PendingPairing> PendingPairings { get; } = [];

    public ObservableCollection<FolderDto> Folders { get; } = [];

    /// <summary>Manual fallback when discovery cannot see the server (blocked multicast, VLAN…).</summary>
    public string NewServerUrl
    {
        get => _newServerUrl;
        set => Set(ref _newServerUrl, value);
    }

    public string PairingCodeInput
    {
        get => _pairingCodeInput;
        set => Set(ref _pairingCodeInput, value);
    }

    public string PairingMessage
    {
        get => _pairingMessage;
        private set => Set(ref _pairingMessage, value);
    }

    public string NewFolderPath
    {
        get => _newFolderPath;
        set => Set(ref _newFolderPath, value);
    }

    public string FolderMessage
    {
        get => _folderMessage;
        private set => Set(ref _folderMessage, value);
    }

    /// <summary>The result of the last scan ("Scan complete - 12 files found"), or the reason it failed.</summary>
    public string ScanMessage
    {
        get => _scanMessage;
        private set => Set(ref _scanMessage, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;

            Raise(nameof(ScanButtonLabel));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>"Scan now" while idle, "Scanning…" while a scan is running (the button is disabled then).</summary>
    public string ScanButtonLabel => IsScanning ? "Scanning…" : "Scan now";

    public TrustedDeviceDto? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

    public string DevicesSummary => TrustedDevices.Count switch
    {
        0 => "No device has been paired with this PC yet.",
        1 => "1 paired device",
        _ => $"{TrustedDevices.Count} paired devices",
    };

    public string FoldersSummary => Folders.Count switch
    {
        0 => "No folders are being watched yet.",
        1 => "1 folder watched",
        _ => $"{Folders.Count} folders watched",
    };

    public string PendingSummary => PendingPairings.Count == 0
        ? "No projector is waiting for a code right now."
        : $"{PendingPairings.Count} waiting: {string.Join(", ", PendingPairings.Select(p => p.Name))}";

    public ICommand ApprovePairingCommand { get; private set; } = null!;
    public ICommand ConnectReceiverCommand { get; private set; } = null!;
    public ICommand RevokeDeviceCommand { get; private set; } = null!;
    public ICommand BrowseFolderCommand { get; private set; } = null!;
    public ICommand AddFolderCommand { get; private set; } = null!;
    public ICommand RemoveFolderCommand { get; private set; } = null!;
    public ICommand ScanCommand { get; private set; } = null!;

    private void InitDeviceCommands()
    {
        ApprovePairingCommand = new AsyncRelayCommand(ApprovePairingAsync);
        ConnectReceiverCommand = new AsyncRelayCommand(async parameter => {
            if (parameter is not DiscoveryReceiverDto receiver) return;
            try {
                await _client.InviteReceiverAsync(receiver.DeviceId, _lifetime.Token);
                PairingMessage = $"Select Yes on {receiver.Name} to connect. Request expires in one minute.";
            } catch (Exception ex) { PairingMessage = $"Cannot connect to {receiver.Name}: {ex.Message}"; }
        });
        RevokeDeviceCommand = new AsyncRelayCommand(parameter => RevokeDeviceAsync(parameter as TrustedDeviceDto));
        BrowseFolderCommand = new AsyncRelayCommand(BrowseForFolderAsync);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync);
        RemoveFolderCommand = new AsyncRelayCommand(parameter => RemoveFolderAsync(parameter as FolderDto));
        ScanCommand = new AsyncRelayCommand(() => ScanLibraryAsync(), () => !IsScanning);
    }

    private async Task LoadDevicesAsync()
    {
        if (!IsConnected) return;

        try
        {
            var devices = await _client.GetDevicesAsync(_lifetime.Token).ConfigureAwait(true);
            TrustedDevices.Clear();
            foreach (var device in devices) TrustedDevices.Add(device);
            var receivers = await _client.GetReceiversAsync(_lifetime.Token).ConfigureAwait(true);
            foreach (var receiver in receivers)
            {
                receiver.Paired = devices.Any(d => d.DeviceId == receiver.DeviceId);
            }
            // Keep buttons in place across discovery refreshes so they retain input focus.
            if (!DiscoveredServers.Select(ReceiverRow).SequenceEqual(receivers.Select(ReceiverRow)))
            {
                DiscoveredServers.Clear();
                foreach (var receiver in receivers) DiscoveredServers.Add(receiver);
                Raise(nameof(DiscoverySummary));
            }
            else
            {
                for (var i = 0; i < receivers.Count; i++)
                    DiscoveredServers[i].LastSeenUtc = receivers[i].LastSeenUtc;
            }


            var pending = await _client.GetPairingRequestsAsync(_lifetime.Token).ConfigureAwait(true);
            PendingPairings.Clear();
            foreach (var request in pending) PendingPairings.Add(request);

            Raise(nameof(DevicesSummary));
            Raise(nameof(PendingSummary));

            if (pending.Count > 0)
            {
                if (!PairingMessage.StartsWith("Select Yes"))
                    PairingMessage = $"{pending[0].Name} is waiting — enter the code displayed on that receiver.";
            }
        }
        catch (Exception ex)
        {
            ReportError($"The device list could not be loaded. {ex.Message}");
        }
    }

    private static (string Id, string Name, string Summary, bool SupportsConnect) ReceiverRow(DiscoveryReceiverDto receiver) =>
        (receiver.DeviceId, receiver.Name, receiver.Summary, receiver.SupportsConnect);

    /// <summary>
    /// Pairs the projector that displays <see cref="PairingCodeInput"/>: the code shown on the TV
    /// IS the pairing request id, so the PC approving it by id is the whole handshake.
    /// </summary>
    private async Task ApprovePairingAsync()
    {
        if (!IsConnected) return;

        var pin = PairingCodeInput.Trim();
        if (pin.Length != 4 || !pin.All(char.IsDigit))
        {
            PairingMessage = "Type the 4-digit code shown on the projector.";
            return;
        }

        try
        {
            // Refresh first so a device that just started waiting is never missed.
            await LoadDevicesAsync().ConfigureAwait(true);

            var match = PendingPairings.FirstOrDefault(request =>
                string.Equals(request.RequestId, pin, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                PairingMessage = $"No device is showing the code {pin}. Codes expire after 2 minutes — " +
                                 "ask the projector for a new one.";
                return;
            }

            await _client.ApprovePairingAsync(match.RequestId, trust: true, _lifetime.Token).ConfigureAwait(true);
            PairingCodeInput = string.Empty;
            PairingMessage = $"{match.Name} is paired. It will reconnect on its own from now on.";
            StatusMessage = $"Paired with {match.Name}";
            await LoadDevicesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PairingMessage = $"Pairing failed: {ex.Message}";
        }
    }

    /// <summary>Removes a trusted device: the projector then needs a new PIN.</summary>
    private async Task RevokeDeviceAsync(TrustedDeviceDto? device)
    {
        if (device is null || !IsConnected) return;

        try
        {
            await _client.RevokeDeviceAsync(device.Id, _lifetime.Token).ConfigureAwait(true);
            StatusMessage = $"{device.Name} can no longer connect without pairing again.";
            await LoadDevicesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportError($"The device could not be removed. {ex.Message}");
        }
    }

    private async Task LoadFoldersAsync()
    {
        if (!IsConnected) return;

        try
        {
            var folders = await _client.GetFoldersAsync(_lifetime.Token).ConfigureAwait(true);
            Folders.Clear();
            foreach (var folder in folders) Folders.Add(folder);

            Raise(nameof(FoldersSummary));
            Raise(nameof(HasFolders));
            Raise(nameof(LibraryEmptyTitle));
            Raise(nameof(LibraryEmptyHint));
            Raise(nameof(LibraryEmptyAction));
        }
        catch (Exception ex)
        {
            ReportError($"The folders could not be loaded. {ex.Message}");
        }
    }

    private async Task AddFolderAsync()
    {
        if (!IsConnected)
        {
            FolderMessage = "Not connected to a server yet, so there is no library to add a folder to.";
            return;
        }

        // Nothing typed yet? Ask Windows for a folder instead of making the user type a path.
        var path = NewFolderPath.Trim();
        if (path.Length == 0 && !TryPickFolder(out path)) return;

        await AddFolderPathAsync(path).ConfigureAwait(true);
    }

    /// <summary>"Browse…": always opens the picker, then adds whatever was chosen.</summary>
    private async Task BrowseForFolderAsync()
    {
        if (!IsConnected)
        {
            FolderMessage = "Not connected to a server yet, so there is no library to add a folder to.";
            return;
        }

        if (!TryPickFolder(out var path)) return;   // cancelled: no error, nothing changed

        NewFolderPath = path;
        await AddFolderPathAsync(path).ConfigureAwait(true);
    }

    private async Task AddFolderPathAsync(string path)
    {
        var wanted = Normalise(path);

        // A folder can only be watched once: never create a second entry for the same path.
        if (Folders.Any(folder => string.Equals(Normalise(folder.Path), wanted, StringComparison.OrdinalIgnoreCase)))
        {
            NewFolderPath = string.Empty;
            FolderMessage = $"{wanted} is already in the library.";
            return;
        }

        try
        {
            var folder = await _client.AddFolderAsync(wanted, scanNow: false, _lifetime.Token).ConfigureAwait(true);
            NewFolderPath = string.Empty;
            FolderMessage = $"Watching {folder.Path}. Scanning it now…";

            await LoadFoldersAsync().ConfigureAwait(true);
            await ScanLibraryAsync(folder.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FolderMessage = $"That folder could not be added: {ex.Message}";
        }
    }

    private async Task RemoveFolderAsync(FolderDto? folder)
    {
        if (folder is null || !IsConnected) return;

        try
        {
            await _client.DeleteFolderAsync(folder.Id, _lifetime.Token).ConfigureAwait(true);
            FolderMessage = $"Removed {folder.Name}. Its titles are gone from the library.";

            await LoadFoldersAsync().ConfigureAwait(true);
            await LoadLibraryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FolderMessage = $"That folder could not be removed: {ex.Message}";
        }
    }

    /// <summary>The native Windows folder picker. False when the user cancels the dialog.</summary>
    private bool TryPickFolder(out string path)
    {
        path = string.Empty;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Add a media folder to Lantern",
            Multiselect = false,
        };

        if (Folders.FirstOrDefault() is { } existing && System.IO.Directory.Exists(existing.Path))
        {
            dialog.InitialDirectory = existing.Path;
        }

        if (dialog.ShowDialog() != true) return false;

        path = dialog.FolderName;
        return !string.IsNullOrWhiteSpace(path);
    }

    /// <summary>Full, separator-normalised path so "D:\Media" and "d:\media\" are one folder.</summary>
    private static string Normalise(string path)
    {
        var trimmed = path.Trim();

        try
        {
            return System.IO.Path.GetFullPath(trimmed).TrimEnd('\\', '/');
        }
        catch (Exception)
        {
            return trimmed.TrimEnd('\\', '/');
        }
    }

    /// <summary>Runs a real scan: the button disables, the result is reported and the UI refreshes.</summary>
    private async Task ScanLibraryAsync(long? folderId = null)
    {
        if (!IsConnected)
        {
            ScanMessage = "Not connected to a server yet, so there is nothing to scan.";
            return;
        }

        if (IsScanning) return;

        IsScanning = true;
        ScanMessage = "Scanning…";

        try
        {
            var report = await _client.ScanAsync(folderId, _lifetime.Token).ConfigureAwait(true);

            ScanMessage = report.FilesSeen == 0
                ? "Scan complete — no media files found in the folders being watched."
                : $"Scan complete — {report.FilesSeen} file(s) found" +
                  (report.ItemsAdded > 0 ? $", {report.ItemsAdded} new title(s) added." : ".");

            if (report.Errors > 0) ScanMessage += $" {report.Errors} file(s) could not be read.";

            StatusMessage = ScanMessage;
            FolderMessage = string.Empty;

            await LoadFoldersAsync().ConfigureAwait(true);
            await LoadLibraryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ScanMessage = $"The scan failed: {ex.Message}";
            ReportError(ScanMessage);
        }
        finally
        {
            IsScanning = false;
        }
    }
}
