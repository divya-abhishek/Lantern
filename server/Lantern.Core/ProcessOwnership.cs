using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lantern.Core;

/// <summary>Owned Windows children are terminated even if their parent crashes.</summary>
public static class ProcessOwnership
{
    private static readonly Lazy<SafeFileHandle> Job = new(CreateJob);

    public static void Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (AssignProcessToJobObject(Job.Value, process.Handle)) return;
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        try { process.Kill(entireProcessTree: true); } catch { }
        throw error;
    }

    private static SafeFileHandle CreateJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>())) return handle;
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        handle.Dispose();
        throw error;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
