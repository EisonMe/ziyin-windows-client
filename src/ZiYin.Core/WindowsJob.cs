using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZiYin.Core;

// Kill-on-close contains descendants created after assignment. Startup has a documented race.
internal sealed class WindowsJob : IDisposable
{
    private readonly SafeFileHandle handle;
    private WindowsJob(SafeFileHandle handle) => this.handle = handle;
    public static WindowsJob Attach(Process process)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var job = new WindowsJob(handle);
        var info = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()) || !AssignProcessToJobObject(handle, process.Handle))
        { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return job;
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    { public BasicLimit BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
