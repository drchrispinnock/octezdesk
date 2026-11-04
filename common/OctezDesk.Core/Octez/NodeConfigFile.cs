using System.Text.Json;

namespace OctezDesk.Core.Octez;

/// <summary>Reads the bits of the node's <c>config.json</c> that OctezDesk needs.</summary>
public static class NodeConfigFile
{
    /// <summary>All <c>rpc.listen-addrs</c> entries (or the older single <c>listen-addr</c>); empty if none, null if the JSON is invalid.</summary>
    public static IReadOnlyList<string>? TryGetRpcListenAddrs(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (!doc.RootElement.TryGetProperty("rpc", out var rpc)) return [];
            if (rpc.TryGetProperty("listen-addrs", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
                return addrs.EnumerateArray().Select(a => a.GetString()).OfType<string>().ToList();
            if (rpc.TryGetProperty("listen-addr", out var single) && single.GetString() is { } s) return [s];
            return [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The port of the first <c>rpc.listen-addrs</c> entry (e.g. "127.0.0.1:8732", "[::1]:8732"),
    /// or null if there is none.
    /// </summary>
    public static int? TryGetRpcPort(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (!doc.RootElement.TryGetProperty("rpc", out var rpc)) return null;

            string? addr = null;
            if (rpc.TryGetProperty("listen-addrs", out var addrs) && addrs.ValueKind == JsonValueKind.Array && addrs.GetArrayLength() > 0)
                addr = addrs[0].GetString();
            else if (rpc.TryGetProperty("listen-addr", out var single))
                addr = single.GetString();

            if (addr is null) return null;
            var colon = addr.LastIndexOf(':');
            return colon >= 0 && int.TryParse(addr[(colon + 1)..], out var port) ? port : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
