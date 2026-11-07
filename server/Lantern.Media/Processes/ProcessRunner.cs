using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Lantern.Media.Processes;

/// <summary>Result of a finished external process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    public string Combined => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : $"{StandardOutput}\n{StandardError}";
}

/// <summary>
/// Runs ffmpeg/ffprobe without opening a console window and with correct
/// cancellation semantics (kill the whole process tree on cancel).
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        logger?.LogDebug("run: {Exe} {Args}", executable, Lantern.Media.Mirroring.MirrorCommandBuilder.FormatArguments(psi.ArgumentList));

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        try
        {
            process.Start();
        Lantern.Core.ProcessOwnership.Attach(process);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to start {Exe}", executable);
            return new ProcessResult(-1, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process, logger);
            return new ProcessResult(-1, stdout.ToString(), "cancelled");
        }

        // Flush the async readers before reading the buffers.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Starts a long-lived process (transcode session) with raw stream access.</summary>
    public static Process StartStreaming(string executable, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        Lantern.Core.ProcessOwnership.Attach(process);
        return process;
    }

    public static void TryKill(Process process, ILogger? logger = null)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not kill process {Pid}", process.Id);
        }
    }
}
