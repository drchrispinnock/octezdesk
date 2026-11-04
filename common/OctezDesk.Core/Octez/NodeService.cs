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

    /// <summary>Upgrades the Octez packages from the tzinit repo and restarts the node.</summary>
    public async Task UpgradeAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        (await host.AptGetAsync(["update"], onLine, ct)).EnsureSuccess("apt-get update");
        (await host.AptGetAsync(["install", "--only-upgrade", .. OctezPaths.NodePackages], onLine, ct))
            .EnsureSuccess("Upgrading Octez");
        await RestartAsync(ct);
    }

    /// <summary>Streams the node log until cancelled.</summary>
    public Task FollowLogAsync(Action<string> onLine, int initialLines = 500, CancellationToken ct = default)
        => host.RunAsync(["tail", "-n", initialLines.ToString(), "-F", OctezPaths.NodeLogFile], onLine: onLine, ct: ct);
}
