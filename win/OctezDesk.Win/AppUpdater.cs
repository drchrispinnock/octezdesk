using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace OctezDesk.Win;

/// <summary>
/// Updates OctezDesk itself through Velopack. Releases are on the public GitHub repo; set
/// <c>OCTEZDESK_UPDATE_URL</c> to a folder or URL holding a <c>vpk pack</c> output to test
/// against a local feed instead. A copy run from a build folder (not installed by Setup.exe)
/// never updates.
/// </summary>
/// <remarks>
/// Updating replaces the exe the keep-alive helper runs from, so the helper is stopped first (see
/// <see cref="Program"/>'s Velopack hooks), and Velopack kills anything still running from the app
/// folder afterwards. The helper comes back when Velopack restarts the app: as the GUI ("Restart
/// now", whose dashboard starts the helper) or straight into helper mode (update on close). The
/// node only stops if that takes longer than WSL's ~20 s idle timeout; an update took about 7 s
/// on the dev machine.
/// </remarks>
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/drchrispinnock/octezdesk";
    public const string FeedOverrideVariable = "OCTEZDESK_UPDATE_URL";

    private readonly UpdateManager _manager;
    private VelopackAsset? _ready;

    public AppUpdater()
    {
        var feed = Environment.GetEnvironmentVariable(FeedOverrideVariable);
        _manager = string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(RepoUrl, null, false))
            : new UpdateManager(feed);
    }

    /// <summary>False when running from a build folder: there is nothing to update.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>e.g. "0.2.0": the installed package's version, or the assembly's in a dev build.</summary>
    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";

    /// <summary>The downloaded update waiting to be applied, if any.</summary>
    public string? ReadyVersion => (_ready ?? _manager.UpdatePendingRestart)?.Version.ToString();

    /// <summary>
    /// Checks for a newer release and downloads it (deltas when possible). Returns its version, or
    /// null if there is none or this copy isn't installed.
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return null;
        if (_manager.UpdatePendingRestart is { } pending) return (_ready = pending).Version.ToString();
        var update = await _manager.CheckForUpdatesAsync();
        if (update is null) return null;
        await _manager.DownloadUpdatesAsync(update, cancelToken: ct);
        _ready = update.TargetFullRelease;
        return _ready.Version.ToString();
    }

    /// <summary>Restarts into the downloaded update now. The keep-alive helper is stopped first; the new version starts it again.</summary>
    public void ApplyAndRestart()
    {
        if ((_ready ?? _manager.UpdatePendingRestart) is not { } asset) return;
        KeepAliveService.StopAndWait(TimeSpan.FromSeconds(10));
        _manager.ApplyUpdatesAndRestart(asset);
    }

    /// <summary>
    /// When the GUI closes with an update downloaded: apply it silently once this process has
    /// exited, then restart the new version as the windowless keep-alive helper (not the window),
    /// so the node keeps running. That's what makes updates automatic.
    /// </summary>
    public void ApplyOnExit()
    {
        if ((_ready ?? _manager.UpdatePendingRestart) is not { } asset) return;
        try { _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: true, restartArgs: [KeepAliveService.Switch]); }
        catch (Exception) { } // best effort; the next start applies it instead
    }
}
