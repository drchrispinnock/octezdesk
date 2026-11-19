using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OctezDesk.Core.Wsl;

/// <summary>
/// WSL shuts a distro down once no Windows process is attached to it, even if systemd services
/// are running. We hold a hidden <c>sleep infinity</c> open for as long as the node should run.
/// See spec/Windows.md §2.3.
/// </summary>
/// <remarks>
/// The <c>wsl.exe</c> child must not outlive this process. Velopack kills the helper outright when
/// uninstalling (before its hook runs) and after updating, which skips <see cref="Release"/>; an
/// orphaned <c>wsl.exe</c> then kept the distro attached and, because it had inherited the install
/// folder as its working directory, stopped Setup from replacing that folder. So the child runs
/// in a job object that kills it when this process's handle to the job closes, however this
/// process ends, and it starts in System32 rather than the app's folder.
/// </remarks>
public sealed class WslKeepAlive(string distroName) : IDisposable
{
    private readonly object _lock = new();
    private Process? _process;
    private KillOnCloseJob? _job;

    public bool IsRunning
    {
        get { lock (_lock) return _process is { HasExited: false }; }
    }

    /// <summary>Starts the keep-alive, or restarts it if it exited (e.g. after <c>wsl --terminate</c>).</summary>
    public void EnsureRunning()
    {
        lock (_lock)
        {
            if (_process is { HasExited: false }) return;
            _process?.Dispose();

            var psi = new ProcessStartInfo(WslManager.WslExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
            foreach (var a in new[] { "-d", distroName, "-u", "root", "--exec", "sleep", "infinity" })
                psi.ArgumentList.Add(a);
            _process = Process.Start(psi);
            if (_process is not null && OperatingSystem.IsWindows())
            {
                try { (_job ??= new KillOnCloseJob()).Add(_process); }
                catch (Win32Exception) { } // without the job, Release still kills it on a clean exit
            }
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_process is null) return;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        Release();
        lock (_lock)
        {
            if (OperatingSystem.IsWindows()) _job?.Dispose(); // only ever created on Windows
            _job = null;
        }
    }
}

/// <summary>
/// A Windows job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: processes added to it
/// (and the processes they start) are killed when the last handle closes, including when the
/// owning process is killed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private IntPtr _handle;

    public KillOnCloseJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

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
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
