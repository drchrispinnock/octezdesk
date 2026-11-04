using System.Globalization;
using System.Text.Json;
using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Health;

/// <summary>
/// The handful of Octez node RPCs used for health monitoring. The transport is pluggable:
/// <see cref="ViaHost"/> queries from inside the host (no dependency on WSL port forwarding);
/// <see cref="ViaHttp"/> queries directly over HTTP.
/// </summary>
public sealed class NodeRpcClient(Func<string, CancellationToken, Task<string>> get)
{
    /// <summary>Runs <c>wget</c> against 127.0.0.1 inside the host. Works the same on WSL and native Linux.</summary>
    public static NodeRpcClient ViaHost(IOctezHost host, int port) => new(async (path, ct) =>
    {
        var r = await host.RunAsync(["wget", "-q", "-O-", "-T", "3", "-t", "1", $"http://127.0.0.1:{port}/{path}"], ct: ct);
        if (!r.Success) throw new HttpRequestException($"RPC /{path} failed (wget exit {r.ExitCode})");
        return r.StdOut;
    });

    public static NodeRpcClient ViaHttp(int port)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/"), Timeout = TimeSpan.FromSeconds(3) };
        return new((path, ct) => http.GetStringAsync(path, ct));
    }

    public async Task<(bool Bootstrapped, string? SyncState)> IsBootstrappedAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("chains/main/is_bootstrapped", ct);
        var root = doc.RootElement;
        return (root.GetProperty("bootstrapped").GetBoolean(),
                root.TryGetProperty("sync_state", out var s) ? s.GetString() : null);
    }

    public async Task<int> GetPeerCountAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("network/connections", ct);
        return doc.RootElement.GetArrayLength();
    }

    public async Task<(int Level, DateTimeOffset Timestamp)> GetHeadAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("chains/main/blocks/head/header", ct);
        var root = doc.RootElement;
        return (root.GetProperty("level").GetInt32(),
                DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture));
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("version", ct);
        var v = doc.RootElement.GetProperty("version");
        return $"{v.GetProperty("major").GetInt32()}.{v.GetProperty("minor").GetInt32()}";
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
        => JsonDocument.Parse(await get(path, ct));
}
