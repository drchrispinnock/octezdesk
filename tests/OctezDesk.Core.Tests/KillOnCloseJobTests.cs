using System.Diagnostics;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Core.Tests;

public class KillOnCloseJobTests
{
    /// <summary>
    /// Closing the job's last handle (as when the keep-alive helper is killed) ends the processes in
    /// it, including ones they started, which is what stops an orphaned wsl.exe.
    /// </summary>
    [Fact]
    public void Closing_the_job_kills_the_process_tree()
    {
        if (!OperatingSystem.IsWindows()) return;
        // cmd starts ping, so the tree has a grandchild, like wsl.exe starting its own wsl.exe.
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 600 127.0.0.1 > nul")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory,
        };
        using var parent = Process.Start(psi)!;
        var job = new KillOnCloseJob();
        job.Add(parent);
        Thread.Sleep(500); // let cmd start ping inside the job
        var pings = Process.GetProcessesByName("PING").Where(p => p.StartTime >= parent.StartTime).ToList();

        job.Dispose();

        Assert.True(parent.WaitForExit(5000), "the job's process should be killed");
        foreach (var p in pings) Assert.True(p.WaitForExit(5000), "the process it started should be killed too");
    }
}
