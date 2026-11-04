using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Core.Provisioning;

/// <summary>
/// Creates the dedicated OctezDesk distro. (We don't make the VHD sparse: current WSL disables
/// sparse VHDs because of a data-corruption risk and only allows it with --allow-unsafe.)
/// </summary>
public sealed class CreateDistroStep(WslManager wsl, string location) : IProvisioningStep
{
    public string Title => $"Create WSL distro \"{wsl.DistroName}\"";

    public Task<bool> IsDoneAsync(CancellationToken ct) => wsl.DistroExistsAsync(ct);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        log($"Installing {WslManager.BaseImage} into {location}");
        await wsl.InstallDistroAsync(location, log, ct);
    }
}

/// <summary>
/// Checks the distro is Debian 13 (trixie), which tzinit builds for. A separate step so that it is
/// re-checked on every run, including retries after the distro already exists.
/// </summary>
public sealed class CheckDebianReleaseStep(IOctezHost host) : IProvisioningStep
{
    public const string RequiredCodename = "trixie";

    public string Title => $"Check the distro is Debian {RequiredCodename}";

    public async Task<bool> IsDoneAsync(CancellationToken ct) => await GetCodenameAsync(ct) == RequiredCodename;

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        var codename = await GetCodenameAsync(ct);
        throw new InvalidOperationException(
            $"The distro is Debian '{codename}', but the Octez packages need '{RequiredCodename}'.");
    }

    private async Task<string> GetCodenameAsync(CancellationToken ct)
        => (await host.ShAsync(". /etc/os-release && echo \"$VERSION_CODENAME\"", ct: ct)).StdOut.Trim();
}

/// <summary>Turns on systemd in the distro so the packaged octez-node unit can be used unchanged.</summary>
public sealed class EnableSystemdStep(WslManager wsl, IOctezHost host) : IProvisioningStep
{
    public const string WslConf = """
        # Managed by OctezDesk
        [boot]
        systemd=true

        [user]
        default=root

        [interop]
        appendWindowsPath=false
        """;

    public string Title => "Enable systemd in the distro";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => (await host.TryReadFileAsync("/proc/1/comm", ct))?.Trim() == "systemd";

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        await host.WriteFileAsync("/etc/wsl.conf", WslConf + "\n", ct);
        log("Restarting the distro with systemd");
        await wsl.TerminateAsync(ct);

        // This call boots the distro again. Wait for boot to settle; "degraded" exits non-zero but is fine for us.
        await host.RunAsync(["systemctl", "is-system-running", "--wait"], onLine: log, ct: ct);
        if (!await IsDoneAsync(ct))
            throw new InvalidOperationException("systemd did not start inside the distro.");
    }
}

public static class ProvisioningPlans
{
    /// <summary>Objective 1 on Windows: create the distro and get a node running.</summary>
    public static Provisioner WindowsNode(
        WslManager wsl, IOctezHost host, string distroLocation, NodeSettings settings)
    {
        var node = new NodeService(host);
        var steps = new List<IProvisioningStep>
        {
            new CreateDistroStep(wsl, distroLocation),
            new CheckDebianReleaseStep(host),
            new EnableSystemdStep(wsl, host),
            new InstallOctezStep(host, node),
            new ConfigureNodeStep(host, node, settings),
        };
        if (settings.ImportSnapshot) steps.Add(new ImportSnapshotStep(host, node, settings));
        steps.Add(new StartNodeStep(node));
        return new Provisioner(steps);
    }
}
