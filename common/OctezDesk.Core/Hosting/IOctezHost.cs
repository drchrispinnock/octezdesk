namespace OctezDesk.Core.Hosting;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public CommandResult EnsureSuccess(string what)
    {
        if (!Success)
        {
            // Some tools (wsl.exe included) report errors on stdout.
            var detail = StdErr.Trim().Length > 0 ? StdErr.Trim() : StdOut.Trim();
            throw new HostCommandException($"{what} failed (exit {ExitCode}): {detail}", this);
        }
        return this;
    }
}

public sealed class HostCommandException(string message, CommandResult result) : Exception(message)
{
    public CommandResult Result { get; } = result;
}

/// <summary>The Linux account a command runs as.</summary>
public enum HostUser
{
    Root,
    /// <summary>The service account created by the tzinit packages.</summary>
    Tezos,
}

/// <summary>
/// A Linux machine on which Octez is installed. On Windows this is the OctezDesk WSL distro
/// (<see cref="Wsl.WslHost"/>); on Linux it will be the local machine. Everything above this
/// interface (provisioning, service control, health) is platform-neutral.
/// </summary>
public interface IOctezHost
{
    /// <summary>
    /// Runs <paramref name="argv"/> directly (no shell). Use <see cref="OctezHostExtensions.ShAsync"/>
    /// when a shell is genuinely needed.
    /// </summary>
    Task<CommandResult> RunAsync(
        IReadOnlyList<string> argv,
        HostUser user = HostUser.Root,
        string? stdin = null,
        Action<string>? onLine = null,
        CancellationToken ct = default);
}
