using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Only the three explicitly launched lab apps enter this unnamed job. Their
// pipe-fixture children inherit it, including when a Start response is lost.
// Closing the harness kills remaining job members, never a named process group.
internal sealed class LocalPairingLabJob : IDisposable
{
    private readonly SafeFileHandle handle;

    internal LocalPairingLabJob()
    {
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the disposable lab job.");
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not contain disposable lab processes.");
        }
    }

    internal void Add(Process process)
    {
        if (AssignProcessToJobObject(handle, process.Handle)) return;
        var error = Marshal.GetLastWin32Error();
        // No API actions have been sent to this exact newly launched app yet.
        if (!process.HasExited) process.Kill();
        throw new Win32Exception(error, "The app could not enter its disposable lab job.");
    }

    internal uint ActiveCount()
    {
        if (!QueryInformationJobObject(handle, 1, out var info, Marshal.SizeOf<Accounting>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not verify lab process cleanup.");
        return info.ActiveProcesses;
    }

    // Include browser/fixture descendants and nested jobs, not just tracked apps.
    internal int[] ProcessIds()
    {
        var capacity = 64;
        while (capacity <= 4096)
        {
            var length = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.WriteInt32(buffer, 0, 0);
                Marshal.WriteInt32(buffer, 4, 0);
                var success = QueryInformationJobObject(handle, 3, buffer, length, out _);
                var error = success ? 0 : Marshal.GetLastWin32Error();
                var assigned = Marshal.ReadInt32(buffer, 0);
                var returned = Marshal.ReadInt32(buffer, 4);
                if (assigned < 0 || returned < 0 || assigned > 4096 || returned > capacity)
                    throw new InvalidOperationException("The disposable job process list is invalid.");
                if (!success && error != 234)
                    throw new Win32Exception(error, "Could not observe contained lab processes.");
                if (error == 234 || assigned > returned)
                {
                    capacity = Math.Max(assigned, checked(capacity * 2));
                    continue;
                }
                var result = new int[returned];
                for (var index = 0; index < returned; index++)
                {
                    var id = Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64();
                    if (id <= 0 || id > int.MaxValue) throw new InvalidOperationException("Invalid contained process identity.");
                    result[index] = (int)id;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("The disposable job process list exceeded its observation bound.");
    }

    internal async Task StopAsync()
    {
        if (ActiveCount() == 0) return;
        if (!TerminateJobObject(handle, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not stop the contained lab processes.");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (ActiveCount() == 0) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Disposable lab processes remain in the job; cleanup needs review.");
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting info, int length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, IntPtr info, int length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
