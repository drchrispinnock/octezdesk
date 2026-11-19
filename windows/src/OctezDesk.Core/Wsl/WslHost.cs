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
        args.AddRange(argv.Select(ToLinuxText));
        return ProcessRunner.RunAsync(
            WslManager.WslExe, args, stdin is null ? null : ToLinuxText(stdin), onLine, WslManager.Utf8Env, ct);
    }

    /// <summary>
    /// Converts CRLF to LF. Scripts written as C# raw string literals take the source file's line
    /// endings, which are CRLF in a Windows checkout; sh then reads <c>set -e\r</c> and fails, and
    /// systemd reads <c>value\r</c>. Everything sent into Linux goes through here.
    /// </summary>
    public static string ToLinuxText(string text) => text.Replace("\r\n", "\n");
}
