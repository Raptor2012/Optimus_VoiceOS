namespace Optimus.Inference;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// A Windows job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so assigned child
/// processes die when this process does.
/// </summary>
/// <remarks>
/// Disposing <see cref="LlamaServerProcess"/> normally is not enough on its own: if the shell
/// is force-killed or crashes, managed cleanup never runs and a multi-gigabyte llama-server is
/// left resident. The kernel closes job handles on process death regardless of how the process
/// died, so this is the only cleanup that survives a kill.
/// </remarks>
internal sealed class ChildProcessJob : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public ChildProcessJob()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            return; // Job objects unavailable; fall back to managed cleanup only.
        }

        var limits = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int length = Marshal.SizeOf(limits);
        IntPtr pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(limits, pointer, fDeleteOld: false);
            NativeMethods.SetInformationJobObject(
                _handle,
                NativeMethods.JobObjectExtendedLimitInformation,
                pointer,
                (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public bool IsAvailable => _handle != IntPtr.Zero;

    /// <summary>Assigns a process to the job. Returns false if the job is unavailable.</summary>
    public bool Assign(IntPtr processHandle)
    {
        if (_handle == IntPtr.Zero || processHandle == IntPtr.Zero)
        {
            return false;
        }

        return NativeMethods.AssignProcessToJobObject(_handle, processHandle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static class NativeMethods
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
