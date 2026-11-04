using OctezDesk.Core.Octez;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Core;

public enum InstallationState
{
    WslMissing,
    /// <summary>WSL is present but the node has not been fully set up (no distro, package or config).</summary>
    NotSetUp,
    /// <summary>The node is installed and configured; show the main screen.</summary>
    Ready,
}

/// <summary>
/// Decides whether to show the wizard or the main screen by looking at the distro itself rather
/// than a Windows-side settings file, so reinstalling OctezDesk recovers cleanly.
/// </summary>
public sealed class InstallationProbe(WslManager wsl, NodeService node)
{
    public async Task<InstallationState> ProbeAsync(CancellationToken ct = default)
    {
        if (!await wsl.IsWslInstalledAsync(ct)) return InstallationState.WslMissing;
        if (!await wsl.DistroExistsAsync(ct)) return InstallationState.NotSetUp;
        // The unit is only enabled by the last provisioning step, so "enabled" means setup finished.
        // (Being configured is not enough: a snapshot import may have failed after config init.)
        if (!await node.IsInstalledAsync(ct) || !await node.IsConfiguredAsync(ct) || !await node.IsEnabledAsync(ct))
            return InstallationState.NotSetUp;
        return InstallationState.Ready;
    }
}
