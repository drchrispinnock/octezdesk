using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Health;

namespace OctezDesk.Win.ViewModels;

/// <summary>Screen 6: node health, log, and the burger menu actions.</summary>
public partial class DashboardViewModel(MainViewModel main) : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _cts = new();
    private AppServices Services => main.Services;

    public LogBuffer Log { get; } = new();

    /// <summary>Null until the first check completes (grey dot).</summary>
    [ObservableProperty] private HealthLevel? _nodeLevel;
    [ObservableProperty] private string _nodeSummary = "Checking…";
    [ObservableProperty] private string _nodeDetails = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand), nameof(RestartCommand), nameof(ShutdownCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _busyText;

    public void Start()
    {
        KeepAliveService.EnsureRunning();
        _ = PollLoopAsync(_cts.Token);
        _ = FollowLogLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                KeepAliveService.EnsureRunning();
                Apply(await Services.Monitor.CheckAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                NodeLevel = HealthLevel.Red;
                NodeSummary = $"Could not check the node: {ex.Message}";
                NodeDetails = "";
            }
        }
        while (await WaitAsync(timer, ct));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private void Apply(NodeHealth health)
    {
        NodeLevel = health.Level;
        NodeSummary = health.Summary;
        var o = health.Observation;
        var parts = new List<string>();
        if (o.HeadLevel is { } level) parts.Add($"Level {level:N0}");
        if (o.HeadTimestamp is { } ts) parts.Add($"head {ts.ToLocalTime():g}");
        if (o.PeerCount is { } peers) parts.Add($"{peers} peers");
        if (o.Version is { } version) parts.Add($"Octez {version}");
        NodeDetails = string.Join(" · ", parts);
    }

    /// <summary>tail -F exits if the distro restarts, so keep re-attaching.</summary>
    private async Task FollowLogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Run(() => Services.Node.FollowLogAsync(Log.Append, ct: ct), ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Append($"(log unavailable: {ex.Message})"); }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool NotBusy() => !IsBusy;

    private async Task RunActionAsync(string text, Func<Task> action)
    {
        IsBusy = true;
        BusyText = text;
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "OctezDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task Upgrade() => RunActionAsync("Upgrading Octez…", async () =>
    {
        var before = await Services.Node.GetInstalledVersionAsync();
        await Services.Node.UpgradeAsync(Log.Append);
        var after = await Services.Node.GetInstalledVersionAsync();
        Log.Append(before == after ? $"Octez is already up to date ({after})." : $"Upgraded Octez {before} → {after}.");
    });

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task Restart() => RunActionAsync("Restarting the node…", () => Services.Node.RestartAsync());

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task Shutdown()
    {
        var answer = MessageBox.Show("Stop the Octez node?", "OctezDesk", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes
            ? RunActionAsync("Stopping the node…", () => Services.Node.StopAsync())
            : Task.CompletedTask;
    }

    /// <summary>Opt-in: start the keep-alive helper at Windows sign-in so the node survives reboots.</summary>
    public bool StartAtSignIn
    {
        get => KeepAliveService.IsAutostartRegistered();
        set
        {
            if (value) KeepAliveService.RegisterAutostart();
            else KeepAliveService.UnregisterAutostart();
            OnPropertyChanged();
        }
    }

    /// <summary>Closes the GUI only; the node keeps running.</summary>
    [RelayCommand]
    private static void Exit() => Application.Current.Shutdown();

    /// <summary>Not implemented yet: needs config update / data wipe handling (spec/Windows.md §2.5).</summary>
    [RelayCommand(CanExecute = nameof(CanReconfigure))]
    private void Reconfigure() { }

    private static bool CanReconfigure() => false;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
