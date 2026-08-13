using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BCSTool.Services;

/// <summary>
/// Groups the managed Bannerlord server process tree inside a Windows Job
/// Object.
///
/// The important safety feature is:
///
///     JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
///
/// This tells Windows:
///
///     "If BCS Tool itself disappears and this Job Object handle is closed,
///      terminate any server processes that are still inside the job."
///
/// Why this matters during development:
///
/// Pressing Visual Studio's Stop Debugging button can terminate BCS Tool
/// immediately without giving MainWindow a chance to run its normal
/// save/stop shutdown sequence.
///
/// Without KILL_ON_JOB_CLOSE:
///
///     BCS Tool exits
///         ↓
///     BannerlordCoopServer.exe survives in background
///         ↓
///     next F5 detects another server process
///         ↓
///     startup is blocked
///
/// With KILL_ON_JOB_CLOSE:
///
///     BCS Tool exits unexpectedly
///         ↓
///     Windows closes the Job Object handle
///         ↓
///     Windows terminates the managed server process tree
///
/// Normal BCS Tool shutdown is still graceful:
///
///     save
///       ↓
///     stop
///       ↓
///     server exits normally
///       ↓
///     application closes
///
/// So this flag is primarily an orphan-prevention safety net.
/// </summary>
internal sealed class ManagedJobObject : IDisposable
{
    private IntPtr _handle;


    /// <summary>
    /// Creates the Job Object and enables automatic cleanup when the final job
    /// handle closes.
    /// </summary>
    public ManagedJobObject()
    {
        _handle = CreateJobObject(
            IntPtr.Zero,
            null);

        if (_handle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error());
        }

        ConfigureKillOnJobClose();
    }


    /// <summary>
    /// Assigns the Bannerlord server process to this Job Object.
    ///
    /// Child processes created by the server normally remain associated with
    /// the same job, which lets Windows clean up the complete process tree.
    /// </summary>
    public bool TryAssign(System.Diagnostics.Process process)
    {
        if (_handle == IntPtr.Zero)
            return false;

        return AssignProcessToJobObject(
            _handle,
            process.Handle);
    }


    /// <summary>
    /// Emergency cleanup used by crash recovery.
    ///
    /// Normal scheduled/manual restarts still use the server's own:
    ///
    ///     save
    ///     stop
    ///
    /// commands. This force termination path is for abnormal cleanup only.
    /// </summary>
    public void Terminate(uint exitCode = 1)
    {
        if (_handle == IntPtr.Zero)
            return;

        TerminateJobObject(
            _handle,
            exitCode);
    }


    /// <summary>
    /// Enables JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
    /// </summary>
    private void ConfigureKillOnJobClose()
    {
        var information =
            new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();

        information.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        var length =
            Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();

        var pointer =
            Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(
                information,
                pointer,
                fDeleteOld: false);

            var success =
                SetInformationJobObject(
                    _handle,
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    pointer,
                    (uint)length);

            if (!success)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }


    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;

        // Closing this handle activates KILL_ON_JOB_CLOSE only if processes
        // still remain in the job.
        //
        // During a normal application shutdown the server should already have
        // exited gracefully, so there is nothing left for Windows to kill.
        CloseHandle(_handle);

        _handle = IntPtr.Zero;
    }


    // ========================================================
    // WINDOWS JOB OBJECT CONSTANTS
    // ========================================================

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE =
        0x00002000;


    // ========================================================
    // WINDOWS JOB OBJECT STRUCTURES
    // ========================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }


    private enum JOBOBJECTINFOCLASS
    {
        JobObjectExtendedLimitInformation = 9
    }


    // ========================================================
    // WINDOWS API
    // ========================================================

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr CreateJobObject(
        IntPtr lpJobAttributes,
        string? lpName);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool AssignProcessToJobObject(
        IntPtr hJob,
        IntPtr hProcess);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool TerminateJobObject(
        IntPtr hJob,
        uint uExitCode);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JOBOBJECTINFOCLASS JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);


    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool CloseHandle(
        IntPtr hObject);
}
