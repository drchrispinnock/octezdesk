using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Health;

/// <summary>
/// Gathers a <see cref="NodeObservation"/> from systemd and the node RPC, and evaluates it.
/// RPCs are made from inside the host: WSL's localhost forwarding to Windows proved unreliable.
/// </summary>
public sealed class NodeMonitor(IOctezHost host, NodeService node)
{
    private NodeRpcClient? _rpc;
    private int _rpcPort;

    public async Task<NodeHealth> CheckAsync(CancellationToken ct = default)
    {
        var active = await node.IsActiveAsync(ct);
        if (!active)
            return HealthEvaluator.Evaluate(new NodeObservation { ServiceActive = false });

        var port = await node.GetRpcPortAsync(ct);
        if (_rpc is null || port != _rpcPort)
        {
            _rpc = NodeRpcClient.ViaHost(host, port);
            _rpcPort = port;
        }

        bool? bootstrapped = null;
        string? syncState = null;
        try
        {
            (var b, syncState) = await _rpc.IsBootstrappedAsync(ct);
            bootstrapped = b;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthEvaluator.Evaluate(new NodeObservation { ServiceActive = true, RpcReachable = false });
        }

        var observation = new NodeObservation
        {
            ServiceActive = true,
            RpcReachable = true,
            Bootstrapped = bootstrapped,
            SyncState = syncState,
            PeerCount = await TryAsync(() => _rpc.GetPeerCountAsync(ct)),
            Version = await TryRefAsync(() => _rpc.GetVersionAsync(ct)),
        };
        try
        {
            var (level, ts) = await _rpc.GetHeadAsync(ct);
            observation = observation with { HeadLevel = level, HeadTimestamp = ts };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }

        return HealthEvaluator.Evaluate(observation);
    }

    private static async Task<int?> TryAsync(Func<Task<int>> f)
    {
        try { return await f(); } catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static async Task<string?> TryRefAsync(Func<Task<string>> f)
    {
        try { return await f(); } catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
