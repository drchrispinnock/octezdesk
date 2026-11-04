using System.Text.Json;
using System.Text.RegularExpressions;
using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Octez;

/// <summary>What the user chooses on wizard screen 4 (DAL node).</summary>
public sealed record DalSettings
{
    public const int DefaultRpcPort = 10732;
    public const int DefaultP2PPort = 11732;

    private static readonly Regex ImplicitAddress = new("^tz[1-4][1-9A-HJ-NP-Za-km-z]{33}$", RegexOptions.Compiled);

    public bool Enabled { get; init; }

    /// <summary>
    /// Attest for the master key created on the baking keys screen. Its address is only known once
    /// setup has generated it, so it's resolved then.
    /// </summary>
    public bool AttestForMasterKey { get; init; }

    /// <summary>The baker's address to attest for, when it isn't a master key OctezDesk created. Null: no attester profile.</summary>
    public string? AttesterAddress { get; init; }

    public int RpcPort { get; init; } = DefaultRpcPort;
    public int P2PPort { get; init; } = DefaultP2PPort;

    /// <summary>The DAL node's RPC listens inside the distro only; the baker is its only client.</summary>
    public string RpcAddr => $"127.0.0.1:{RpcPort}";
    public string NetAddr => $"0.0.0.0:{P2PPort}";

    public static bool IsImplicitAddress(string? s) => s is not null && ImplicitAddress.IsMatch(s);

    /// <param name="node">The node's settings, whose ports must not clash with the DAL node's.</param>
    /// <param name="baking">True if a baking key setup was chosen: an attester is then required.</param>
    public IReadOnlyList<string> Validate(NodeSettings node, bool baking)
    {
        var errors = new List<string>();
        if (!Enabled) return errors;
        if (!AttestForMasterKey)
        {
            if (string.IsNullOrWhiteSpace(AttesterAddress))
            {
                if (baking) errors.Add("Enter the address of your baker's master key, so the DAL node attests for it.");
            }
            else if (!IsImplicitAddress(AttesterAddress))
            {
                errors.Add("The attester must be a tz1, tz2, tz3 or tz4 address.");
            }
        }

        foreach (var (name, port) in new[] { ("DAL RPC port", RpcPort), ("DAL P2P port", P2PPort) })
            if (port is < 1024 or > 65535) errors.Add($"{name} must be between 1024 and 65535.");
        var ports = new[] { RpcPort, P2PPort, node.RpcPort, node.P2PPort };
        if (ports.Distinct().Count() < ports.Length) errors.Add("The DAL node's ports must differ from each other and from the node's.");
        return errors;
    }

    /// <summary>argv for <c>octez-dal-node config init</c>, run as the tezos user.</summary>
    /// <param name="attester">The resolved attester address, or null for none.</param>
    public IReadOnlyList<string> ConfigInitArgv(string dataDir, int nodeRpcPort, string? attester)
    {
        var argv = new List<string>
        {
            "octez-dal-node", "config", "init",
            "--data-dir", dataDir,
            "--endpoint", $"http://127.0.0.1:{nodeRpcPort}",
            "--rpc-addr", RpcAddr,
            "--net-addr", NetAddr,
        };
        if (attester is not null) argv.AddRange(["--attester-profiles", attester]);
        return argv;
    }
}

/// <summary>What OctezDesk configured the DAL node with, kept in the distro so a changed choice is noticed.</summary>
public sealed record DalSettingsRecord(string? Attester, int RpcPort, int P2PPort, int NodeRpcPort)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DalSettingsRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DalSettingsRecord>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Controls the packaged <c>octez-dal-node</c> systemd unit and reads its configuration.</summary>
public sealed class DalNodeService(IOctezHost host)
{
    public async Task<string> GetDataDirAsync(CancellationToken ct = default)
    {
        var content = await host.TryReadFileAsync(OctezPaths.DalDefaultsFile, ct);
        if (content is not null && ShellDefaultsFile.Parse(content).TryGetValue("DATADIR", out var dir) && dir.Length > 0)
            return dir;
        return OctezPaths.DefaultDalDataDir;
    }

    public async Task<string> GetConfigFilePathAsync(CancellationToken ct = default)
        => (await GetDataDirAsync(ct)).TrimEnd('/') + "/config.json";

    public async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        var r = await host.RunAsync(["dpkg-query", "-W", "-f=${Status}", OctezPaths.DalPackage], ct: ct);
        return r.Success && r.StdOut.Contains("install ok installed", StringComparison.Ordinal);
    }

    public async Task<DalSettingsRecord?> GetConfiguredSettingsAsync(CancellationToken ct = default)
        => DalSettingsRecord.TryParse(await host.TryReadFileAsync(OctezPaths.OctezDeskDalSettingsFile, ct));

    /// <summary>
    /// The DAL settings as installed, e.g. to prefill the wizard so running it again doesn't turn the
    /// DAL node off; null if OctezDesk never configured one.
    /// </summary>
    public async Task<DalSettings?> GetInstalledSettingsAsync(CancellationToken ct = default)
    {
        if (!await IsInstalledAsync(ct) || await GetConfiguredSettingsAsync(ct) is not { } r) return null;
        return new DalSettings
        {
            Enabled = await IsEnabledAsync(ct) || await IsActiveAsync(ct),
            AttesterAddress = r.Attester,
            RpcPort = r.RpcPort,
            P2PPort = r.P2PPort,
        };
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "is-active", "--quiet", OctezPaths.DalNodeService], ct: ct)).Success;

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "is-enabled", "--quiet", OctezPaths.DalNodeService], ct: ct)).Success;

    public async Task EnableAndStartAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "enable", "--now", OctezPaths.DalNodeService], ct: ct)).EnsureSuccess("Starting the DAL node");

    public async Task RestartAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "restart", OctezPaths.DalNodeService], ct: ct)).EnsureSuccess("Restarting the DAL node");

    /// <summary>Streams the DAL node log until cancelled.</summary>
    public Task FollowLogAsync(Action<string> onLine, int initialLines = 500, CancellationToken ct = default)
        => host.FollowFileAsync(OctezPaths.DalLogFile, onLine, initialLines, ct);
}
