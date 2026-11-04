using OctezDesk.Core.Health;
using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Provisioning;

/// <summary>
/// Shared by the funding steps: the master key's address, and the local node's RPC. Every check
/// reads the chain through the local node, so it reflects the real state on a re-run.
/// </summary>
public sealed class MasterAccount(IOctezHost host, NodeService node, OctezClient client)
{
    public const long MutezPerTez = 1_000_000;

    public OctezClient Client => client;

    public async Task<string> GetAddressAsync(CancellationToken ct)
        => (await client.ShowAddressAsync(BakingKeyService.MasterAlias, ct))?.Address
           ?? throw new InvalidOperationException($"The wallet has no master key '{BakingKeyService.MasterAlias}'.");

    /// <summary>Null if there's no master key yet, e.g. when checking steps before the keys step has run.</summary>
    public async Task<string?> TryGetAddressAsync(CancellationToken ct)
        => (await client.ShowAddressAsync(BakingKeyService.MasterAlias, ct))?.Address;

    public async Task<NodeRpcClient> RpcAsync(CancellationToken ct)
        => NodeRpcClient.ViaHost(host, await node.GetRpcPortAsync(ct));

    /// <summary>The master key's on-chain state; null if there's no master key or the node can't be asked.</summary>
    public async Task<MasterAccountStatus?> TryGetStatusAsync(CancellationToken ct = default)
    {
        if (await TryGetAddressAsync(ct) is not { } address) return null;
        try
        {
            var rpc = await RpcAsync(ct);
            return new MasterAccountStatus(
                address, await rpc.GetBalanceAsync(address, ct), await rpc.GetStakedBalanceAsync(address, ct),
                await rpc.TryGetDelegateAsync(address, ct))
            {
                UnstakedFrozenMutez = await rpc.GetUnstakedFrozenBalanceAsync(address, ct),
                UnstakedFinalizableMutez = await rpc.GetUnstakedFinalizableBalanceAsync(address, ct),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or FormatException) { return null; }
    }

    /// <summary>Registers the master key as a delegate, with the companion key if the wallet has one, once the node is bootstrapped.</summary>
    public async Task RegisterAsync(bool withCompanion, Action<string>? log = null, CancellationToken ct = default)
    {
        await client.WaitForBootstrappedAsync(log, ct);
        await client.RegisterDelegateAsync(BakingKeyService.MasterAlias, withCompanion ? BakingKeyService.CompanionAlias : null, log, ct);
    }

    /// <summary>Unstakes <paramref name="mutez"/> from the master key's stake (null: everything), once the node is bootstrapped.</summary>
    public async Task UnstakeAsync(long? mutez, Action<string>? log = null, CancellationToken ct = default)
    {
        await client.WaitForBootstrappedAsync(log, ct);
        await client.UnstakeAsync(BakingKeyService.MasterAlias, mutez, log, ct);
    }

    /// <summary>Moves the master key's finalizable unstaked tez to its spendable balance.</summary>
    public async Task FinalizeUnstakeAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        await client.WaitForBootstrappedAsync(log, ct);
        await client.FinalizeUnstakeAsync(BakingKeyService.MasterAlias, log, ct);
    }

    /// <summary>Spendable plus staked, in mutez; null if the node can't be asked.</summary>
    public async Task<long?> TryGetTotalBalanceAsync(string address, CancellationToken ct)
    {
        try
        {
            var rpc = await RpcAsync(ct);
            return await rpc.GetBalanceAsync(address, ct) + await rpc.GetStakedBalanceAsync(address, ct);
        }
        catch (HttpRequestException) { return null; }
    }
}

/// <param name="Delegate">Null if the account isn't registered as a delegate.</param>
public sealed record MasterAccountStatus(string Address, long BalanceMutez, long StakedMutez, DelegateInfo? Delegate)
{
    public bool IsDelegate => Delegate is not null;

    /// <summary>Unstaked, still frozen for a few cycles.</summary>
    public long UnstakedFrozenMutez { get; init; }

    /// <summary>Unstaked and no longer frozen: <c>finalize unstake</c> makes it spendable.</summary>
    public long UnstakedFinalizableMutez { get; init; }
}

/// <summary>
/// Gets test tez for the master key from the network's faucet, solving its proof-of-work
/// challenges, then waits until the local node sees them. Done when the master key already holds
/// the amount (less <see cref="FundingSettings.FeeAllowance"/>), counting what it has staked.
/// </summary>
public sealed class FundFromFaucetStep(MasterAccount account, Faucet faucet, FundingSettings settings, TezosNetwork network) : IProvisioningStep
{
    private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromMinutes(10);

    public string Title => $"Get {settings.Amount:N0} ꜩ from the {network.DisplayName} faucet";

    private long Target => (long)(settings.Amount - FundingSettings.FeeAllowance) * MasterAccount.MutezPerTez;

    public async Task<bool> IsDoneAsync(CancellationToken ct)
        => await account.TryGetAddressAsync(ct) is { } address && await account.TryGetTotalBalanceAsync(address, ct) >= Target;

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        var address = await account.GetAddressAsync(ct);
        log("Waiting for the node to be bootstrapped, to read the balance…");
        await account.Client.WaitForBootstrappedAsync(log, ct);

        var have = await account.TryGetTotalBalanceAsync(address, ct) ?? 0;
        var missing = (int)Math.Max(1, (settings.Amount * MasterAccount.MutezPerTez - have + MasterAccount.MutezPerTez - 1) / MasterAccount.MutezPerTez);
        log($"Requesting {missing:N0} ꜩ for {address} from {faucet.BaseUrl}. This can take a few minutes.");
        var hash = await faucet.RequestAsync(address, missing, log, ct);
        log($"The faucet sent the tez (operation {hash}). Waiting for it to reach the node…");

        var deadline = DateTime.UtcNow + ArrivalTimeout;
        while (await account.TryGetTotalBalanceAsync(address, ct) is not { } balance || balance < Target)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"The faucet's transfer {hash} hasn't reached the node after {ArrivalTimeout.TotalMinutes:N0} minutes. Retry to check again.");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        log($"The master key now holds {await account.TryGetTotalBalanceAsync(address, ct) / (decimal)MasterAccount.MutezPerTez:N2} ꜩ.");
    }
}

/// <summary>Registers the master key as a delegate, with the companion key when the setup has one.</summary>
public sealed class RegisterDelegateStep(MasterAccount account, bool withCompanion) : IProvisioningStep
{
    public string Title => withCompanion
        ? $"Register \"{BakingKeyService.MasterAlias}\" as a baker, with its companion key"
        : $"Register \"{BakingKeyService.MasterAlias}\" as a baker";

    public async Task<bool> IsDoneAsync(CancellationToken ct)
    {
        if (await account.TryGetAddressAsync(ct) is not { } address) return false;
        try { return await (await account.RpcAsync(ct)).IsDelegateAsync(address, ct); }
        catch (HttpRequestException) { return false; }
    }

    public Task RunAsync(Action<string> log, CancellationToken ct) => account.RegisterAsync(withCompanion, log, ct);
}

/// <summary>
/// Stakes <see cref="FundingSettings.StakeAmount"/> from the master key, topping up what's
/// already staked. Baking rights follow a few cycles later.
/// </summary>
public sealed class StakeStep(MasterAccount account, FundingSettings settings) : IProvisioningStep
{
    public string Title => $"Stake {settings.StakeAmount:N0} ꜩ";

    private long Target => settings.StakeAmount * MasterAccount.MutezPerTez;

    private async Task<long?> StakedAsync(CancellationToken ct)
    {
        if (await account.TryGetAddressAsync(ct) is not { } address) return null;
        try { return await (await account.RpcAsync(ct)).GetStakedBalanceAsync(address, ct); }
        catch (HttpRequestException) { return null; }
    }

    public async Task<bool> IsDoneAsync(CancellationToken ct) => await StakedAsync(ct) >= Target;

    public async Task RunAsync(Action<string> log, CancellationToken ct)
    {
        await account.Client.WaitForBootstrappedAsync(log, ct);
        var staked = await StakedAsync(ct) ?? 0;
        var tez = (Target - staked + MasterAccount.MutezPerTez - 1) / MasterAccount.MutezPerTez;
        await account.Client.StakeAsync(BakingKeyService.MasterAlias, tez, log, ct);
        log("Staked. Baking rights start a few cycles from now.");
    }
}
