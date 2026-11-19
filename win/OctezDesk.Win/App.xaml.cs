using System.Windows;
using OctezDesk.Core;
using OctezDesk.Core.Health;
using OctezDesk.Core.Octez;
using OctezDesk.Core.Provisioning;
using OctezDesk.Core.Wsl;
using OctezDesk.Win.ViewModels;

namespace OctezDesk.Win;

/// <summary>Shared services, created once in <see cref="App.OnStartup"/>.</summary>
public sealed class AppServices
{
    public WslManager Wsl { get; }
    public WslHost Host { get; }
    public NodeService Node { get; }
    public NodeMonitor Monitor { get; }
    public InstallationProbe Probe { get; }
    public BakingKeyService Keys { get; }
    /// <summary>The master key's on-chain state (balance, stake, delegate registration).</summary>
    public MasterAccount Account { get; }
    /// <summary>Updates OctezDesk itself (Velopack, from GitHub Releases).</summary>
    public AppUpdater Updater { get; } = new();
    public DalNodeService Dal { get; }
    public DalMonitor DalMonitor { get; }
    public BakerService Baker { get; }
    public BakerMonitor BakerMonitor { get; }
    /// <summary>Start, stop and restart from the dashboard's ☰ menu.</summary>
    public ServiceControl Control { get; }

    /// <param name="distroName">From <see cref="WslManager.FindDistroNameAsync"/>.</param>
    public AppServices(string distroName)
    {
        Wsl = new WslManager(distroName);
        Host = new WslHost(Wsl.DistroName);
        Node = new NodeService(Host);
        Monitor = new NodeMonitor(Host, Node);
        Probe = new InstallationProbe(Wsl, Node);
        var client = new OctezClient(Host);
        Keys = new BakingKeyService(Host, client);
        Account = new MasterAccount(Host, Node, client);
        Dal = new DalNodeService(Host);
        DalMonitor = new DalMonitor(Dal);
        Baker = new BakerService(Host);
        BakerMonitor = new BakerMonitor(Baker);
        Control = new ServiceControl(Host);
    }
}

/// <summary>
/// The GUI only configures and monitors. The node runs under systemd in WSL and is kept alive by
/// <see cref="KeepAliveService"/>, so exiting the GUI leaves it running.
/// </summary>
public partial class App : Application
{
    /// <summary>Developer switch: start in the setup wizard even if the node is already set up.</summary>
    public const string WizardSwitch = "--wizard";

    private AppServices? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow { DataContext = new { CurrentPage = "Checking installation…" } };
        MainWindow.Show();
        string distroName;
        try { distroName = await Task.Run(() => WslManager.FindDistroNameAsync()); }
        catch (Exception ex)
        {
            MainWindow.DataContext = new { CurrentPage = $"Could not check the installation: {ex.Message}" };
            return;
        }
        _services = new AppServices(distroName);
        var main = new MainViewModel(_services);
        MainWindow.DataContext = main;
        await main.StartAsync(forceWizard: e.Args.Contains(WizardSwitch));
    }

    /// <summary>An update downloaded while the window was open is installed once it closes.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Updater.ApplyOnExit();
        base.OnExit(e);
    }
}
