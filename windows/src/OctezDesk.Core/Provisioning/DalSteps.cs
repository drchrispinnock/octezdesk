using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Provisioning;

/// <summary>Installs the tzinit <c>octez-dal-node</c> package (about 640 MB, mostly the DAL trusted setup).</summary>
public sealed class InstallDalNodeStep(IOctezHost host, DalNodeService dal) : IProvisioningStep
{
    public string Title => "Install the DAL node";

    public Task<bool> IsDoneAsync(CancellationToken ct) => dal.IsInstalledAsync(ct);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
        => (await host.AptGetAsync(["install", OctezPaths.DalPackage], log, ct)).EnsureSuccess("Installing the DAL node");
}

/// <summary>
/// Runs <c>octez-dal-node config init</c> as the tezos user, pointed at the local node, with the
/// attester profile, and records the settings used. Done only when the recorded settings match, so
/// a changed choice replaces the config (DAL data is kept; only config.json is rewritten).
/// </summary>
public sealed class ConfigureDalNodeStep(
    IOctezHost host, DalNodeService dal, NodeService node, OctezClient client, DalSettings settings) : IProvisioningStep
{
    public string Title => "Configure the DAL node";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => await host.FileExistsAsync(await dal.GetConfigFilePathAsync(ct), ct)
           && await dal.GetConfiguredSettingsAsync(ct) == await ExpectedRecordAsync(ct);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        var record = await ExpectedRecordAsync(ct);
        var dataDir = await dal.GetDataDirAsync(ct);
        var configFile = await dal.GetConfigFilePathAsync(ct);

        if (await host.FileExistsAsync(configFile, ct))
        {
            log("Replacing the existing DAL node configuration.");
            (await host.RunAsync(["rm", "-f", configFile], ct: ct)).EnsureSuccess("Removing old DAL config");
        }

        log(record.Attester is null ? "No attester profile." : $"Attester: {record.Attester}");
        (await host.RunAsync(settings.ConfigInitArgv(dataDir, record.NodeRpcPort, record.Attester), HostUser.Tezos, onLine: log, ct: ct))
            .EnsureSuccess("octez-dal-node config init");
        await host.WriteFileAsync(OctezPaths.OctezDeskDalSettingsFile, record.ToJson(), ct);

        // A running DAL node only reads its config at start.
        if (await dal.IsActiveAsync(ct)) await dal.RestartAsync(ct);
    }

    private async Task<DalSettingsRecord> ExpectedRecordAsync(CancellationToken ct)
        => new(await ResolveAttesterAsync(ct), settings.RpcPort, settings.P2PPort, await node.GetRpcPortAsync(ct));

    private async Task<string?> ResolveAttesterAsync(CancellationToken ct)
    {
        if (!settings.AttestForMasterKey)
            return string.IsNullOrWhiteSpace(settings.AttesterAddress) ? null : settings.AttesterAddress.Trim();
        return (await client.ShowAddressAsync(BakingKeyService.MasterAlias, ct))?.Address
               ?? throw new InvalidOperationException(
                   $"The DAL node should attest for the master key '{BakingKeyService.MasterAlias}', but the wallet has no such key.");
    }
}

public sealed class StartDalNodeStep(DalNodeService dal) : IProvisioningStep
{
    public string Title => "Start the DAL node";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => await dal.IsEnabledAsync(ct) && await dal.IsActiveAsync(ct);

    public Task RunAsync(Action<string> log, CancellationToken ct) => dal.EnableAndStartAsync(ct);
}

/// <summary>When the DAL node was turned off in the wizard: stops and disables one set up earlier. The package and data are kept.</summary>
public sealed class DisableDalNodeStep(IOctezHost host, DalNodeService dal) : IProvisioningStep
{
    public string Title => "Turn off the DAL node";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => !await dal.IsInstalledAsync(ct) || (!await dal.IsEnabledAsync(ct) && !await dal.IsActiveAsync(ct));

    public async Task RunAsync(Action<string> log, CancellationToken ct)
        => (await host.RunAsync(["systemctl", "disable", "--now", OctezPaths.DalNodeService], ct: ct)).EnsureSuccess("Stopping the DAL node");
}
