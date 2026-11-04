using CommunityToolkit.Mvvm.ComponentModel;
using OctezDesk.Core;
using OctezDesk.Core.Octez;

namespace OctezDesk.Win.ViewModels;

/// <summary>Owns navigation between the wizard pages and the main (dashboard) screen.</summary>
public partial class MainViewModel(AppServices services) : ObservableObject
{
    public AppServices Services { get; } = services;

    [ObservableProperty]
    private object? _currentPage = "Checking installation…";

    public async Task StartAsync(bool forceWizard = false)
    {
        try
        {
            var state = await Services.Probe.ProbeAsync();
            if (state == InstallationState.Ready && !forceWizard) ShowDashboard();
            else ShowWelcome();
        }
        catch (Exception ex)
        {
            CurrentPage = $"Could not check the installation: {ex.Message}";
        }
    }

    public void ShowWelcome() => Navigate(new WelcomeViewModel(this));

    public void ShowNodeSetup(NodeSettings? settings = null, string? location = null)
        => Navigate(new NodeSetupViewModel(this, settings ?? new NodeSettings(), location));

    public void ShowSetupProgress(NodeSettings settings, string location)
    {
        var page = new SetupProgressViewModel(this, settings, location);
        Navigate(page);
        _ = page.RunAsync();
    }

    public void ShowDashboard()
    {
        var page = new DashboardViewModel(this);
        Navigate(page);
        page.Start();
    }

    private void Navigate(object page)
    {
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = page;
    }
}
