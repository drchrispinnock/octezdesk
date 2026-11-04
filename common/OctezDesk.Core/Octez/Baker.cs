using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Octez;

/// <summary>The baker's vote on the liquidity baking toggle (<c>--liquidity-baking-toggle-vote</c>).</summary>
public enum LiquidityBakingVote
{
    Pass,
    On,
    Off,
}

public static class LiquidityBakingVoteExtensions
{
    public static string ToCliValue(this LiquidityBakingVote v) => v switch
    {
        LiquidityBakingVote.Pass => "pass",
        LiquidityBakingVote.On => "on",
        LiquidityBakingVote.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static LiquidityBakingVote? FromCliValue(string? s)
        => Enum.GetValues<LiquidityBakingVote>().Cast<LiquidityBakingVote?>().FirstOrDefault(v => v!.Value.ToCliValue() == s);
}

/// <summary>What the user chooses for the baker on wizard screen 3, besides the keys.</summary>
public sealed record BakerSettings(LiquidityBakingVote Vote = LiquidityBakingVote.Pass);

/// <summary>
/// Controls the packaged <c>octez-baker</c> unit. Its settings live in <c>/etc/default/octez-baker</c>,
/// which OctezDesk writes in full; that file is also how a finished configuration is recognised.
/// </summary>
public sealed class BakerService(IOctezHost host)
{
    /// <summary>The <c>/etc/default/octez-baker</c> OctezDesk writes. Values are fixed strings or aliases, so they need no escaping.</summary>
    /// <param name="keyAliases">Wallet aliases the baker signs with: the consensus (or master) key, and the companion key if any.</param>
    /// <param name="dalRpcPort">The local DAL node's RPC port, or null to bake without DAL.</param>
    public static string DefaultsFile(
        string nodeDataDir, IReadOnlyList<string> keyAliases, int? dalRpcPort, BakerSettings settings, int nodeRpcPort)
    {
        // LF only (not AppendLine): this is a Linux file, and systemd would read "pass\r" from CRLF lines.
        string[] lines =
        [
            "# Managed by OctezDesk. Changes here are replaced when you run its setup wizard.",
            $"DATADIR={nodeDataDir}",
            $"ENDPOINT=\"http://127.0.0.1:{nodeRpcPort}\"",
            $"CLIENTDIR={OctezPaths.TezosHome}/.tezos-client",
            $"KEYS=\"{string.Join(' ', keyAliases)}\"",
            dalRpcPort is { } port ? $"DAL_OPTIONS=\"--dal-node http://127.0.0.1:{port}\"" : "DAL_OPTIONS=\"--without-dal\"",
            $"LQ_VOTE=\"{settings.Vote.ToCliValue()}\"",
            "RUNTIME_OPTS=\"\"",
        ];
        return string.Join('\n', lines) + "\n";
    }

    public async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        var r = await host.RunAsync(["dpkg-query", "-W", "-f=${Status}", OctezPaths.BakerPackage], ct: ct);
        return r.Success && r.StdOut.Contains("install ok installed", StringComparison.Ordinal);
    }

    public Task<string?> ReadDefaultsFileAsync(CancellationToken ct = default) => host.TryReadFileAsync(OctezPaths.BakerDefaultsFile, ct);

    /// <summary>The settings in the installed defaults file, e.g. to prefill the wizard; null if there's no valid vote.</summary>
    public async Task<BakerSettings?> GetInstalledSettingsAsync(CancellationToken ct = default)
    {
        if (await ReadDefaultsFileAsync(ct) is not { } content) return null;
        return LiquidityBakingVoteExtensions.FromCliValue(ShellDefaultsFile.Parse(content).GetValueOrDefault("LQ_VOTE")) is { } vote
            ? new BakerSettings(vote)
            : null;
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "is-active", "--quiet", OctezPaths.BakerService], ct: ct)).Success;

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "is-enabled", "--quiet", OctezPaths.BakerService], ct: ct)).Success;

    public async Task EnableAndStartAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "enable", "--now", OctezPaths.BakerService], ct: ct)).EnsureSuccess("Starting the baker");

    public async Task RestartAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "restart", OctezPaths.BakerService], ct: ct)).EnsureSuccess("Restarting the baker");

    /// <summary>Streams the baker log until cancelled.</summary>
    public Task FollowLogAsync(Action<string> onLine, int initialLines = 500, CancellationToken ct = default)
        => host.FollowFileAsync(OctezPaths.BakerLogFile, onLine, initialLines, ct);
}
