namespace OctezDesk.Core.Hosting;

public static class OctezHostExtensions
{
    /// <summary>
    /// Runs a POSIX shell script. Pass values as <paramref name="args"/> and refer to them as
    /// "$1", "$2"… in the script rather than interpolating them, so no quoting is needed.
    /// </summary>
    public static Task<CommandResult> ShAsync(
        this IOctezHost host,
        string script,
        IEnumerable<string>? args = null,
        HostUser user = HostUser.Root,
        Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var argv = new List<string> { "sh", "-c", script, "sh" };
        if (args is not null) argv.AddRange(args);
        return host.RunAsync(argv, user, onLine: onLine, ct: ct);
    }

    public static async Task<bool> FileExistsAsync(this IOctezHost host, string path, CancellationToken ct = default)
        => (await host.RunAsync(["test", "-e", path], ct: ct)).Success;

    public static async Task<bool> DirectoryExistsAsync(this IOctezHost host, string path, CancellationToken ct = default)
        => (await host.RunAsync(["test", "-d", path], ct: ct)).Success;

    public static async Task<string?> TryReadFileAsync(this IOctezHost host, string path, CancellationToken ct = default)
    {
        var r = await host.RunAsync(["cat", path], ct: ct);
        return r.Success ? r.StdOut : null;
    }

    public static async Task WriteFileAsync(this IOctezHost host, string path, string content, CancellationToken ct = default)
    {
        var r = await host.RunAsync(["sh", "-c", "cat > \"$1\"", "sh", path], stdin: content, ct: ct);
        r.EnsureSuccess($"Writing {path}");
    }

    /// <summary>
    /// Streams the last <paramref name="initialLines"/> lines of a file, then new ones, until cancelled.
    /// <c>tail -F</c> keeps waiting if the file doesn't exist yet or is rotated.
    /// </summary>
    public static Task<CommandResult> FollowFileAsync(
        this IOctezHost host, string path, Action<string> onLine, int initialLines = 500, CancellationToken ct = default)
        => host.RunAsync(["tail", "-n", initialLines.ToString(), "-F", path], onLine: onLine, ct: ct);

    /// <summary>Runs apt-get non-interactively as root.</summary>
    public static Task<CommandResult> AptGetAsync(
        this IOctezHost host, IEnumerable<string> args, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var argv = new List<string> { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "-y", "-q" };
        argv.AddRange(args);
        return host.RunAsync(argv, onLine: onLine, ct: ct);
    }
}
