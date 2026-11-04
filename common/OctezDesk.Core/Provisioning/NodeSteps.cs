using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Provisioning;

/// <summary>Adds the tzinit APT repository and installs the node packages.</summary>
public sealed class InstallOctezStep(IOctezHost host, NodeService node) : IProvisioningStep
{
    public const string TzinitKeyFingerprint = "181E6294A0770AE9AF0173B21F688E1201D819B4";

    // Fallback when `extrepo enable tzinit` is unavailable. $1 = expected key fingerprint.
    private const string ManualRepoScript = """
        set -e
        install -d -m 0755 /etc/apt/keyrings
        curl -fsSL https://apt.tzinit.org/keys/tzinit.asc -o /tmp/tzinit.asc
        fpr=$(gpg --show-keys --with-colons /tmp/tzinit.asc | awk -F: '/^fpr:/ {print $10; exit}')
        if [ "$fpr" != "$1" ]; then echo "tzinit key fingerprint mismatch: got $fpr" >&2; exit 1; fi
        gpg --dearmor --yes -o /etc/apt/keyrings/tzinit.gpg /tmp/tzinit.asc
        rm -f /tmp/tzinit.asc
        echo "deb [signed-by=/etc/apt/keyrings/tzinit.gpg] https://apt.tzinit.org/debian trixie main" > /etc/apt/sources.list.d/tzinit-octez.list
        """;

    public string Title => "Install Octez from packages.tzinit.org";

    public Task<bool> IsDoneAsync(CancellationToken ct) => node.IsInstalledAsync(ct);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        (await host.AptGetAsync(["update"], log, ct)).EnsureSuccess("apt-get update");
        // wget is also used by NodeMonitor for RPC health checks; the WSL Debian image lacks it.
        (await host.AptGetAsync(["install", "extrepo", "ca-certificates", "wget"], log, ct)).EnsureSuccess("Installing extrepo");

        var extrepo = await host.RunAsync(["extrepo", "enable", "tzinit"], onLine: log, ct: ct);
        if (!extrepo.Success)
        {
            log("extrepo could not enable tzinit; adding the repository manually.");
            (await host.AptGetAsync(["install", "curl", "gpg"], log, ct)).EnsureSuccess("Installing curl and gpg");
            (await host.ShAsync(ManualRepoScript, [TzinitKeyFingerprint], onLine: log, ct: ct)).EnsureSuccess("Adding the tzinit repository");
        }

        (await host.AptGetAsync(["update"], log, ct)).EnsureSuccess("apt-get update");
        (await host.AptGetAsync(["install", .. OctezPaths.NodePackages], log, ct)).EnsureSuccess("Installing Octez");
    }
}

/// <summary>
/// Runs <c>octez-node config init</c> as the tezos user, and records the settings used. Done only
/// when the node is configured <em>with these settings</em>; a different existing config is
/// replaced if the node has no chain data yet, and refused otherwise (that is Reconfigure's job).
/// </summary>
public sealed class ConfigureNodeStep(IOctezHost host, NodeService node, NodeSettings settings) : IProvisioningStep
{
    public string Title => $"Configure node for {settings.Network.DisplayName} ({settings.HistoryMode.ToCliValue()})";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => await node.IsConfiguredAsync(ct) && await node.GetConfiguredSettingsAsync(ct) == NodeSettingsRecord.From(settings);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        var dataDir = await node.GetDataDirAsync(ct);
        var configFile = await node.GetConfigFilePathAsync(ct);

        if (await host.FileExistsAsync(configFile, ct))
        {
            // Same chain (network and history mode): ports and RPC access are updated in place,
            // keeping the chain data. Only a different chain needs the data wiped.
            var wanted = NodeSettingsRecord.From(settings);
            if (await node.GetConfiguredSettingsAsync(ct) is { } recorded && recorded.IsSameChain(wanted))
            {
                log("Updating the node's ports and RPC access; its chain data is kept.");
                (await host.RunAsync(settings.ConfigUpdateArgv(dataDir), HostUser.Tezos, onLine: log, ct: ct))
                    .EnsureSuccess("octez-node config update");
                await host.WriteFileAsync(OctezPaths.OctezDeskNodeSettingsFile, wanted.ToJson(), ct);
                // The node only reads its config at start.
                if (await node.IsActiveAsync(ct)) await node.RestartAsync(ct);
                return;
            }

            // An import still running is for the old settings; the import step would otherwise
            // re-attach to it and import the wrong network's snapshot.
            if ((await host.RunAsync(["systemctl", "is-active", "--quiet", ImportSnapshotStep.UnitName], ct: ct)).Success)
            {
                log("Cancelling the snapshot import for the previous settings.");
                (await host.RunAsync(["systemctl", "stop", ImportSnapshotStep.UnitName], ct: ct)).EnsureSuccess("Stopping the snapshot import");
                await host.RunAsync(["rm", "-f", ImportSnapshotStep.DownloadFile], ct: ct);
            }

            if (await node.HasChainDataAsync(ct))
                throw new InvalidOperationException(
                    "The node already has chain data for a different network or history mode. " +
                    "Go Back and choose the node's current network and history mode to keep it.");
            log("Replacing the existing node configuration (no chain data yet).");
            (await host.RunAsync(["rm", "-f", configFile], ct: ct)).EnsureSuccess("Removing old config");
        }

        (await host.RunAsync(settings.ConfigInitArgv(dataDir), HostUser.Tezos, onLine: log, ct: ct))
            .EnsureSuccess("octez-node config init");
        await host.WriteFileAsync(OctezPaths.OctezDeskNodeSettingsFile, NodeSettingsRecord.From(settings).ToJson(), ct);
    }
}

/// <summary>
/// Downloads and imports a tzinit snapshot. It runs as a transient systemd unit so it survives
/// the GUI being closed; if the unit is already running (e.g. after an app restart) we re-attach.
/// </summary>
public sealed class ImportSnapshotStep(IOctezHost host, NodeService node, NodeSettings settings) : IProvisioningStep
{
    public const string UnitName = "octezdesk-snapshot";

    public const string DownloadFile = OctezPaths.TezosHome + "/octezdesk-snapshot";

    // $1 = snapshot URL, $2 = node data dir, $3 = download file
    private const string ImportScript = """
        set -e
        f="$3"
        wget --progress=dot:giga -O "$f" "$1"
        octez-node snapshot import "$f" --data-dir "$2"
        rm -f "$f"
        """;

    public string Title => $"Download and import {settings.HistoryMode.ToCliValue()} snapshot";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => !await IsUnitActiveAsync(ct) && await node.HasChainDataAsync(ct);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        using var followCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var follow = host.RunAsync(["journalctl", "-f", "-o", "cat", "--since", "now", "-u", UnitName], onLine: log, ct: followCts.Token);

        try
        {
            if (await IsUnitActiveAsync(ct))
            {
                log("A snapshot import is already running; waiting for it to finish.");
                while (await IsUnitActiveAsync(ct))
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            else
            {
                // The WSL Debian image is minimal (no wget or curl). Installed here rather than in
                // InstallOctezStep so that a retry, which skips that step, still gets it.
                (await host.AptGetAsync(["install", "wget", "ca-certificates"], log, ct)).EnsureSuccess("Installing wget");

                var url = settings.Network.SnapshotUrl(settings.HistoryMode);
                var dataDir = await node.GetDataDirAsync(ct);
                log($"Snapshot: {url}");
                (await host.RunAsync(
                    [
                        "systemd-run", "--unit", UnitName, "--wait", "--collect", "--quiet",
                        "--uid=tezos", "--gid=tezos", "--property=WorkingDirectory=" + OctezPaths.TezosHome,
                        "sh", "-c", ImportScript, "sh", url, dataDir, DownloadFile,
                    ],
                    onLine: log, ct: ct)).EnsureSuccess("Snapshot import");
            }
        }
        finally
        {
            followCts.Cancel();
            try { await follow; } catch (OperationCanceledException) { }
        }

        if (!await node.HasChainDataAsync(ct))
            throw new InvalidOperationException("Snapshot import finished but the node store is missing; see the log above.");
    }

    private async Task<bool> IsUnitActiveAsync(CancellationToken ct)
        => (await host.RunAsync(["systemctl", "is-active", "--quiet", UnitName], ct: ct)).Success;
}

/// <summary>
/// Generates the baking keys chosen on the Baker screen, in the tezos user's octez-client wallet.
/// Runs once the node is up: without one, each octez-client call waits about 20 s for it.
/// </summary>
public sealed class GenerateBakingKeysStep(BakingKeyService keys, BakingKeyChoice choice, NodeService node) : IProvisioningStep
{
    public string Title => choice.Setup switch
    {
        BakingKeySetup.MasterKey => $"Generate a {choice.MasterType.Prefix()} master key",
        BakingKeySetup.MasterAndCompanion => "Generate tz4 master and companion keys",
        BakingKeySetup.ConsensusAndCompanion => "Generate tz4 consensus and companion keys",
        _ => "Generate baking keys",
    };

    public Task<bool> IsDoneAsync(CancellationToken ct) => keys.HasKeysForAsync(choice, ct);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        // Not fatal: keys can be made without the node, only slowly.
        if (!await node.WaitForRpcAsync(TimeSpan.FromMinutes(1), ct))
            log("The node's RPC isn't answering yet; generating keys anyway (this is slower).");
        await keys.CreateAsync(choice, log, ct);
    }
}

/// <summary>
/// Makes the node's RPC listen on <see cref="NodeSettings.RpcAddr"/>. A fresh config already does;
/// this repairs nodes configured by earlier OctezDesk builds, which listened on 0.0.0.0. There,
/// Octez's restrictive default ACL blocked the DAL node. Uses <c>config update</c>, which keeps the
/// chain data, and restarts a running node so it takes effect.
/// </summary>
public sealed class SetNodeRpcAddressStep(IOctezHost host, NodeService node, NodeSettings settings) : IProvisioningStep
{
    public string Title => $"Set the node's RPC address to {settings.RpcAddr}";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => await node.GetRpcListenAddrsAsync(ct) is [var addr] && addr == settings.RpcAddr;

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        var current = await node.GetRpcListenAddrsAsync(ct);
        log($"The node's RPC listens on {(current is { Count: > 0 } ? string.Join(", ", current) : "nothing")}; changing it to {settings.RpcAddr}.");
        (await host.RunAsync(settings.ConfigUpdateArgv(await node.GetDataDirAsync(ct)), HostUser.Tezos, onLine: log, ct: ct))
            .EnsureSuccess("octez-node config update");
        // The node only reads its config at start.
        if (await node.IsActiveAsync(ct))
        {
            log("Restarting the node.");
            await node.RestartAsync(ct);
        }
    }
}

public sealed class StartNodeStep(NodeService node) : IProvisioningStep
{
    public string Title => "Start the node";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => await node.IsEnabledAsync(ct) && await node.IsActiveAsync(ct);

    public Task RunAsync(Action<string> log, CancellationToken ct) => node.EnableAndStartAsync(ct);
}
