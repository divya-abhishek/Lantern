// Lantern.Server - the LAN media host the Android projector and the WPF app talk to.
//
// The process is deliberately minimal: Kestrel over plain HTTP on the LAN, the SQLite
// database opened before anything else (the listening port is a persisted setting), the
// service graph from Lantern.Server/Services, and one endpoint group per API area.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lantern.Core.Api;
using Lantern.Core.Compatibility;
using Lantern.Core.Database;
using Lantern.Core.Dtos;
using Lantern.Database;
using Lantern.Database.Repositories;
using Lantern.Media.Mirroring;
using Lantern.Media.Probing;
using Lantern.Media.Scanning;
using Lantern.Media.Thumbnails;
using Lantern.Media.Tooling;
using Lantern.Media.Transcoding;
using Lantern.Server;
using Lantern.Server.Endpoints;
using Lantern.Server.Security;
using Lantern.Server.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var options = ServerOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(ServerOptions.HelpText);
    return 0;
}

// --- bootstrap ------------------------------------------------------------------------
// Kestrel cannot be configured before the settings are known, and the settings live in
// SQLite, so the database is opened first and the schema created synchronously.
var databasePath = DatabasePaths.Resolve(options.DatabasePath, DatabasePaths.DefaultDatabasePath);
var database = new SqliteConnectionFactory(databasePath);
var initializer = new DatabaseInitializer(database);
await initializer.InitializeAsync().ConfigureAwait(false);

var settings = initializer.LoadSettings();
var overridesApplied = options.Apply(settings);

// The file that was actually opened is the authoritative database path; --cache only
// overrides the cache root (thumbnails and transcode scratch space).
settings.DatabasePath = database.DatabasePath;
if (!string.IsNullOrWhiteSpace(options.CachePath))
{
    settings.CachePath = DatabasePaths.Resolve(options.CachePath, DatabasePaths.DefaultCachePath);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(settings.VerboseLogging ? LogLevel.Debug : LogLevel.Information);

// Framework request logging would drown the console while a movie is streaming.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// Warnings and errors are also kept in memory for GET /api/v1/diagnostics.
var logBuffer = new LogBuffer();
builder.Logging.AddProvider(logBuffer);
builder.Services.AddSingleton(logBuffer);

// Minimal API serialization mirrors ApiJson so /openapi, problem details and bind errors
// look like every other payload the clients parse.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.PropertyNameCaseInsensitive = true;
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    json.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    json.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
});

// Plain HTTP on the LAN: no HTTPS redirect (a self signed certificate would break the
// Android client) and no request-size ceiling worth worrying about for JSON bodies.
var bindAddress = IPAddress.TryParse(settings.BindAddress, out var parsedBind) && parsedBind is not null
    ? parsedBind
    : IPAddress.Any;

builder.WebHost.UseKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    kestrel.Listen(bindAddress, Math.Clamp(settings.Port, 1, 65535));
});

// --- services -------------------------------------------------------------------------
// Storage. The instances created during bootstrap are reused so the process only ever has
// one connection factory (and one migration run) for the database file.
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(initializer);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton(sp => new ServerIdentity(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<ILogger<ServerIdentity>>(),
    options.ServerId));
builder.Services.AddSingleton<ServerRuntime>();

builder.Services.AddSingleton<MediaRepository>();
builder.Services.AddSingleton<DeviceRepository>();
builder.Services.AddSingleton<LibraryFolderRepository>();
builder.Services.AddSingleton<ProgressRepository>();

// Media tooling: ffmpeg is resolved once (FfmpegLocator caches it) and reused by probing,
// thumbnails, subtitles and transcoding.
builder.Services.AddSingleton<FfmpegLocator>();
builder.Services.AddSingleton<FfprobeRunner>();
builder.Services.AddSingleton(sp => new ThumbnailService(
    sp.GetRequiredService<FfmpegLocator>(),
    sp.GetRequiredService<ILogger<ThumbnailService>>()));
builder.Services.AddSingleton<TranscodeSessionManager>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<CompatibilityEngine>();
builder.Services.AddSingleton<SubtitleConverter>();

// Server side use cases.
builder.Services.AddSingleton<LibraryStatsService>();
builder.Services.AddSingleton<ProgressService>();
builder.Services.AddSingleton<PlaybackPlanner>();
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddSingleton<ConnectionHub>();
builder.Services.AddSingleton<PairingService>();
builder.Services.AddSingleton<ReceiverInvitations>();
builder.Services.AddSingleton<ApiAuth>();

// Live counters the diagnostics endpoint reports: bytes/range requests per stream and the
// process CPU/memory sampler. Both are stateless wrappers, so one instance is enough.
builder.Services.AddSingleton<StreamRegistry>();
builder.Services.AddSingleton<ProcessMonitor>();

// Screen mirroring: the capability probe (which test-encodes each candidate), the single capture
// session owner and the live transport. Separate from the media pipeline on purpose - mirroring
// shares no state with Media Cast beyond the authentication and the ffmpeg binary.
builder.Services.AddSingleton<MirrorCapabilityProbe>();
builder.Services.AddSingleton<MirrorService>();

// Discovery (mDNS + UDP beacon) and the lifecycle (ffmpeg detection, startup scan,
// transcode reaper) are hosted services, but endpoints also need to inspect their state,
// so the singleton is registered first and the hosted-service slot forwards to it.
builder.Services.AddSingleton<DiscoveryHostedService>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DiscoveryHostedService>());
builder.Services.AddSingleton<ServerLifecycle>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ServerLifecycle>());

var app = builder.Build();

// Command line overrides win over the stored settings; the resolved paths are written back
// so the PC UI shows the same values the server is actually using.
var settingsStore = app.Services.GetRequiredService<SettingsStore>();
settingsStore.Replace(settings, persist: overridesApplied);
settingsStore.ApplyDefaults(persist: true);

var runtime = app.Services.GetRequiredService<ServerRuntime>();
var serverIdHeader = runtime.Identity.ServerId;

// Anything that escapes an endpoint becomes the same ApiErrorDto shape the clients parse.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;

    // A player that seeked away or closed the app is not an incident worth a red screen.
    if (error is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
    {
        context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        return;
    }

    context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Lantern.Server")
        .LogError(error, "Unhandled error for {Method} {Path}", context.Request.Method, context.Request.Path);

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(
        new ApiErrorDto
        {
            Code = "server_error",
            Message = "The server could not complete the request.",
            Detail = error?.Message,
        },
        ApiJson.Options,
        context.RequestAborted).ConfigureAwait(false);
}));

// The Android client checks this header to be sure it reached the server it paired with.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Lantern-Server"] = serverIdHeader;
    await next(context).ConfigureAwait(false);
});

// The bidirectional channel: scan progress, library changes, resume updates, errors.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

ServerEndpoints.Map(app);
MediaEndpoints.Map(app);
StreamingEndpoints.Map(app);
PlaybackEndpoints.Map(app);
LibraryEndpoints.Map(app);
PairingEndpoints.Map(app);
MirrorEndpoints.Map(app);

// Opening the address in a browser explains what the device just found.
app.MapGet("/", (ServerRuntime host) => Results.Text(
    $"""
     Lantern media server {host.Version}
     name      : {host.SettingsNow.ServerName}
     server id : {host.Identity.ServerId}
     api       : {ApiRoutes.Prefix}
     uptime    : {TimeSpan.FromSeconds(host.UptimeSeconds):d\.hh\:mm\:ss}
     """,
    "text/plain"));

app.MapGet("/healthz", (ServerRuntime host) => Results.Text(
    $"ok {host.UptimeSeconds}s", "text/plain"));

await app.StartAsync().ConfigureAwait(false);

var activeSettings = settingsStore.Current;
Console.WriteLine();
Console.WriteLine($"Lantern.Server {runtime.Version}");
Console.WriteLine($"  name     : {activeSettings.ServerName}  ({runtime.Identity.ServerId})");
Console.WriteLine($"  database : {initializer.DatabasePath}");
Console.WriteLine($"  cache    : {activeSettings.CachePath}");
Console.WriteLine($"  ffmpeg   : {FfmpegSummary(runtime.Tools)}");
Console.WriteLine($"  pairing  : {(activeSettings.RequirePairing ? "required" : "off")}");
Console.WriteLine($"  discovery: mDNS {(activeSettings.EnableMdns ? "on" : "off")}, UDP beacon {(activeSettings.EnableUdpBeacon ? "on" : "off")}");

foreach (var address in runtime.LanAddresses())
{
    Console.WriteLine($"  url      : http://{address}:{activeSettings.Port}{ApiRoutes.Prefix}");
}

Console.WriteLine($"(binding {bindAddress}:{activeSettings.Port}) - press Ctrl+C to stop");
Console.WriteLine();

await app.WaitForShutdownAsync().ConfigureAwait(false);
return 0;

static string FfmpegSummary(FfmpegTools tools) => tools switch
{
    { CanTranscode: true } => $"{tools.Version ?? "available"} ({tools.FfmpegPath})",
    { Available: false, Error: not null } => $"not found - direct play only ({tools.Error})",
    { Available: false } => "not configured - direct play only",
    _ => $"{tools.Version ?? "available"} (probing only)",
};
