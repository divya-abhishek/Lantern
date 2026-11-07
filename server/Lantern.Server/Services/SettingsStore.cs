using Lantern.Core.Database;
using Lantern.Core.Models;
using Lantern.Database;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;

/// <summary>
/// Single owner of <see cref="LanternSettings"/> for the running server. Reads happen
/// constantly (every request) so <see cref="Current"/> hands out the live instance;
/// writes go through <see cref="Update"/> which persists to SQLite atomically.
/// </summary>
public sealed class SettingsStore
{
    private readonly DatabaseInitializer _initializer;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _gate = new();

    public SettingsStore(DatabaseInitializer initializer, ILogger<SettingsStore> logger)
    {
        _initializer = initializer;
        _logger = logger;
        Current = initializer.LoadSettings();
    }

    /// <summary>Live settings instance. Treat as read-only outside <see cref="Update"/>.</summary>
    public LanternSettings Current { get; private set; }

    /// <summary>Fills in the path defaults the rest of the server relies on.</summary>
    public void ApplyDefaults(bool persist)
    {
        lock (_gate)
        {
            var settings = Current;
            settings.DatabasePath = DatabasePaths.Resolve(settings.DatabasePath, DatabasePaths.DefaultDatabasePath);
            settings.CachePath = DatabasePaths.Resolve(settings.CachePath, DatabasePaths.DefaultCachePath);
            if (persist) _initializer.SaveSettings(settings);
        }
    }

    /// <summary>Replaces the whole instance (used by command line overrides).</summary>
    public void Replace(LanternSettings settings, bool persist = true)
    {
        lock (_gate)
        {
            Current = settings;
            if (persist) _initializer.SaveSettings(settings);
        }
    }

    public void Update(Action<LanternSettings> mutate, bool persist = true)
    {
        lock (_gate)
        {
            mutate(Current);
            if (persist)
            {
                _initializer.SaveSettings(Current);
                _logger.LogDebug("Settings persisted");
            }
        }
    }

    /// <summary>Remembers the last throughput measured against a client (diagnostics + quality ladder).</summary>
    public void SetMeasuredThroughput(double megabitsPerSecond, bool persist = false)
    {
        lock (_gate)
        {
            Current.LastMeasuredThroughputMbps = megabitsPerSecond;
            if (persist) _initializer.SaveSettings(Current);
        }
    }
}
