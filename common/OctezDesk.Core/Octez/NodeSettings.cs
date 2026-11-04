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

    // Both listen on all IPv4 interfaces. With WSL mirrored networking (EnableMirroredNetworkingStep)
    // Windows reaches them at 127.0.0.1; without it, at the distro's own IP (spec/Windows.md §2.7).
    public string NetAddr => $"0.0.0.0:{P2PPort}";
    public string RpcAddr => $"0.0.0.0:{RpcPort}";

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
            "--rpc-addr", RpcAddr,
        };
        if (AllowAllRpc) argv.AddRange(["--allow-all-rpc", RpcAddr]);
        return argv;
    }
}
