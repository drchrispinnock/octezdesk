using System.Text.Json;

namespace OctezDesk.Core.Octez;

/// <summary>
/// What OctezDesk configured the node with, stored in the distro at
/// <see cref="OctezPaths.OctezDeskNodeSettingsFile"/>. Lets setup tell "configured as the user asked"
/// apart from "configured differently" (e.g. after going Back and changing network), and will
/// pre-fill Reconfigure.
/// </summary>
public sealed record NodeSettingsRecord(string Network, string HistoryMode, int P2PPort, int RpcPort, bool AllowAllRpc)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static NodeSettingsRecord From(NodeSettings s)
        => new(s.Network.Id, s.HistoryMode.ToCliValue(), s.P2PPort, s.RpcPort, s.AllowAllRpc);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Same network and history mode: the chain data fits, and only ports or RPC access differ.</summary>
    public bool IsSameChain(NodeSettingsRecord other) => Network == other.Network && HistoryMode == other.HistoryMode;

    /// <summary>The wizard settings this record came from, e.g. to prefill the Node screen when the wizard is run again.</summary>
    public NodeSettings? ToSettings()
    {
        var network = TezosNetwork.All.FirstOrDefault(n => n.Id == Network);
        var mode = Enum.GetValues<HistoryMode>().Cast<HistoryMode?>().FirstOrDefault(m => m!.Value.ToCliValue() == HistoryMode);
        if (network is null || mode is null) return null;
        // An existing node already has its data, so a snapshot isn't needed.
        return new NodeSettings
        {
            Network = network, HistoryMode = mode.Value, P2PPort = P2PPort, RpcPort = RpcPort, AllowAllRpc = AllowAllRpc,
            ImportSnapshot = false,
        };
    }

    public static NodeSettingsRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<NodeSettingsRecord>(json); }
        catch (JsonException) { return null; }
    }
}
