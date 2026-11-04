using System.Diagnostics;

namespace OctezDesk.Core.Wsl;

/// <summary>
/// WSL shuts a distro down once no Windows process is attached to it, even if systemd services
/// are running. We hold a hidden <c>sleep infinity</c> open for as long as the node should run.
/// See spec/Windows.md §2.3 — to be re-verified against current WSL behaviour.
/// </summary>
public sealed class WslKeepAlive(string distroName) : IDisposable
{
    private readonly object _lock = new();
    private Process? _process;

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
            };
            foreach (var a in new[] { "-d", distroName, "-u", "root", "--exec", "sleep", "infinity" })
                psi.ArgumentList.Add(a);
            _process = Process.Start(psi);
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_process is null) return;
            try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { }
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Release();
}
