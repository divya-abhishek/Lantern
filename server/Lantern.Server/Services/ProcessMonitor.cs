using System.Diagnostics;

namespace Lantern.Server.Services;

/// <summary>
/// Process level counters for <c>GET /api/v1/diagnostics</c>. CPU usage is derived from
/// the delta between two samples, which is what the PC UI wants to plot.
/// </summary>
public sealed class ProcessMonitor
{
    private readonly object _gate = new();
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private TimeSpan _lastCpu;
    private double _cpuPercent;

    public double CpuPercent
    {
        get
        {
            lock (_gate) return _cpuPercent;
        }
    }

    public long ProcessMemoryMb => WorkingSetBytes() / (1024 * 1024);

    public long ManagedMemoryMb => GC.GetTotalMemory(false) / (1024 * 1024);

    public int ThreadCount
    {
        get
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.Threads.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>Recomputes the CPU usage since the previous sample (0..100, all cores).</summary>
    public double Sample()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var now = DateTime.UtcNow;

            lock (_gate)
            {
                var wall = now - _lastSampleUtc;
                if (wall.TotalMilliseconds >= 100)
                {
                    var used = (cpu - _lastCpu).TotalMilliseconds;
                    _cpuPercent = Math.Clamp(
                        used / (wall.TotalMilliseconds * Environment.ProcessorCount) * 100, 0, 100);
                    _lastSampleUtc = now;
                    _lastCpu = cpu;
                }

                return _cpuPercent;
            }
        }
        catch (Exception)
        {
            return _cpuPercent;
        }
    }

    private static long WorkingSetBytes()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.WorkingSet64;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
