using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Health;
using OctezDesk.Core.Octez;

namespace OctezDesk.Win.ViewModels;

/// <summary>Screen 6: node health, log, and the burger menu actions.</summary>
public partial class DashboardViewModel(MainViewModel main) : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Node log lines hidden unless "Show routine messages" is on: per-peer block fetching, and the
    /// indented continuation lines of harmless event-loop errors.
    /// </summary>
    private static readonly Regex RoutineLogLine = new(
        @"fetching branch of|fetching operations|Failed to send event|^\s", RegexOptions.Compiled);

    private readonly CancellationTokenSource _cts = new();
    private AppServices Services => main.Services;

    /// <summary>The log panel's tabs: node, baker, DAL node. Each follows its own log file.</summary>
    public IReadOnlyList<LogTabViewModel> LogTabs { get; } =
    [
        new("Node", (onLine, ct) => main.Services.Node.FollowLogAsync(onLine, ct: ct), RoutineLogLine),
        new("Baker", (onLine, ct) => main.Services.Baker.FollowLogAsync(onLine, ct: ct)),
        new("DAL node", (onLine, ct) => main.Services.Dal.FollowLogAsync(onLine, ct: ct)),
    ];

    private LogTabViewModel NodeLog => LogTabs[0];

    [ObservableProperty] private LogTabViewModel? _selectedLogTab;

    /// <summary>Null until the first check completes (grey dot).</summary>
    [ObservableProperty] private HealthLevel? _nodeLevel;
    [ObservableProperty] private string _nodeSummary = "Checking…";
    [ObservableProperty] private string _nodeDetails = "";

    /// <summary>e.g. "Shadownet · rolling", from what OctezDesk configured.</summary>
    [ObservableProperty] private string? _networkLabel;


    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand), nameof(RestartCommand), nameof(ShutdownCommand), nameof(SetupWizardCommand), nameof(CheckForUpdatesCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _busyText;

    public void Start()
    {
        KeepAliveService.EnsureRunning();
        _ = LoadNetworkLabelAsync();
        _ = PollLoopAsync(_cts.Token);
        SelectedLogTab = NodeLog;
        foreach (var tab in LogTabs) tab.Start(_cts.Token);
        _ = UpdateLoopAsync(_cts.Token);
    }

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);

    /// <summary>e.g. "OctezDesk 0.2.0", shown in the footer.</summary>
    public string AppVersionText => $"OctezDesk {Services.Updater.CurrentVersion}" + (Services.Updater.IsInstalled ? "" : " (development build: no updates)");

    /// <summary>Set once an update is downloaded: "OctezDesk 0.3.0 is ready to install."</summary>
    [ObservableProperty] private string? _updateReadyText;

    /// <summary>Checks at start and every few hours; downloads in the background. Closing the window installs it.</summary>
    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        if (!Services.Updater.IsInstalled) return;
        using var timer = new PeriodicTimer(UpdateInterval);
        do
        {
            try { ShowUpdate(await Services.Updater.CheckAndDownloadAsync(ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { } // offline, rate-limited…: try again later
        }
        while (await WaitAsync(timer, ct));
    }

    private void ShowUpdate(string? version)
        => UpdateReadyText = version is null ? null
            : $"OctezDesk {version} is ready to install. Restart now, or it installs when you close OctezDesk. The node keeps running.";

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task CheckForUpdates() => RunActionAsync("Checking for updates…", async () =>
    {
        if (!Services.Updater.IsInstalled)
        {
            ShowMessage("This copy of OctezDesk wasn't installed with Setup.exe, so it doesn't update itself.");
            return;
        }
        var version = await Services.Updater.CheckAndDownloadAsync();
        ShowUpdate(version);
        if (version is null) ShowMessage($"OctezDesk {Services.Updater.CurrentVersion} is the latest version.");
    });

    private static void ShowMessage(string text)
        => Application.Current.Dispatcher.Invoke(() => MessageBox.Show(text, "OctezDesk", MessageBoxButton.OK, MessageBoxImage.Information));

    /// <summary>Restarts OctezDesk into the downloaded version.</summary>
    [RelayCommand]
    private void RestartToUpdate() => Services.Updater.ApplyAndRestart();

    private async Task LoadNetworkLabelAsync()
    {
        try
        {
            if (await Services.Node.GetConfiguredSettingsAsync(_cts.Token) is { } s)
            {
                var network = TezosNetwork.All.FirstOrDefault(n => n.Id == s.Network)?.DisplayName ?? s.Network;
                NetworkLabel = $"{network} · {s.HistoryMode}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
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
                ApplyDal(await Services.DalMonitor.CheckAsync(ct));
                ApplyBaker(await Services.BakerMonitor.CheckAsync(ct));
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

    [ObservableProperty] private HealthLevel? _dalLevel;
    [ObservableProperty] private string _dalSummary = "Checking…";
    [ObservableProperty] private string _dalDetails = "";

    private void ApplyDal(ServiceHealth health)
    {
        DalLevel = health.Level;
        DalSummary = health.Summary;
        DalDetails = health.Details;
    }

    [ObservableProperty] private HealthLevel? _bakerLevel;
    [ObservableProperty] private string _bakerSummary = "Checking…";
    [ObservableProperty] private string _bakerDetails = "";

    private void ApplyBaker(ServiceHealth health)
    {
        BakerLevel = health.Level;
        BakerSummary = health.Summary;
        BakerDetails = health.Details;
    }

    private void Apply(NodeHealth health)
    {
        NodeLevel = health.Level;
        NodeSummary = health.Summary;
        var o = health.Observation;
        var parts = new List<string>();
        if (o.HeadLevel is { } level) parts.Add($"Level {level:N0}");
        if (o.HeadTimestamp is { } ts)
            parts.Add($"last block {HealthEvaluator.Describe(DateTimeOffset.UtcNow - ts)} ago");
        if (o.PeerCount is { } peers) parts.Add($"{peers} peers");
        if (o.Version is { } version) parts.Add($"Octez {version}");
        NodeDetails = string.Join(" · ", parts);
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
        await Services.Node.UpgradeAsync(NodeLog.Log.Append);
        var after = await Services.Node.GetInstalledVersionAsync();
        NodeLog.Log.Append(before == after ? $"Octez is already up to date ({after})." : $"Upgraded Octez {before} → {after}.");
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

    [RelayCommand]
    private Task BakingKeys() => main.ShowRecordedBakingKeysAsync();

    /// <summary>Runs the wizard again, e.g. to add baking keys or a DAL node. Its steps skip what's already done.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void SetupWizard() => main.ShowWelcome();

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
