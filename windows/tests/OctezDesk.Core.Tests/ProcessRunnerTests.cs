using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Tests;

public class ProcessRunnerTests
{
    /// <summary>
    /// A child that inherited the app's folder as its working directory stopped Velopack replacing
    /// that folder while it ran (orphaned log followers blocked the 0.2.4 update), so none do.
    /// </summary>
    [Fact]
    public async Task Children_never_start_in_the_apps_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var r = await ProcessRunner.RunAsync("cmd.exe", ["/c", "cd"]);
        Assert.Equal(Environment.SystemDirectory, r.StdOut.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task A_child_that_ends_with_the_app_still_runs_and_reports()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        var r = await ProcessRunner.RunAsync("cmd.exe", ["/c", "echo following"], onLine: lines.Add, endsWithApp: true);
        Assert.True(r.Success);
        Assert.Equal(["following"], lines);
    }
}
