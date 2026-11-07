using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace Lantern.Desktop.Services;

/// <summary>
/// Owns the local Lantern server process. The app first looks for a server already listening on
/// the loopback address and only starts one when nothing answers, so opening the app twice never
/// produces two servers fighting over the database.
/// </summary>
public sealed class ServerHost : IDisposable
{
    private static readonly string[] RelativeServerPaths =
    [
        "Lantern.Server.exe",
        Path.Combine("server", "Lantern.Server.exe"),
        Path.Combine("..", "..", "..", "..", "..", "server", "Lantern.Server", "bin", "Debug", "net10.0", "Lantern.Server.exe"),
        Path.Combine("..", "..", "..", "..", "server", "Lantern.Server", "bin", "Debug", "net10.0", "Lantern.Server.exe"),
    ];

    private static readonly string[] RelativeServerDlls =
    [
        "Lantern.Server.dll",
        Path.Combine("server", "Lantern.Server.dll"),
        Path.Combine("..", "..", "..", "..", "..", "server", "Lantern.Server", "bin", "Debug", "net10.0", "Lantern.Server.dll"),
        Path.Combine("..", "..", "..", "..", "server", "Lantern.Server", "bin", "Debug", "net10.0", "Lantern.Server.dll"),
    ];

    private Process? _process;

    public int Port { get; init; } = 8787;

    public string LoopbackUrl => $"http://127.0.0.1:{Port}";

    public bool StartedByThisApp => _process is { HasExited: false };

    public string? LastError { get; private set; }

    /// <summary>Ensures a server answers on the loopback address, starting one when needed.</summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (await IsUpAsync(ct).ConfigureAwait(false)) return true;

        if (!TryStart(out var error))
        {
            LastError = error;
            return false;
        }

        // A cold start opens SQLite, creates the schema and starts discovery; give it time.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await IsUpAsync(ct).ConfigureAwait(false)) return true;
            if (_process is { HasExited: true }) break;
        }

        LastError = "The Lantern server did not start. Keep the complete Lantern folder together and check that port 8787 is available.";
        return false;
    }

    public async Task<bool> IsUpAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"{LoopbackUrl}/healthz", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryStart(out string error)
    {
        error = string.Empty;

        var startInfo = BuildStartInfo();
        if (startInfo is null)
        {
            error = "Lantern.Server was not found next to this app. Install it or run the server manually.";
            return false;
        }

        try
        {
            _process = Process.Start(startInfo);
            if (_process is null)
            {
                error = "The Lantern server process could not be started.";
                return false;
            }

            Lantern.Core.ProcessOwnership.Attach(_process);
            return true;
        }
        catch (Exception ex)
        {
            error = $"The Lantern server could not be started: {ex.Message}";
            return false;
        }
    }

    private ProcessStartInfo? BuildStartInfo()
    {
        // The .exe apphost is preferred because it needs no dotnet on PATH.
        foreach (var relative in RelativeServerPaths)
        {
            var path = Resolve(relative);
            if (path is null) continue;

            var exe = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            exe.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            return exe;
        }

        foreach (var relative in RelativeServerDlls)
        {
            var path = Resolve(relative);
            if (path is null) continue;

            var dll = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            dll.ArgumentList.Add(path);
            dll.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            return dll;
        }

        return null;
    }

    private static string? Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
        return File.Exists(full) ? full : null;
    }

    public void Dispose()
    {
        // Only the process this app started is stopped; a server the user launched stays up.
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // Shutting down is best effort.
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }
}
