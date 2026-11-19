using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;

namespace OctezDesk.Win.ViewModels;

public partial class StepItem(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(TitleWeight))]
    private StepStatus _status = StepStatus.Pending;

    [ObservableProperty] private string? _error;

    public string Glyph => Status switch
    {
        StepStatus.Pending => "○",
        StepStatus.Checking or StepStatus.Running => "●",
        StepStatus.AlreadyDone or StepStatus.Done => "✓",
        StepStatus.Failed => "✕",
        _ => "",
    };

    public FontWeight TitleWeight => Status == StepStatus.Running ? FontWeights.SemiBold : FontWeights.Normal;
}

/// <summary>Runs the provisioning steps and shows their progress and log.</summary>
public partial class SetupProgressViewModel : ObservableObject
{
    // wget --progress=dot:giga lines end like "...  42% 4.59M 2m16s"
    private static readonly Regex WgetPercent = new(@"\s(\d{1,3})%\s", RegexOptions.Compiled);

    private readonly MainViewModel _main;
    private readonly NodeSettings _settings;
    private readonly string _location;
    private readonly BakingKeyChoice? _keys;
    private readonly BakerSettings _baker;
    private readonly Provisioner _provisioner;
    private volatile bool _downloading;

    public ObservableCollection<StepItem> Steps { get; }
    public LogBuffer Log { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(BackCommand), nameof(CancelCommand), nameof(ContinueCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(BackCommand), nameof(CancelCommand), nameof(ContinueCommand))]
    private bool _succeeded;

    [ObservableProperty] private string _status = "Setting up…";
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _showDetails;

    public SetupProgressViewModel(
        MainViewModel main, NodeSettings settings, string location, BakingKeyChoice? keys, DalSettings dal, BakerSettings baker,
        FundingSettings? funding)
    {
        _main = main;
        _settings = settings;
        _location = location;
        _keys = keys;
        _baker = baker;
        var s = main.Services;
        _provisioner = ProvisioningPlans.WindowsNode(s.Wsl, s.Host, location, settings, keys, dal, baker, funding);
        Steps = new(_provisioner.Steps.Select(step => new StepItem(step.Title)));
    }

    public async Task RunAsync()
    {
        IsRunning = true;
        Succeeded = false;
        Status = "This can take a while. You can watch the details below.";
        foreach (var step in Steps) { step.Status = StepStatus.Pending; step.Error = null; }

        // Progress<T> captures the UI SynchronizationContext, so updates arrive on the UI thread.
        var progress = new Progress<StepUpdate>(u =>
        {
            Steps[u.Index].Status = u.Status;
            Steps[u.Index].Error = u.Error;
            if (u.Status != StepStatus.Running) return;

            _downloading = u.Step is ImportSnapshotStep;
            IsIndeterminate = true;
            DownloadPercent = 0;
            ActivityText = _downloading ? "Downloading snapshot…" : u.Step.Title + "…";
        });

        try
        {
            Succeeded = await Task.Run(() => _provisioner.RunAsync(progress, OnLogLine));
            if (Succeeded) KeepAliveService.EnsureRunning();
            Status = Succeeded
                ? "Your node is set up and running. It will keep running in the background."
                : "Setup stopped. Fix the problem shown below, then choose Retry to continue from where it stopped.";
        }
        catch (Exception ex)
        {
            Log.Append($"!! {ex.Message}");
            Status = "Setup stopped unexpectedly. Choose Retry to continue from where it stopped.";
        }
        finally
        {
            IsRunning = false;
            _downloading = false;
            if (!Succeeded) ShowDetails = true;
        }
    }

    /// <summary>Called from a background thread for every log line.</summary>
    private void OnLogLine(string line)
    {
        Log.Append(line);
        if (!_downloading) return;
        var m = WgetPercent.Match(line);
        if (!m.Success) return;
        var pct = int.Parse(m.Groups[1].Value);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsIndeterminate = false;
            DownloadPercent = pct;
            ActivityText = pct < 100 ? $"Downloading snapshot… {pct}%" : "Importing snapshot…";
            if (pct == 100) IsIndeterminate = true;
        });
    }

    private bool CanRetry() => !IsRunning && !Succeeded;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task Retry() => RunAsync();

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Back() => _main.ShowDalSetup(_settings, _location, _keys, _baker);

    /// <summary>Leaves a stopped setup: to the dashboard if a node is set up (e.g. the wizard was re-run), else to Welcome.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task Cancel() => _main.StartAsync();

    private bool CanContinue() => Succeeded;

    /// <summary>To the baking keys (public keys and proofs) if any were set up, else the dashboard.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task Continue()
    {
        if (_keys is null) _main.ShowDashboard();
        else await _main.ShowRecordedBakingKeysAsync();
    }
}
