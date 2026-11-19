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
        => ProcessRunner.RunAsync(
            WslManager.WslExe, WslArgs(argv, user), stdin is null ? null : ToLinuxText(stdin), onLine, WslManager.Utf8Env, ct);

    /// <summary>
    /// Ends with the app: otherwise a <c>tail -F</c> outlives every closed window, piling up
    /// <c>wsl.exe</c> processes that keep the distro attached.
    /// </summary>
    public Task<CommandResult> FollowAsync(IReadOnlyList<string> argv, Action<string> onLine, CancellationToken ct = default)
        => ProcessRunner.RunAsync(WslManager.WslExe, WslArgs(argv, HostUser.Root), null, onLine, WslManager.Utf8Env, ct, endsWithApp: true);

    private List<string> WslArgs(IReadOnlyList<string> argv, HostUser user)
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
        args.AddRange(argv.Select(ToLinuxText));
        return args;
    }

    /// <summary>
    /// Converts CRLF to LF. Scripts written as C# raw string literals take the source file's line
    /// endings, which are CRLF in a Windows checkout; sh then reads <c>set -e\r</c> and fails, and
    /// systemd reads <c>value\r</c>. Everything sent into Linux goes through here.
    /// </summary>
    public static string ToLinuxText(string text) => text.Replace("\r\n", "\n");
}
