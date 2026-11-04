using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Wsl;

/// <summary>Runs commands inside a WSL distro via <c>wsl.exe -d &lt;distro&gt; -u &lt;user&gt; --exec …</c>.</summary>
public sealed class WslHost(string distroName) : IOctezHost
{
    public string DistroName { get; } = distroName;

    public Task<CommandResult> RunAsync(
        IReadOnlyList<string> argv,
        HostUser user = HostUser.Root,
        string? stdin = null,
        Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        // --exec bypasses the distro's login shell, so argv reaches Linux as-is
        // (Windows-side quoting is handled by ProcessStartInfo.ArgumentList).
        var args = new List<string>
        {
            "-d", DistroName,
            "-u", user == HostUser.Root ? "root" : "tezos",
            "--cd", "/",
            "--exec",
        };
        args.AddRange(argv);
        return ProcessRunner.RunAsync(WslManager.WslExe, args, stdin, onLine, WslManager.Utf8Env, ct);
    }
}
