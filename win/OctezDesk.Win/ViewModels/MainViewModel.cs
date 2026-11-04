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

    // Wizard answers kept while the user moves Back and forward through screens 2 to 5.
    /// <summary>Null: don't bake.</summary>
    private BakingKeyChoice? _wizardKeys;
    private DalSettings? _wizardDal;
    /// <summary>Whether the user was baking when <see cref="_wizardDal"/> was chosen. If that changes, the DAL page resets to its defaults.</summary>
    private bool _wizardDalBaking;
    private BakerSettings? _wizardBaker;
    /// <summary>The Funds screen's choice; null until it's shown (setup then uses the defaults).</summary>
    private FundingSettings? _wizardFunding;

    /// <param name="keys">The baking keys choice to keep when coming Back from that screen; null (from Welcome) starts afresh.</param>
    public void ShowNodeSetup(NodeSettings? settings = null, string? location = null, BakingKeyChoice? keys = null)
    {
        _wizardKeys = keys;
        if (settings is null) { _wizardDal = null; _wizardBaker = null; _wizardFunding = null; } // from Welcome: start afresh
        Navigate(new NodeSetupViewModel(this, settings ?? new NodeSettings(), location));
    }

    public void RememberBaker(BakerSettings baker) => _wizardBaker = baker;

    /// <summary>
    /// From Welcome: the Node screen, prefilled with what's installed when the wizard is run again, so
    /// that pressing Next throughout keeps the node, keys and DAL node as they are.
    /// </summary>
    public async Task ShowNodeSetupFromWelcomeAsync()
    {
        NodeSettings? settings = null;
        BakingKeyChoice? keys = null;
        DalSettings? dal = null;
        BakerSettings? baker = null;
        try
        {
            (settings, keys, dal, baker) = await Task.Run(async () =>
            {
                var s = (await Services.Node.GetConfiguredSettingsAsync())?.ToSettings();
                if (s is not null) s = s with { ImportSnapshot = !await Services.Node.HasChainDataAsync() };
                return (s, await Services.Keys.GetRecordedChoiceAsync(), await Services.Dal.GetInstalledSettingsAsync(),
                        await Services.Baker.GetInstalledSettingsAsync());
            });
        }
        catch (Exception) { } // no distro yet, or unreadable: start from the defaults

        ShowNodeSetup(settings, keys: keys);
        RememberDal(dal, baking: keys is not null);
        if (baker is not null) RememberBaker(baker);
    }

    /// <summary>Wizard screen 3 (Baker), between Node and DAL node.</summary>
    public void ShowBakingKeys(NodeSettings settings, string location)
        => Navigate(new BakingKeysViewModel(this, new WizardContext(settings, location), _wizardKeys, _wizardBaker));

    public void RememberFunding(FundingSettings funding) => _wizardFunding = funding;

    /// <summary>After Baker: the Funds screen when it applies (a test network and a master key), else the DAL node screen.</summary>
    public void ShowAfterBakingKeys(NodeSettings settings, string location, BakingKeyChoice? keys, BakerSettings baker)
    {
        if (!FundingSettings.AppliesTo(settings.Network, keys))
        {
            ShowDalSetup(settings, location, keys, baker);
            return;
        }
        _wizardKeys = keys;
        _wizardBaker = baker;
        Navigate(new FundingViewModel(this, new WizardContext(settings, location), keys!, baker, _wizardFunding));
    }

    /// <summary>Back from the DAL node screen: to Funds when it applies, else to Baker.</summary>
    public void ShowBeforeDal(NodeSettings settings, string location, BakingKeyChoice? keys, BakerSettings baker)
    {
        if (FundingSettings.AppliesTo(settings.Network, keys)) ShowAfterBakingKeys(settings, location, keys, baker);
        else ShowBakingKeys(settings, location);
    }

    /// <summary>Wizard screen after Baker (and Funds), before setup.</summary>
    public void ShowDalSetup(NodeSettings settings, string location, BakingKeyChoice? keys, BakerSettings baker)
    {
        _wizardKeys = keys;
        _wizardBaker = baker;
        var previous = _wizardDal is not null && _wizardDalBaking == (keys is not null) ? _wizardDal : null;
        Navigate(new DalSetupViewModel(this, new WizardContext(settings, location), keys, baker, previous));
    }

    public void RememberDal(DalSettings? dal, bool baking)
    {
        _wizardDal = dal;
        _wizardDalBaking = baking;
    }

    public void ShowSetupProgress(NodeSettings settings, string location, BakingKeyChoice? keys, DalSettings dal, BakerSettings baker)
    {
        _wizardKeys = keys;
        _wizardBaker = baker;
        RememberDal(dal, keys is not null);
        var funding = FundingSettings.AppliesTo(settings.Network, keys) ? _wizardFunding ?? new FundingSettings() : null;
        var page = new SetupProgressViewModel(this, settings, location, keys, dal, baker, funding);
        Navigate(page);
        _ = page.RunAsync();
    }

    /// <summary>
    /// From the dashboard: shows the keys OctezDesk set up, or the choice screen (generating directly,
    /// since Octez is installed) if there are none yet.
    /// </summary>
    public async Task ShowRecordedBakingKeysAsync()
    {
        IReadOnlyList<BakingKey> keys;
        try { keys = await Task.Run(() => Services.Keys.GetKeysAsync()); }
        catch (Exception ex) { keys = []; System.Windows.MessageBox.Show($"Could not read the baking keys: {ex.Message}", "OctezDesk"); }
        if (keys.Count > 0) ShowBakingKeysResult(keys);
        else Navigate(new BakingKeysViewModel(this, wizard: null, choice: null));
    }

    public void ShowBakingKeysResult(IReadOnlyList<BakingKey> keys) => Navigate(new BakingKeysResultViewModel(this, keys));

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
