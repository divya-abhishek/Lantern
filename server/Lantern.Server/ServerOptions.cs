using Lantern.Core.Models;

namespace Lantern.Server;

/// <summary>
/// Command line surface of <c>Lantern.Server</c>. Everything is optional: with no
/// arguments the server uses the persisted settings and the default data directory.
/// </summary>
public sealed class ServerOptions
{
    public int? Port { get; set; }
    public string? BindAddress { get; set; }
    public string? DatabasePath { get; set; }
    public string? CachePath { get; set; }
    public string? ServerName { get; set; }
    public string? ServerId { get; set; }
    public string? FfmpegPath { get; set; }
    public bool? EnableMdns { get; set; }
    public bool? EnableUdpBeacon { get; set; }
    public bool? RequirePairing { get; set; }
    public bool? ScanOnStartup { get; set; }
    public bool? TranscodingEnabled { get; set; }
    public bool? Verbose { get; set; }
    public bool ShowHelp { get; set; }

    public static ServerOptions Parse(string[] args)
    {
        var options = new ServerOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var key = raw.Trim();
            string? inline = null;
            var eq = key.IndexOf('=');
            if (eq > 0)
            {
                inline = key[(eq + 1)..];
                key = key[..eq];
            }

            string? Take() => inline ?? (i + 1 < args.Length ? args[++i] : null);

            switch (key.ToLowerInvariant())
            {
                case "-p" or "--port":
                    if (int.TryParse(Take(), out var port) && port is > 0 and < 65536) options.Port = port;
                    break;
                case "-b" or "--bind":
                    options.BindAddress = Take();
                    break;
                case "-d" or "--db" or "--database":
                    options.DatabasePath = Take();
                    break;
                case "-c" or "--cache":
                    options.CachePath = Take();
                    break;
                case "-n" or "--name":
                    options.ServerName = Take();
                    break;
                case "--id":
                    options.ServerId = Take();
                    break;
                case "--ffmpeg":
                    options.FfmpegPath = Take();
                    break;
                case "--no-mdns":
                    options.EnableMdns = false;
                    break;
                case "--no-beacon":
                    options.EnableUdpBeacon = false;
                    break;
                case "--no-discovery":
                    options.EnableMdns = false;
                    options.EnableUdpBeacon = false;
                    break;
                case "--no-scan":
                    options.ScanOnStartup = false;
                    break;
                case "--no-pairing":
                    options.RequirePairing = false;
                    break;
                case "--no-transcode":
                    options.TranscodingEnabled = false;
                    break;
                case "-v" or "--verbose":
                    options.Verbose = true;
                    break;
                case "-h" or "--help" or "-?":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    /// <summary>Applies the overrides onto the persisted settings object.</summary>
    public bool Apply(LanternSettings settings)
    {
        var changed = false;

        if (Port is { } port && settings.Port != port) { settings.Port = port; changed = true; }
        if (!string.IsNullOrWhiteSpace(BindAddress) && settings.BindAddress != BindAddress) { settings.BindAddress = BindAddress; changed = true; }
        if (!string.IsNullOrWhiteSpace(DatabasePath) && settings.DatabasePath != DatabasePath) { settings.DatabasePath = DatabasePath; changed = true; }
        if (!string.IsNullOrWhiteSpace(CachePath) && settings.CachePath != CachePath) { settings.CachePath = CachePath; changed = true; }
        if (!string.IsNullOrWhiteSpace(ServerName) && settings.ServerName != ServerName) { settings.ServerName = ServerName; changed = true; }
        if (!string.IsNullOrWhiteSpace(FfmpegPath) && settings.FfmpegPath != FfmpegPath) { settings.FfmpegPath = FfmpegPath; changed = true; }
        if (EnableMdns is { } mdns && settings.EnableMdns != mdns) { settings.EnableMdns = mdns; changed = true; }
        if (EnableUdpBeacon is { } beacon && settings.EnableUdpBeacon != beacon) { settings.EnableUdpBeacon = beacon; changed = true; }
        if (RequirePairing is { } pairing && settings.RequirePairing != pairing) { settings.RequirePairing = pairing; changed = true; }
        if (ScanOnStartup is { } scan && settings.ScanOnStartup != scan) { settings.ScanOnStartup = scan; changed = true; }
        if (TranscodingEnabled is { } transcode && settings.TranscodingEnabled != transcode) { settings.TranscodingEnabled = transcode; changed = true; }
        if (Verbose is { } verbose && settings.VerboseLogging != verbose) { settings.VerboseLogging = verbose; changed = true; }

        return changed;
    }

    public const string HelpText = """
        Lantern media server

          Lantern.Server [options]

          -p, --port <port>      HTTP port to listen on            (default 8787)
          -b, --bind <address>   Interface to bind, 0.0.0.0 = LAN  (default 0.0.0.0)
          -d, --db <path>        SQLite database file
          -c, --cache <path>     Thumbnail/transcode cache folder
          -n, --name <name>      Friendly name shown to phones
              --id <id>          Force the server id (defaults to a stored guid)
              --ffmpeg <path>    Folder containing ffmpeg.exe/ffprobe.exe
              --no-mdns          Do not answer mDNS queries
              --no-beacon        Do not broadcast UDP beacons
              --no-discovery     Disable both discovery transports
              --no-scan          Do not scan the library at startup
              --no-pairing       Accept any client without pairing
              --no-transcode     Disable media preparation (direct play only)
          -v, --verbose          Log debug detail
          -h, --help             Show this text
        """;
}
