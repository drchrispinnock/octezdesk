using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Octez;

/// <summary>Controls the packaged <c>octez-node</c> systemd unit and reads its configuration.</summary>
public sealed class NodeService(IOctezHost host)
{
    public async Task<string> GetDataDirAsync(CancellationToken ct = default)
    {
        var content = await host.TryReadFileAsync(OctezPaths.NodeDefaultsFile, ct);
        if (content is not null && ShellDefaultsFile.Parse(content).TryGetValue("DATADIR", out var dir) && dir.Length > 0)
            return dir;
        return OctezPaths.DefaultNodeDataDir;
    }

    public async Task<string> GetConfigFilePathAsync(CancellationToken ct = default)
        => (await GetDataDirAsync(ct)).TrimEnd('/') + "/config.json";

    public async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        var r = await host.RunAsync(["dpkg-query", "-W", "-f=${Status}", "octez-node"], ct: ct);
        return r.Success && r.StdOut.Contains("install ok installed", StringComparison.Ordinal);
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => await host.FileExistsAsync(await GetConfigFilePathAsync(ct), ct);

    /// <summary>True once the node has a store (from a snapshot import or from running).</summary>
    public async Task<bool> HasChainDataAsync(CancellationToken ct = default)
        => await host.DirectoryExistsAsync((await GetDataDirAsync(ct)).TrimEnd('/') + "/store", ct);

    /// <summary>The settings OctezDesk last configured the node with, or null if unknown.</summary>
    public async Task<NodeSettingsRecord?> GetConfiguredSettingsAsync(CancellationToken ct = default)
        => NodeSettingsRecord.TryParse(await host.TryReadFileAsync(OctezPaths.OctezDeskNodeSettingsFile, ct));

    public async Task<int> GetRpcPortAsync(CancellationToken ct = default)
    {
        var json = await host.TryReadFileAsync(await GetConfigFilePathAsync(ct), ct);
        return (json is null ? null : NodeConfigFile.TryGetRpcPort(json)) ?? NodeSettings.DefaultRpcPort;
    }

    /// <summary>
    /// Waits until the node answers <c>/version</c> on its RPC port (it opens it a few seconds after
    /// starting). Returns false if it doesn't within <paramref name="timeout"/>.
    /// </summary>
    public async Task<bool> WaitForRpcAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var port = await GetRpcPortAsync(ct);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var r = await host.RunAsync(["wget", "-q", "-O-", "-T", "3", "-t", "1", $"http://127.0.0.1:{port}/version"], ct: ct);
            if (r.Success) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "is-active", "--quiet", OctezPaths.NodeService], ct: ct)).Success;

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "is-enabled", "--quiet", OctezPaths.NodeService], ct: ct)).Success;

    public async Task EnableAndStartAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "enable", "--now", OctezPaths.NodeService], ct: ct)).EnsureSuccess("Starting the node");

    public async Task RestartAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "restart", OctezPaths.NodeService], ct: ct)).EnsureSuccess("Restarting the node");

    public async Task StopAsync(CancellationToken ct = default)
        => (await host.RunAsync(["systemctl", "stop", OctezPaths.NodeService], ct: ct)).EnsureSuccess("Stopping the node");

    public async Task<string?> GetInstalledVersionAsync(CancellationToken ct = default)
    {
        var r = await host.RunAsync(["dpkg-query", "-W", "-f=${Version}", "octez-node"], ct: ct);
        return r.Success ? r.StdOut.Trim() : null;
    }

    /// <summary>Upgrades the Octez packages from the tzinit repo and restarts the node (and the DAL node and baker, if running).</summary>
    public async Task UpgradeAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        (await host.AptGetAsync(["update"], onLine, ct)).EnsureSuccess("apt-get update");
        // --only-upgrade skips packages that aren't installed, such as the DAL node or baker when they're off.
        (await host.AptGetAsync(["install", "--only-upgrade", .. OctezPaths.NodePackages, OctezPaths.DalPackage, OctezPaths.BakerPackage], onLine, ct))
            .EnsureSuccess("Upgrading Octez");
        await RestartAsync(ct);
        await host.RunAsync(["systemctl", "try-restart", OctezPaths.DalNodeService, OctezPaths.BakerService], ct: ct);
    }

    /// <summary>Streams the node log until cancelled.</summary>
    public Task FollowLogAsync(Action<string> onLine, int initialLines = 500, CancellationToken ct = default)
        => host.FollowFileAsync(OctezPaths.NodeLogFile, onLine, initialLines, ct);

    /// <summary>The node's <c>rpc.listen-addrs</c> from its config.json, or null if there's no readable config.</summary>
    public async Task<IReadOnlyList<string>?> GetRpcListenAddrsAsync(CancellationToken ct = default)
    {
        var json = await host.TryReadFileAsync(await GetConfigFilePathAsync(ct), ct);
        return json is null ? null : NodeConfigFile.TryGetRpcListenAddrs(json);
    }
}
