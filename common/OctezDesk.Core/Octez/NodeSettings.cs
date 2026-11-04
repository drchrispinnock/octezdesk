namespace OctezDesk.Core.Octez;

/// <summary>What the user chooses on wizard screen 2 (Node).</summary>
public sealed record NodeSettings
{
    public const int DefaultP2PPort = 9732;
    public const int DefaultRpcPort = 8732;

    public TezosNetwork Network { get; init; } = TezosNetwork.Mainnet;
    public HistoryMode HistoryMode { get; init; } = HistoryMode.Rolling;
    public int P2PPort { get; init; } = DefaultP2PPort;
    public int RpcPort { get; init; } = DefaultRpcPort;
    /// <summary>Maps to <c>--allow-all-rpc</c> on the RPC address.</summary>
    public bool AllowAllRpc { get; init; }
    public bool ImportSnapshot { get; init; } = true;

    /// <summary>Gossip listens on all IPv4 interfaces, so peers can reach it.</summary>
    public string NetAddr => $"0.0.0.0:{P2PPort}";

    /// <summary>
    /// RPC listens on localhost only. With WSL mirrored networking (EnableMirroredNetworkingStep)
    /// Windows reaches it at 127.0.0.1 too (spec/Windows.md §2.7). It must stay a localhost address:
    /// on any other address Octez applies its restrictive default ACL, which blocks
    /// <c>/monitor/heads/main</c>, and the DAL node exits when it can't call that.
    /// </summary>
    public string RpcAddr => $"127.0.0.1:{RpcPort}";

    /// <summary>The RPC options for <c>octez-node config init</c> and <c>config update</c>.</summary>
    public IReadOnlyList<string> RpcArgs()
        => AllowAllRpc ? ["--rpc-addr", RpcAddr, "--allow-all-rpc", RpcAddr] : ["--rpc-addr", RpcAddr];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IsValidPort(P2PPort)) errors.Add("Gossip port must be between 1024 and 65535.");
        if (!IsValidPort(RpcPort)) errors.Add("RPC port must be between 1024 and 65535.");
        if (P2PPort == RpcPort) errors.Add("Gossip and RPC ports must be different.");
        if (ImportSnapshot && HistoryMode == HistoryMode.Archive)
            errors.Add("An archive node cannot be started from a snapshot; it must sync from genesis.");
        return errors;
    }

    private static bool IsValidPort(int port) => port is >= 1024 and <= 65535;

    /// <summary>
    /// argv for <c>octez-node config update</c>, run as the tezos user: sets the ports and RPC access
    /// of an existing node, keeping its chain data. The listen addresses are replaced, not added to.
    /// </summary>
    public IReadOnlyList<string> ConfigUpdateArgv(string dataDir)
        => ["octez-node", "config", "update", "--data-dir", dataDir, "--net-addr", NetAddr, .. RpcArgs()];

    /// <summary>argv for <c>octez-node config init</c>, run as the tezos user.</summary>
    public IReadOnlyList<string> ConfigInitArgv(string dataDir)
    {
        var argv = new List<string>
        {
            "octez-node", "config", "init",
            "--data-dir", dataDir,
            "--network", Network.ConfigValue,
            "--history-mode", HistoryMode.ToCliValue(),
            "--net-addr", NetAddr,
        };
        argv.AddRange(RpcArgs());
        return argv;
    }
}
