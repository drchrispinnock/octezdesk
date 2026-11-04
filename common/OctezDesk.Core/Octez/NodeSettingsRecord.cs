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

    public static NodeSettingsRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<NodeSettingsRecord>(json); }
        catch (JsonException) { return null; }
    }
}
