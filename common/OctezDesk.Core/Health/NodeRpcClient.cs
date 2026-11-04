using System.Globalization;
using System.Text.Json;
using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Health;

/// <summary>
/// The handful of Octez node RPCs used for health monitoring. The transport is pluggable:
/// <see cref="ViaHost"/> queries from inside the host (no dependency on WSL port forwarding);
/// <see cref="ViaHttp"/> queries directly over HTTP.
/// </summary>
/// <summary>The parts of <c>/delegates/&lt;pkh&gt;</c> OctezDesk shows.</summary>
/// <param name="CompanionPendingCycle">The cycle from which <see cref="CompanionPending"/> becomes active.</param>
public sealed record DelegateInfo(string? CompanionActive, string? CompanionPending, int? CompanionPendingCycle)
{
    public static DelegateInfo? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null; // an error list: not registered
        string? active = null, pending = null;
        int? cycle = null;
        if (root.TryGetProperty("companion_key", out var ck) && ck.ValueKind == JsonValueKind.Object)
        {
            if (ck.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("pkh", out var apkh))
                active = apkh.GetString();
            if (ck.TryGetProperty("pendings", out var ps) && ps.ValueKind == JsonValueKind.Array && ps.GetArrayLength() > 0)
            {
                var last = ps[ps.GetArrayLength() - 1];
                if (last.TryGetProperty("pkh", out var ppkh)) pending = ppkh.GetString();
                if (last.TryGetProperty("cycle", out var c) && c.TryGetInt32(out var n)) cycle = n;
            }
        }
        return new DelegateInfo(active, pending, cycle);
    }
}

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

    /// <summary>The account's spendable balance, in mutez (0 for an account the chain hasn't seen).</summary>
    public async Task<long> GetBalanceAsync(string address, CancellationToken ct = default)
        => await GetMutezAsync($"chains/main/blocks/head/context/contracts/{address}/balance", ct);

    /// <summary>The account's staked balance, in mutez.</summary>
    public async Task<long> GetStakedBalanceAsync(string address, CancellationToken ct = default)
        => await GetMutezAsync($"chains/main/blocks/head/context/contracts/{address}/staked_balance", ct);

    /// <summary>
    /// True if <paramref name="address"/> is registered as a delegate. The node answers with a
    /// <c>delegate.not_registered</c> error otherwise, which the transport reports as a failure.
    /// </summary>
    public async Task<bool> IsDelegateAsync(string address, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync($"chains/main/blocks/head/context/delegates/{address}", ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { return false; }
    }

    /// <summary>The delegate's companion key, active and pending; null if <paramref name="address"/> isn't a delegate.</summary>
    public async Task<DelegateInfo?> TryGetDelegateAsync(string address, CancellationToken ct = default)
    {
        JsonDocument doc;
        try { doc = await GetJsonAsync($"chains/main/blocks/head/context/delegates/{address}", ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { return null; }
        using (doc) return DelegateInfo.Parse(doc.RootElement);
    }

    /// <summary>Mutez amounts are JSON strings; a null (e.g. no staked balance) counts as 0.</summary>
    private async Task<long> GetMutezAsync(string path, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(path, ct);
        return doc.RootElement.ValueKind == JsonValueKind.String
            ? long.Parse(doc.RootElement.GetString()!, CultureInfo.InvariantCulture)
            : 0;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
        => JsonDocument.Parse(await get(path, ct));
}
