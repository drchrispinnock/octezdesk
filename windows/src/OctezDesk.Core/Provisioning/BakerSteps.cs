using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Provisioning;

public sealed class InstallBakerStep(IOctezHost host, BakerService baker) : IProvisioningStep
{
    public string Title => "Install the baker";

    public Task<bool> IsDoneAsync(CancellationToken ct) => baker.IsInstalledAsync(ct);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
        => (await host.AptGetAsync(["install", OctezPaths.BakerPackage], log, ct)).EnsureSuccess("Installing the baker");
}

/// <summary>
/// Writes <c>/etc/default/octez-baker</c>: the node's data directory, the keys from the baking keys
/// screen (consensus or master key, then companion key), the DAL node and the liquidity baking vote.
/// Done when the file already has exactly that content.
/// </summary>
public sealed class ConfigureBakerStep(
    IOctezHost host, BakerService baker, NodeService node, BakingKeyService keys, DalSettings? dal, BakerSettings settings) : IProvisioningStep
{
    public string Title => $"Configure the baker (liquidity baking vote: {settings.Vote.ToCliValue()})";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => await baker.ReadDefaultsFileAsync(ct) == await ExpectedAsync(ct);

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        var content = await ExpectedAsync(ct);
        await host.WriteFileAsync(OctezPaths.BakerDefaultsFile, content, ct);
        log($"Wrote {OctezPaths.BakerDefaultsFile}:");
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries)) log("  " + line);

        // A running baker only reads its settings at start.
        if (await baker.IsActiveAsync(ct)) await baker.RestartAsync(ct);
    }

    private async Task<string> ExpectedAsync(CancellationToken ct)
    {
        var record = await keys.GetRecordAsync(ct)
                     ?? throw new InvalidOperationException("No baking keys are recorded, so the baker has nothing to bake with.");
        var aliases = record.Aliases
            .Select(kv => (Role: Enum.TryParse<KeyRole>(kv.Key, out var r) ? r : (KeyRole?)null, Alias: kv.Value))
            .Where(k => k.Role is not null)
            .OrderBy(k => k.Role)
            .Select(k => k.Alias)
            .ToList();
        return BakerService.DefaultsFile(
            await node.GetDataDirAsync(ct), aliases, dal is { Enabled: true } ? dal.RpcPort : null, settings, await node.GetRpcPortAsync(ct));
    }
}

public sealed class StartBakerStep(BakerService baker) : IProvisioningStep
{
    public string Title => "Start the baker";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => await baker.IsEnabledAsync(ct) && await baker.IsActiveAsync(ct);

    public Task RunAsync(Action<string> log, CancellationToken ct) => baker.EnableAndStartAsync(ct);
}

/// <summary>When "Don't bake" was chosen: stops and disables a baker set up earlier. Keys and package are kept.</summary>
public sealed class DisableBakerStep(IOctezHost host, BakerService baker) : IProvisioningStep
{
    public string Title => "Turn off the baker";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => !await baker.IsInstalledAsync(ct) || (!await baker.IsEnabledAsync(ct) && !await baker.IsActiveAsync(ct));

    public async Task RunAsync(Action<string> log, CancellationToken ct)
        => (await host.RunAsync(["systemctl", "disable", "--now", OctezPaths.BakerService], ct: ct)).EnsureSuccess("Stopping the baker");
}
