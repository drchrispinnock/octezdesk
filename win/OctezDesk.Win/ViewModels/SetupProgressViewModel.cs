using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;

namespace OctezDesk.Win.ViewModels;

public partial class StepItem(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    private StepStatus _status = StepStatus.Pending;

    [ObservableProperty] private string? _error;

    public string Glyph => Status switch
    {
        StepStatus.Pending => "○",
        StepStatus.Checking or StepStatus.Running => "◔",
        StepStatus.AlreadyDone or StepStatus.Done => "✔",
        StepStatus.Failed => "✖",
        _ => "",
    };
}

/// <summary>Runs the provisioning steps and shows their progress and log.</summary>
public partial class SetupProgressViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly NodeSettings _settings;
    private readonly string _location;
    private readonly Provisioner _provisioner;

    public ObservableCollection<StepItem> Steps { get; }
    public LogBuffer Log { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(BackCommand), nameof(ContinueCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(BackCommand), nameof(ContinueCommand))]
    private bool _succeeded;

    [ObservableProperty] private string _status = "Setting up…";

    public SetupProgressViewModel(MainViewModel main, NodeSettings settings, string location)
    {
        _main = main;
        _settings = settings;
        _location = location;
        var s = main.Services;
        _provisioner = ProvisioningPlans.WindowsNode(s.Wsl, s.Host, location, settings);
        Steps = new(_provisioner.Steps.Select(step => new StepItem(step.Title)));
    }

    public async Task RunAsync()
    {
        IsRunning = true;
        Succeeded = false;
        Status = "Setting up… this can take a long time while the snapshot downloads.";
        foreach (var step in Steps) { step.Status = StepStatus.Pending; step.Error = null; }

        // Progress<T> captures the UI SynchronizationContext, so updates arrive on the UI thread.
        var progress = new Progress<StepUpdate>(u =>
        {
            Steps[u.Index].Status = u.Status;
            Steps[u.Index].Error = u.Error;
        });

        try
        {
            Succeeded = await Task.Run(() => _provisioner.RunAsync(progress, Log.Append));
            if (Succeeded) KeepAliveService.EnsureRunning();
            Status = Succeeded ? "Your node is set up and running." : "Setup stopped. Fix the problem and choose Retry.";
        }
        catch (Exception ex)
        {
            Log.Append($"!! {ex.Message}");
            Status = "Setup stopped unexpectedly. Choose Retry to continue from where it stopped.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanRetry() => !IsRunning && !Succeeded;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task Retry() => RunAsync();

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Back() => _main.ShowNodeSetup(_settings, _location);

    private bool CanContinue() => Succeeded;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue() => _main.ShowDashboard();
}
